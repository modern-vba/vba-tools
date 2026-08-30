using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace VbaDebugAdapter.Infrastructure;

public sealed class VbaDebugSessionWorkspaceManager : IVbaDebugSessionWorkspaceManager
{
    private static readonly JsonSerializerOptions LeaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Lazy<WorkspaceContext> workspaceContext;

    public VbaDebugSessionWorkspaceManager(string workspaceRoot)
        : this(new VbaDebugWorkspaceRootBinding(workspaceRoot), cleanupOperations: null)
    {
    }

    internal VbaDebugSessionWorkspaceManager(
        string workspaceRoot,
        IVbaDebugWorkspaceCleanupOperations? cleanupOperations,
        Action<string>? beforeCreateLeaseFile = null,
        Action<string>? afterCreateDirectoryBeforeOpen = null,
        Action<string>? beforeDeleteOwnedTree = null,
        Action<string>? beforeCreateSourceFile = null,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer = null)
        : this(
            new VbaDebugWorkspaceRootBinding(workspaceRoot),
            cleanupOperations,
            beforeCreateLeaseFile,
            afterCreateDirectoryBeforeOpen,
            beforeDeleteOwnedTree,
            beforeCreateSourceFile,
            afterCreateSourceFileBeforeOwnershipTransfer)
    {
    }

