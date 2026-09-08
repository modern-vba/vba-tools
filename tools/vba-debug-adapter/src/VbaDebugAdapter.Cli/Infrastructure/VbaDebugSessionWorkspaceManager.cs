using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer = null,
        Action<SafeFileHandle, string>? releaseOwnedHandle = null)
        : this(
            new VbaDebugWorkspaceRootBinding(workspaceRoot),
            cleanupOperations,
            beforeCreateLeaseFile,
            afterCreateDirectoryBeforeOpen,
            beforeDeleteOwnedTree,
            beforeCreateSourceFile,
            afterCreateSourceFileBeforeOwnershipTransfer,
            releaseOwnedHandle)
    {
    }

    internal VbaDebugSessionWorkspaceManager(
        VbaDebugWorkspaceRootBinding workspaceRootBinding,
        IVbaDebugWorkspaceCleanupOperations? cleanupOperations,
        Action<string>? beforeCreateLeaseFile = null,
        Action<string>? afterCreateDirectoryBeforeOpen = null,
        Action<string>? beforeDeleteOwnedTree = null,
        Action<string>? beforeCreateSourceFile = null,
        Action<string>? afterCreateSourceFileBeforeOwnershipTransfer = null,
        Action<SafeFileHandle, string>? releaseOwnedHandle = null)
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
                        afterCreateSourceFileBeforeOwnershipTransfer,
                    releaseOwnedHandle: releaseOwnedHandle);
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
        var acquisition = new DebugFailureCompletion();
        try
        {
            creationScope = WorkspaceCreator.ClaimSession(sessionId);
            sessionWorkspacePath = creationScope.SessionWorkspacePath;
            leaseStream = creationScope.CreateLeaseStream();
            var processStartTimeUtc = ReadCurrentProcessStartTimeUtc(acquisition);
            var metadata = new VbaDebugSessionWorkspaceLeaseMetadata(
                1,
                sessionId.Value,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Environment.ProcessId,
                processStartTimeUtc.ToString("O"));
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
                creationScope,
                acquisition.Complete());
        }
        catch (Exception primary)
        {
            var completion = new DebugFailureCompletion(primary);
            completion.Merge(acquisition.Complete());
            if (leaseStream is not null)
            {
                WindowsVbaDebugWorkspaceTreeDeleter.ReleaseOwnedHandles(
                    [leaseStream.SafeFileHandle], completion, "session-lease-handle", sessionWorkspacePath);
                try { await leaseStream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    completion.AddFailure("session-lease-stream", sessionWorkspacePath,
                        DebugResourceKind.Handle, cleanup, retainedPath: sessionWorkspacePath);
                }
            }
            if (creationScope is not null)
            {
                var deletionRequested = false;
                try
                {
                    creationScope.DeleteOwnedTree();
                    deletionRequested = true;
                }
                catch (Exception cleanup)
                {
                    completion.AddFailure("session-delete", sessionWorkspacePath,
                        DebugResourceKind.FileSystem, cleanup, retainedPath: sessionWorkspacePath);
                }
                try { creationScope.Dispose(); }
                catch (Exception cleanup)
                {
                    completion.AddFailure("session-remaining-handles", sessionWorkspacePath,
                        DebugResourceKind.Handle, cleanup, retainedPath: sessionWorkspacePath);
                }
                if (creationScope is IDebugResourceOwnerEvidence { CleanupOutcome: { } ownedOutcome })
                {
                    completion.Merge(ownedOutcome);
                }
                else
                {
                    completion.AddEvidence(new("session-remaining-handles", sessionWorkspacePath,
                        DebugResourceKind.Handle, false, "The workspace owner supplied no handle-release evidence.",
                        RetainedPath: sessionWorkspacePath));
                }
                WindowsVbaDebugWorkspaceTreeDeleter.RecordOwnedTreeDeletion(
                    completion, "session-delete", sessionWorkspacePath, deletionRequested);
            }
            completion.Complete().Throw();
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

        var completion = new DebugFailureCompletion();
        var preliminaryLeaseState = InspectLease(
            () => CleanupOperations.OpenSessionLeaseStream(sessionId),
            sessionId, completion, "reaper-preliminary-lease", sessionWorkspacePath);
        if (preliminaryLeaseState == VbaDebugSessionLeaseState.Active)
        {
            var activeOutcome = completion.Complete();
            var message = "The VBA debug session workspace lease is still active.";
            if (activeOutcome.HasCleanupFailure) { message += Environment.NewLine + activeOutcome.Describe(); }
            return new VbaDebugSessionCleanupResult(false, Path.GetFullPath(sessionWorkspacePath), message)
                { CleanupOutcome = activeOutcome };
        }

        IVbaDebugWorkspaceCleanupScope cleanupScope;
        try
        {
            cleanupScope = CleanupOperations.OpenSessionCleanupScope(sessionId);
        }
        catch (Exception failure)
        {
            var retained = completion.Complete();
            completion = new DebugFailureCompletion(failure);
            completion.Merge(retained);
            if (failure is not IDebugFailureEvidence carrier
                || !carrier.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle))
            {
                completion.AddEvidence(new("reaper-scope-acquisition", sessionWorkspacePath,
                    DebugResourceKind.Handle, false, "The failed scope owner supplied no partial handle evidence.",
                    RetainedPath: sessionWorkspacePath));
            }
            var failedOutcome = completion.Complete();
            return new VbaDebugSessionCleanupResult(false, Path.GetFullPath(sessionWorkspacePath),
                "The VBA debug session workspace boundary could not be verified as safe."
                + Environment.NewLine + failedOutcome.Describe()) { CleanupOutcome = failedOutcome };
        }

        VbaDebugSessionCleanupResult? result = null;
        Exception? primary = null;
        var deletionAttempted = false;
        var deletionRequested = false;
        try
        {
            var leaseState = InspectLease(cleanupScope.OpenLeaseStream, sessionId,
                completion, "reaper-pinned-lease", sessionWorkspacePath);
            if (leaseState != VbaDebugSessionLeaseState.Stale)
            {
                var stateDescription = leaseState == VbaDebugSessionLeaseState.Active
                    ? "is still active" : "could not be verified as stale";
                result = new VbaDebugSessionCleanupResult(false, Path.GetFullPath(sessionWorkspacePath),
                    $"The VBA debug session workspace lease {stateDescription}.");
            }
            else
            {
                deletionAttempted = true;
                deletionRequested = await TryDeleteWithRetryAsync(cleanupScope.DeleteDirectory,
                    cancellationToken, completion, sessionWorkspacePath).ConfigureAwait(false);
                result = deletionRequested
                    ? new VbaDebugSessionCleanupResult(true, null, null)
                    : new VbaDebugSessionCleanupResult(false, Path.GetFullPath(sessionWorkspacePath),
                        "The stale VBA debug session workspace could not be deleted within five seconds.");
            }
        }
        catch (Exception failure)
        {
            primary = failure;
            var retained = completion.Complete();
            completion = new DebugFailureCompletion(primary);
            completion.Merge(retained);
        }
        try { cleanupScope.Dispose(); }
        catch (Exception failure)
        {
            completion.AddFailure("reaper-scope-release", sessionWorkspacePath,
                DebugResourceKind.Handle, failure, retainedPath: sessionWorkspacePath);
        }
        if (cleanupScope is IDebugResourceOwnerEvidence { CleanupOutcome: { } ownerOutcome })
        {
            completion.Merge(ownerOutcome);
        }
        else
        {
            completion.AddEvidence(new("reaper-scope-release", sessionWorkspacePath,
                DebugResourceKind.Handle, false, "The reaper scope supplied no handle-release evidence.",
                RetainedPath: sessionWorkspacePath));
        }
        if (deletionAttempted)
        {
            WindowsVbaDebugWorkspaceTreeDeleter.RecordOwnedTreeDeletion(
                completion, "session-delete", sessionWorkspacePath, deletionRequested);
        }
        var outcome = completion.Complete();
        if (primary is not null) { outcome.ThrowWithEvidence(); }
        if (outcome.HasCleanupFailure)
        {
            return new VbaDebugSessionCleanupResult(false, Path.GetFullPath(sessionWorkspacePath),
                string.Join(Environment.NewLine, new[] { result?.Message, outcome.Describe() }
                    .Where(message => !string.IsNullOrEmpty(message)))) { CleanupOutcome = outcome };
        }
        return result! with { CleanupOutcome = outcome };
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
        CancellationToken cancellationToken,
        DebugFailureCompletion? failureCompletion = null,
        string? ownedPath = null)
    {
        var observedFailures = new List<Exception>();
        var started = CleanupOperations.GetTimestamp();
        var retryDelay = TimeSpan.FromMilliseconds(100);
        var timeout = TimeSpan.FromSeconds(5);
        var succeeded = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    deleteDirectory();
                    succeeded = true;
                    return true;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    observedFailures.Add(exception);
                    var elapsed = CleanupOperations.GetElapsedTime(started);
                    if (elapsed >= timeout) { return false; }
                    var remaining = timeout - elapsed;
                    await CleanupOperations.DelayAsync(
                        remaining < retryDelay ? remaining : retryDelay,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Successful bounded retries stay successful. Cancellation or any other
            // terminal failure retains the failures already observed before it.
            if (!succeeded && failureCompletion is not null && ownedPath is not null)
            {
                foreach (var failure in observedFailures)
                {
                    failureCompletion.AddFailure("session-delete", ownedPath,
                        DebugResourceKind.FileSystem, failure, retainedPath: ownedPath);
                }
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
        DebugSessionId expectedSessionId, DebugFailureCompletion completion, string stage, string ownedPath)
    {
        Stream? leaseStream = null;
        try
        {
            leaseStream = openLeaseStream();
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

            return InspectProcessIdentity(pid, leasedStartTime, completion, stage, ownedPath);
        }
        catch (Exception failure)
        {
            completion.AddFailure(stage, "lease inspection", DebugResourceKind.Observation, failure,
                retainedPath: ownedPath);
            if (leaseStream is null && (failure is not IDebugFailureEvidence carrier
                || !carrier.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle)))
            {
                completion.AddEvidence(new(stage, "lease stream", DebugResourceKind.Handle, false,
                    "The failed lease reader supplied no partial handle evidence.", RetainedPath: ownedPath));
            }
            return VbaDebugSessionLeaseState.Unverified;
        }
        finally
        {
            if (leaseStream is not null)
            {
                if (leaseStream is FileStream file)
                {
                    ReleaseInspectionHandle(file.SafeFileHandle, completion, stage, "lease stream", ownedPath);
                }
                try { leaseStream.Dispose(); }
                catch (Exception failure)
                {
                    completion.AddFailure(stage, "lease stream", DebugResourceKind.Handle, failure,
                        retainedPath: ownedPath);
                }
                if (leaseStream is not FileStream)
                {
                    var released = leaseStream.GetType() == typeof(MemoryStream);
                    if (leaseStream is IDebugResourceOwnerEvidence { CleanupOutcome: { } readerOutcome })
                    {
                        completion.Merge(readerOutcome);
                        released = !readerOutcome.HasUnprovedRelease
                            && readerOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle && item.Released);
                    }
                    completion.AddEvidence(new(stage, "lease stream", DebugResourceKind.Handle, released,
                        "An in-memory reader acquires no native handle; other readers must supply owner evidence.",
                        RetainedPath: ownedPath));
                }
            }
        }
    }

    private static VbaDebugSessionLeaseState InspectProcessIdentity(int pid, DateTimeOffset leasedStartTime,
        DebugFailureCompletion completion, string stage, string ownedPath)
    {
        const uint queryLimitedInformationAndSynchronize = 0x00101000;
        using var handle = OpenProcess(queryLimitedInformationAndSynchronize, false, (uint)pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            completion.AddEvidence(new(stage, "lease process identity", DebugResourceKind.Handle, true,
                "The native process query did not acquire a handle.", pid, ownedPath));
            if (error == 87) { return VbaDebugSessionLeaseState.Stale; }
            throw new Win32Exception(error);
        }
        try
        {
            var waitResult = WaitForSingleObject(handle, 0);
            if (waitResult == 0) { return VbaDebugSessionLeaseState.Stale; }
            if (waitResult != 258) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            if (!GetOwnedProcessTimes(handle, out var creationTime, out _, out _, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return DateTime.FromFileTimeUtc(creationTime).Ticks == leasedStartTime.UtcTicks
                ? VbaDebugSessionLeaseState.Active : VbaDebugSessionLeaseState.Stale;
        }
        finally
        {
            ReleaseInspectionHandle(handle, completion, stage, "lease process identity", ownedPath, pid);
        }
    }

    private static void ReleaseInspectionHandle(SafeHandle handle, DebugFailureCompletion completion,
        string stage, string resource, string ownedPath, int? pid = null)
    {
        var release = new DebugNativeHandleRelease(handle);
        try { release.Release(); }
        catch (Exception failure)
        {
            completion.AddFailure(stage, resource, DebugResourceKind.Handle, failure, pid, ownedPath);
        }
        completion.AddEvidence(new(stage, resource, DebugResourceKind.Handle, release.IsVerified,
            "The synchronous identity inspection retained the native CloseHandle result.", pid, ownedPath));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOwnedProcessTimes(SafeFileHandle process, out long creationTime,
        out long exitTime, out long kernelTime, out long userTime);

    private static bool IsCanonicalHex32(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class OwnedVbaDebugSessionWorkspaceLease(
        VbaDebugSessionWorkspaceManager owner,
        DebugSessionId sessionId,
        string sessionWorkspacePath,
        FileStream leaseStream,
        IVbaDebugSessionWorkspaceCreationScope creationScope,
        DebugFailureOutcome acquisitionOutcome)
        : IVbaDebugSessionWorkspaceLease, IDebugResourceOwnerEvidence
    {
        private readonly HashSet<DebugGenerationId> claimedGenerations = [];
        private readonly object gate = new();
        private int disposed;
        private Task? disposal;

        public DebugFailureOutcome? CleanupOutcome { get; private set; }

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

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                disposed = 1;
                return new ValueTask(disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            var completion = new DebugFailureCompletion();
            completion.Merge(acquisitionOutcome);
            WindowsVbaDebugWorkspaceTreeDeleter.ReleaseOwnedHandles(
                [leaseStream.SafeFileHandle], completion, "session-lease-handle", SessionWorkspacePath);
            try
            {
                await leaseStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                completion.AddFailure("session-lease-stream", SessionWorkspacePath,
                    DebugResourceKind.Handle, exception, retainedPath: SessionWorkspacePath);
            }
            var deletionRequested = false;
            try
            {
                if (!await owner.TryDeleteWithRetryAsync(
                        creationScope.DeleteOwnedTree,
                        CancellationToken.None,
                        completion,
                        SessionWorkspacePath).ConfigureAwait(false))
                {
                    throw new IOException(
                        $"The VBA debug session workspace could not be deleted within five seconds: {SessionWorkspacePath}");
                }
                deletionRequested = true;
            }
            catch (Exception exception)
            {
                completion.AddFailure("session-delete", SessionWorkspacePath,
                    DebugResourceKind.FileSystem, exception, retainedPath: SessionWorkspacePath);
            }
            try { creationScope.Dispose(); }
            catch (Exception exception)
            {
                completion.AddFailure("session-remaining-handles", SessionWorkspacePath,
                    DebugResourceKind.Handle, exception, retainedPath: SessionWorkspacePath);
            }
            if (creationScope is IDebugResourceOwnerEvidence { CleanupOutcome: { } handleOutcome })
            {
                completion.Merge(handleOutcome);
            }
            else
            {
                completion.AddEvidence(new("session-remaining-handles", SessionWorkspacePath,
                    DebugResourceKind.Handle, false, "The workspace owner supplied no handle-release evidence.",
                    RetainedPath: SessionWorkspacePath));
            }
            WindowsVbaDebugWorkspaceTreeDeleter.RecordOwnedTreeDeletion(
                completion, "session-delete", SessionWorkspacePath, deletionRequested);
            CleanupOutcome = completion.Complete();
            CleanupOutcome.Throw();
        }
    }

    private static DateTime ReadCurrentProcessStartTimeUtc(DebugFailureCompletion completion)
    {
        try
        {
            if (!GetProcessTimes(GetCurrentProcess(), out var creationTime, out _, out _, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return DateTime.FromFileTimeUtc(creationTime);
        }
        finally
        {
            completion.AddEvidence(new("session-metadata-query", "current process metadata",
                DebugResourceKind.Handle, true,
                "The identity query used the current-process pseudo handle and acquired no releasable handle.",
                Environment.ProcessId));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long creationTime,
        out long exitTime, out long kernelTime, out long userTime);

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
    string? Message) : IDebugResourceOwnerEvidence
{
    internal DebugFailureOutcome? CleanupOutcome { get; init; }
    DebugFailureOutcome? IDebugResourceOwnerEvidence.CleanupOutcome => CleanupOutcome;
}

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