    internal VbaDebugSessionWorkspaceManager(
        VbaDebugWorkspaceRootBinding workspaceRootBinding,
        IVbaDebugWorkspaceCleanupOperations? cleanupOperations,
        Action<string>? beforeCreateLeaseFile = null,
        Action<string>? afterCreateDirectoryBeforeOpen = null,
        Action<string>? beforeDeleteOwnedTree = null,
        Action<string>? beforeCreateSourceFile = null,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceRootBinding);
        workspaceContext = new Lazy<WorkspaceContext>(
            () =>
            {
                var creator = new WindowsVbaDebugWorkspaceCreator(
                    workspaceRootBinding.Resolve(),
                    afterCreateDirectoryBeforeOpen: afterCreateDirectoryBeforeOpen,
                    beforeDeleteOwnedTree: beforeDeleteOwnedTree,
                    beforeCreateLeaseFile: beforeCreateLeaseFile,
                    beforeCreateSourceFile: beforeCreateSourceFile,
                    afterCreateSourceFileBeforeOwnershipTransfer:
                        afterCreateSourceFileBeforeOwnershipTransfer);
                return new WorkspaceContext(
                    creator.WorkspaceRoot,
                    creator,
                    cleanupOperations ?? new SystemVbaDebugWorkspaceCleanupOperations(
                        creator.WorkspaceRoot));
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
        DebugSessionId sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        cancellationToken.ThrowIfCancellationRequested();
        var sessionWorkspacePath = Path.Combine(
            WorkspaceRoot,
            "workspaces",
            sessionId.Value);
        IVbaDebugSessionWorkspaceCreationScope? creationScope = null;
        FileStream? leaseStream = null;
        try
        {
            creationScope = WorkspaceCreator.ClaimSession(sessionId);
            sessionWorkspacePath = creationScope.SessionWorkspacePath;
            leaseStream = creationScope.CreateLeaseStream();
            using var process = Process.GetCurrentProcess();
            var metadata = new VbaDebugSessionWorkspaceLeaseMetadata(
                1,
                sessionId.Value,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                process.Id,
                process.StartTime.ToUniversalTime().ToString("O"));
            await JsonSerializer.SerializeAsync(
                leaseStream,
                metadata,
                LeaseJsonOptions,
                cancellationToken).ConfigureAwait(false);
            await leaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            return new OwnedVbaDebugSessionWorkspaceLease(
                this,
                sessionId,
                sessionWorkspacePath,
                leaseStream,
                creationScope);
        }
        catch
        {
            if (leaseStream is not null)
            {
                try
                {
                    await leaseStream.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The claim failure remains authoritative.
                }
            }
            if (creationScope is not null)
            {
                try
                {
                    creationScope.DeleteOwnedTree();
                }
                catch
                {
                    // The claim failure remains authoritative.
                }
                finally
                {
                    creationScope.Dispose();
                }
            }
            throw;
        }
    }

    public async ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
        DebugSessionId sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        cancellationToken.ThrowIfCancellationRequested();
        var sessionWorkspacePath = Path.Combine(
            WorkspaceRoot,
            "workspaces",
            sessionId.Value);
        if (!WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(
                sessionWorkspacePath))
        {
            return new VbaDebugSessionCleanupResult(true, null, null);
        }

        try
        {
            if (HasReparseBoundary(sessionWorkspacePath))
            {
                return new VbaDebugSessionCleanupResult(
                    false,
                    Path.GetFullPath(sessionWorkspacePath),
                    "The VBA debug session workspace boundary could not be verified as safe.");
            }
        }
        catch
        {
            return new VbaDebugSessionCleanupResult(
                false,
                Path.GetFullPath(sessionWorkspacePath),
                "The VBA debug session workspace boundary could not be verified as safe.");
        }

        var preliminaryLeaseState = InspectLease(
            () => CleanupOperations.OpenSessionLeaseStream(sessionId),
            sessionId);
        if (preliminaryLeaseState == VbaDebugSessionLeaseState.Active)
        {
            return new VbaDebugSessionCleanupResult(
                false,
                Path.GetFullPath(sessionWorkspacePath),
                "The VBA debug session workspace lease is still active.");
        }

        IVbaDebugWorkspaceCleanupScope cleanupScope;
        try
        {
            cleanupScope = CleanupOperations.OpenSessionCleanupScope(sessionId);
        }
        catch
        {
            return new VbaDebugSessionCleanupResult(
                false,
                Path.GetFullPath(sessionWorkspacePath),
                "The VBA debug session workspace boundary could not be verified as safe.");
        }

        using (cleanupScope)
        {
            var leaseState = InspectLease(
                cleanupScope.OpenLeaseStream,
                sessionId);
            if (leaseState != VbaDebugSessionLeaseState.Stale)
            {
                var stateDescription = leaseState == VbaDebugSessionLeaseState.Active
                    ? "is still active"
                    : "could not be verified as stale";
                return new VbaDebugSessionCleanupResult(
                    false,
                    Path.GetFullPath(sessionWorkspacePath),
                    $"The VBA debug session workspace lease {stateDescription}.");
            }

            if (!await TryDeleteWithRetryAsync(
                    cleanupScope.DeleteDirectory,
                    cancellationToken).ConfigureAwait(false))
            {
                return new VbaDebugSessionCleanupResult(
                    false,
                    Path.GetFullPath(sessionWorkspacePath),
                    "The stale VBA debug session workspace could not be deleted within five seconds.");
            }
        }
        return new VbaDebugSessionCleanupResult(true, null, null);
    }

    public async ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
        DebugSessionId excludedSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(excludedSessionId);

        cancellationToken.ThrowIfCancellationRequested();
        var workspacesPath = Path.Combine(WorkspaceRoot, "workspaces");
        if (!WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(workspacesPath))
        {
            return [];
        }
        if (HasReparseBoundary(workspacesPath))
        {
            throw new InvalidOperationException(
                "The VBA debug workspace root crosses an unproved reparse boundary.");
        }

        var retained = new List<VbaDebugSessionCleanupResult>();
        foreach (var sessionWorkspacePath in Directory.EnumerateFileSystemEntries(
                     workspacesPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionIdValue = Path.GetFileName(sessionWorkspacePath);
            if (!DebugSessionId.TryParse(sessionIdValue, out var sessionId) ||
                sessionId == excludedSessionId)
            {
                continue;
            }
            var cleanup = await CleanupAsync(
                sessionId!,
                cancellationToken).ConfigureAwait(false);
            if (!cleanup.Succeeded)
            {
                retained.Add(cleanup);
            }
        }
        return retained;
    }

    private async ValueTask<bool> TryDeleteWithRetryAsync(
        Action deleteDirectory,
        CancellationToken cancellationToken)
    {
        var started = CleanupOperations.GetTimestamp();
        var retryDelay = TimeSpan.FromMilliseconds(100);
        var timeout = TimeSpan.FromSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                deleteDirectory();
                return true;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                var elapsed = CleanupOperations.GetElapsedTime(started);
                if (elapsed >= timeout)
                {
                    return false;
                }
                var remaining = timeout - elapsed;
                await CleanupOperations.DelayAsync(
                    remaining < retryDelay ? remaining : retryDelay,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool HasReparseBoundary(string sessionWorkspacePath)
    {
        var workspacesPath = Path.Combine(WorkspaceRoot, "workspaces");
        foreach (var directoryPath in new[]
                 {
                     WorkspaceRoot,
                     workspacesPath,
                     sessionWorkspacePath
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(directoryPath) &&
                CleanupOperations.IsReparsePoint(directoryPath))
            {
                return true;
            }
        }
        return false;
    }

    private static VbaDebugSessionLeaseState InspectLease(
        Func<Stream> openLeaseStream,
        DebugSessionId expectedSessionId)
    {
        try
        {
            using var leaseStream = openLeaseStream();
            using var leaseDocument = JsonDocument.Parse(leaseStream);
            var root = leaseDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return VbaDebugSessionLeaseState.Unverified;
            }

            var properties = root.EnumerateObject().ToArray();
            string[] expectedProperties =
            [
                "schemaVersion",
                "sessionId",
                "leaseId",
                "processId",
                "processStartTimeUtc"
            ];
            if (properties.Length != expectedProperties.Length ||
                properties.Any(property => !expectedProperties.Contains(
                    property.Name,
                    StringComparer.Ordinal)) ||
                !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var schema) ||
                schema != 1 ||
                !root.TryGetProperty("sessionId", out var sessionId) ||
                sessionId.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    sessionId.GetString(),
                    expectedSessionId.Value,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("leaseId", out var leaseId) ||
                leaseId.ValueKind != JsonValueKind.String ||
                !IsCanonicalHex32(leaseId.GetString()!) ||
                !root.TryGetProperty("processId", out var processId) ||
                !processId.TryGetInt32(out var pid) ||
                pid <= 0 ||
                !root.TryGetProperty("processStartTimeUtc", out var processStartTime) ||
                processStartTime.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParseExact(
                    processStartTime.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var leasedStartTime) ||
                leasedStartTime.Offset != TimeSpan.Zero)
            {
                return VbaDebugSessionLeaseState.Unverified;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return VbaDebugSessionLeaseState.Stale;
                }
                return process.StartTime.ToUniversalTime().Ticks == leasedStartTime.UtcTicks
                    ? VbaDebugSessionLeaseState.Active
                    : VbaDebugSessionLeaseState.Stale;
            }
            catch (ArgumentException)
            {
                return VbaDebugSessionLeaseState.Stale;
            }
            catch
            {
                return VbaDebugSessionLeaseState.Unverified;
            }
        }
        catch
        {
            return VbaDebugSessionLeaseState.Unverified;
        }
    }

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class OwnedVbaDebugSessionWorkspaceLease(
        VbaDebugSessionWorkspaceManager owner,
        DebugSessionId sessionId,
        string sessionWorkspacePath,
        FileStream leaseStream,
        IVbaDebugSessionWorkspaceCreationScope creationScope)
        : IVbaDebugSessionWorkspaceLease
    {
        private readonly HashSet<DebugGenerationId> claimedGenerations = [];
        private readonly object gate = new();
        private int disposed;

        public DebugSessionId SessionId { get; } = sessionId;

        public string SessionWorkspacePath { get; } = sessionWorkspacePath;

        public IVbaDebugGenerationWorkspace CreateGenerationWorkspace(
            DebugGenerationId generationId,
            string workbookFileName)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                if (claimedGenerations.Contains(generationId))
                {
                    throw new InvalidOperationException(
                        $"Debug generation {generationId} already exists for this lease and cannot be claimed again.");
                }
                var generationWorkspace = creationScope.CreateGenerationWorkspace(
                    generationId,
                    workbookFileName);
                claimedGenerations.Add(generationId);
                return generationWorkspace;
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed != 0)
                {
                    return;
                }
                disposed = 1;
            }

            try
            {
                await leaseStream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (!await owner.TryDeleteWithRetryAsync(
                            creationScope.DeleteOwnedTree,
                            CancellationToken.None).ConfigureAwait(false))
                    {
                        throw new IOException(
                            $"The VBA debug session workspace could not be deleted within five seconds: {SessionWorkspacePath}");
                    }
                }
                finally
                {
                    creationScope.Dispose();
                }
            }
        }
    }

    private sealed record VbaDebugSessionWorkspaceLeaseMetadata(
        int SchemaVersion,
        string SessionId,
        string LeaseId,
        int ProcessId,
        string ProcessStartTimeUtc);

    private string WorkspaceRoot => workspaceContext.Value.WorkspaceRoot;

    private WindowsVbaDebugWorkspaceCreator WorkspaceCreator
        => workspaceContext.Value.Creator;

    private IVbaDebugWorkspaceCleanupOperations CleanupOperations
        => workspaceContext.Value.CleanupOperations;

    private sealed record WorkspaceContext(
        string WorkspaceRoot,
        WindowsVbaDebugWorkspaceCreator Creator,
        IVbaDebugWorkspaceCleanupOperations CleanupOperations);

    private enum VbaDebugSessionLeaseState
    {
        Active,
        Stale,
        Unverified
    }
}

public interface IVbaDebugSessionWorkspaceManager
{
    ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(
        DebugSessionId sessionId,
        CancellationToken cancellationToken);

    ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(
        DebugSessionId sessionId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
        DebugSessionId excludedSessionId,
        CancellationToken cancellationToken);
}

public interface IVbaDebugSessionWorkspaceLease : IAsyncDisposable
{
    DebugSessionId SessionId { get; }

    string SessionWorkspacePath { get; }

    IVbaDebugGenerationWorkspace CreateGenerationWorkspace(
        DebugGenerationId generationId,
        string workbookFileName);
}

public sealed record VbaDebugSessionCleanupResult(
    bool Succeeded,
    string? RetainedPath,
    string? Message);

internal interface IVbaDebugWorkspaceCleanupOperations
{
    bool IsReparsePoint(string directoryPath);

    Stream OpenSessionLeaseStream(DebugSessionId sessionId);

    IVbaDebugWorkspaceCleanupScope OpenSessionCleanupScope(
        DebugSessionId sessionId);

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

}

internal interface IVbaDebugWorkspaceCleanupScope : IDisposable
{
    Stream OpenLeaseStream();

    void DeleteDirectory();
}

internal sealed class SystemVbaDebugWorkspaceCleanupOperations
    : IVbaDebugWorkspaceCleanupOperations
{
    private readonly WindowsVbaDebugWorkspaceTreeDeleter treeDeleter;

    public SystemVbaDebugWorkspaceCleanupOperations(
        string workspaceRoot,
        Action? beforeOpenScope = null,
        Action<string>? beforeDelete = null,
        Action<string>? beforeOpenEntry = null)
    {
        treeDeleter = new WindowsVbaDebugWorkspaceTreeDeleter(
            workspaceRoot,
            beforeOpenScope,
            beforeDelete,
            beforeOpenEntry);
    }

    public bool IsReparsePoint(string directoryPath)
        => (File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0;

    public Stream OpenSessionLeaseStream(DebugSessionId sessionId)
        => treeDeleter.OpenSessionLeaseStream(sessionId);

    public IVbaDebugWorkspaceCleanupScope OpenSessionCleanupScope(
        DebugSessionId sessionId)
        => treeDeleter.OpenSessionScope(sessionId);

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp)
        => Stopwatch.GetElapsedTime(startingTimestamp);

    public async ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
        => await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

}
