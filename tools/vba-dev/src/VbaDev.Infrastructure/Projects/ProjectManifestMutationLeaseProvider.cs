using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Projects;

/// <summary>
/// Owns the cross-process sibling-file lease for one physical project root.
/// </summary>
public sealed class ProjectManifestMutationLeaseProvider : IProjectManifestMutationLeaseProvider
{
    private const int MaximumOwnerMetadataBytes = 4096;
    private const int UnixErrorFileExists = 17;
    private const int WindowsErrorFileExists = 80;
    private const int WindowsErrorAlreadyExists = 183;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan acquisitionTimeout;
    private readonly TimeSpan pollInterval;
    private readonly string toolVersion;
    private readonly Action<string>? afterOwnerRelease;
    private readonly bool useDeleteOnClose;
    private readonly Func<string, FileStream>? createOwnerStreamOverride;

    /// <summary>
    /// Creates a production lease provider with the fixed thirty-second acquisition bound.
    /// </summary>
    public ProjectManifestMutationLeaseProvider()
        : this(
            new FileSystemPathIdentityResolver(),
            TimeProvider.System,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            ResolveToolVersion())
    {
    }

    /// <summary>
    /// Creates a lease provider with explicit timing and identity dependencies.
    /// </summary>
    public ProjectManifestMutationLeaseProvider(
        IFileSystemPathIdentityResolver pathIdentityResolver,
        TimeProvider timeProvider,
        TimeSpan acquisitionTimeout,
        TimeSpan pollInterval,
        string toolVersion)
        : this(
            pathIdentityResolver,
            timeProvider,
            acquisitionTimeout,
            pollInterval,
            toolVersion,
            afterOwnerRelease: null,
            useDeleteOnClose: OperatingSystem.IsWindows())
    {
    }

    internal ProjectManifestMutationLeaseProvider(
        IFileSystemPathIdentityResolver pathIdentityResolver,
        TimeProvider timeProvider,
        TimeSpan acquisitionTimeout,
        TimeSpan pollInterval,
        string toolVersion,
        Action<string>? afterOwnerRelease)
        : this(
            pathIdentityResolver,
            timeProvider,
            acquisitionTimeout,
            pollInterval,
            toolVersion,
            afterOwnerRelease,
            useDeleteOnClose: OperatingSystem.IsWindows())
    {
    }

    internal ProjectManifestMutationLeaseProvider(
        IFileSystemPathIdentityResolver pathIdentityResolver,
        TimeProvider timeProvider,
        TimeSpan acquisitionTimeout,
        TimeSpan pollInterval,
        string toolVersion,
        Action<string>? afterOwnerRelease,
        bool useDeleteOnClose,
        Func<string, FileStream>? createOwnerStreamOverride = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            acquisitionTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            pollInterval,
            TimeSpan.Zero);
        this.pathIdentityResolver = pathIdentityResolver;
        this.timeProvider = timeProvider;
        this.acquisitionTimeout = acquisitionTimeout;
        this.pollInterval = pollInterval;
        this.toolVersion = toolVersion;
        this.afterOwnerRelease = afterOwnerRelease;
        this.useDeleteOnClose = useDeleteOnClose;
        this.createOwnerStreamOverride = createOwnerStreamOverride;
    }

    /// <inheritdoc />
    public async ValueTask<IProjectManifestMutationLease> AcquireAsync(
        string projectRoot,
        ProjectManifestMutationCommand command,
        CancellationToken cancellationToken)
    {
        var projectIdentity = pathIdentityResolver.Resolve(projectRoot);
        var manifestPath = Path.Combine(
            projectIdentity.OperationPath,
            ProjectManifest.ManifestFileName);
        var markerPath = manifestPath + ".vba-dev.lock";
        var startedAt = timeProvider.GetTimestamp();
        var attemptedAcquisition = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attemptedAcquisition
                && timeProvider.GetElapsedTime(startedAt) >= acquisitionTimeout)
            {
                throw CreateBusyException(markerPath, manifestPath);
            }

            attemptedAcquisition = true;
            FileStream? ownerStream = null;
            try
            {
                var markerExistedBeforeCreate = RejectUnsafeMarkerEntry(
                    markerPath,
                    manifestPath);
                try
                {
                    ownerStream = createOwnerStreamOverride?.Invoke(markerPath)
                        ?? new FileStream(
                            markerPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.Read |
                            (useDeleteOnClose ? FileShare.Delete : FileShare.None),
                            bufferSize: 4096,
                            FileOptions.WriteThrough |
                            (useDeleteOnClose
                                ? FileOptions.DeleteOnClose
                                : FileOptions.None));
                }
                catch (IOException ex)
                {
                    var reclaimResult = TryRemoveUnownedMarker(
                        markerPath,
                        manifestPath);
                    if (reclaimResult == MarkerReclaimResult.Removed
                        || (reclaimResult == MarkerReclaimResult.Missing
                            && (markerExistedBeforeCreate
                                || IsMarkerCreateCollision(ex))))
                    {
                        continue;
                    }

                    if (reclaimResult == MarkerReclaimResult.Missing)
                    {
                        throw new ProjectManifestMutationException(
                            "manifestMutationLeaseFailed",
                            $"Project manifest mutation ownership marker could not be created: {manifestPath}",
                            ex);
                    }

                    throw;
                }

                var confirmedIdentity = pathIdentityResolver.Resolve(projectRoot);
                if (!FileSystemPathIdentityRelations.Same(
                        projectIdentity,
                        confirmedIdentity))
                {
                    throw new ProjectManifestMutationException(
                        "manifestProjectIdentityChanged",
                        $"The project root identity changed while acquiring manifest mutation ownership: {manifestPath}");
                }

                var leaseId = Guid.NewGuid();
                WriteOwnerMetadata(ownerStream, leaseId, command);
                var lease = new Lease(
                    ownerStream,
                    projectIdentity,
                    manifestPath,
                    markerPath,
                    leaseId,
                    afterOwnerRelease);
                ownerStream = null;
                return lease;
            }
            catch (ProjectManifestMutationException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseFailed",
                    $"Project manifest mutation ownership could not be established: {manifestPath}",
                    ex);
            }
            catch (IOException) when (ownerStream is null)
            {
                if (timeProvider.GetElapsedTime(startedAt) >= acquisitionTimeout)
                {
                    throw CreateBusyException(markerPath, manifestPath);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseFailed",
                    $"Project manifest mutation ownership metadata could not be established: {manifestPath}",
                    ex);
            }
            finally
            {
                try
                {
                    ownerStream?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ProjectManifestMutationException(
                        "manifestMutationReleaseFailed",
                        $"Project manifest mutation ownership release could not be proved: {manifestPath}",
                        ex);
                }
            }

            var remaining = acquisitionTimeout
                            - timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateBusyException(markerPath, manifestPath);
            }

            var nextPollDelay = remaining < pollInterval
                ? remaining
                : pollInterval;
            await Task.Delay(nextPollDelay, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void WriteOwnerMetadata(
        FileStream ownerStream,
        Guid leaseId,
        ProjectManifestMutationCommand command)
    {
        using var process = Process.GetCurrentProcess();
        var metadata = new LeaseOwnerMetadata(
            "1.0",
            leaseId,
            Environment.MachineName,
            process.Id,
            process.StartTime.ToUniversalTime(),
            StableCommandName(command),
            timeProvider.GetUtcNow(),
            toolVersion);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonOptions);
        ownerStream.SetLength(0);
        ownerStream.Position = 0;
        ownerStream.Write(bytes);
        ownerStream.Flush(flushToDisk: true);
    }

    private static bool RejectUnsafeMarkerEntry(
        string markerPath,
        string manifestPath)
    {
        try
        {
            var attributes = File.GetAttributes(markerPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)
                || attributes.HasFlag(FileAttributes.Directory))
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseFailed",
                    $"Project manifest mutation ownership marker is not an ordinary sibling file: {manifestPath}");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            // The previous owner can disappear between inspection and acquisition.
            return false;
        }
        catch (ProjectManifestMutationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectManifestMutationException(
                "manifestMutationLeaseFailed",
                $"Project manifest mutation ownership marker could not be inspected safely: {manifestPath}",
                ex);
        }
    }

    private static MarkerReclaimResult TryRemoveUnownedMarker(
        string markerPath,
        string manifestPath)
    {
        if (!RejectUnsafeMarkerEntry(markerPath, manifestPath))
        {
            return MarkerReclaimResult.Missing;
        }

        try
        {
            using var staleMarker = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            File.Delete(markerPath);
            return MarkerReclaimResult.Removed;
        }
        catch (FileNotFoundException)
        {
            return MarkerReclaimResult.Missing;
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ProjectManifestMutationException(
                "manifestMutationLeaseFailed",
                $"Project manifest mutation ownership marker parent disappeared: {manifestPath}",
                ex);
        }
        catch (IOException)
        {
            return MarkerReclaimResult.Owned;
        }
    }

    private enum MarkerReclaimResult
    {
        Missing,
        Removed,
        Owned
    }

    private static bool IsMarkerCreateCollision(IOException exception)
    {
        var platformErrorCode = exception.HResult & 0xffff;
        return platformErrorCode is UnixErrorFileExists
            or WindowsErrorFileExists
            or WindowsErrorAlreadyExists;
    }

    private static string StableCommandName(ProjectManifestMutationCommand command)
        => command switch
        {
            ProjectManifestMutationCommand.CommonModuleAdd => "common-module add",
            ProjectManifestMutationCommand.CommonModuleUpdate => "common-module update",
            ProjectManifestMutationCommand.ReferenceAdd => "reference add",
            ProjectManifestMutationCommand.ReferenceRemove => "reference remove",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };

    private static JsonDocument ParseBoundedMarkerJson(FileStream stream)
    {
        var buffer = new byte[MaximumOwnerMetadataBytes + 1];
        stream.Position = 0;
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = stream.Read(
                buffer,
                totalBytesRead,
                buffer.Length - totalBytesRead);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        if (totalBytesRead > MaximumOwnerMetadataBytes)
        {
            throw new JsonException(
                "The project manifest mutation ownership metadata exceeded the safe read limit.");
        }

        return JsonDocument.Parse(
            new ReadOnlyMemory<byte>(buffer, 0, totalBytesRead));
    }

    private static ProjectManifestMutationException CreateBusyException(
        string markerPath,
        string manifestPath)
    {
        var owner = TryReadSafeOwnerMetadata(markerPath);
        var ownerSuffix = owner is null
            ? ""
            : $" Owner: command '{owner.Command}', machine '{owner.MachineName}', process {owner.ProcessId}, acquired {owner.AcquiredAtUtc:O}, tool version '{owner.ToolVersion}'.";
        return new ProjectManifestMutationException(
            "manifestMutationBusy",
            $"Timed out waiting for project manifest mutation ownership: {manifestPath}.{ownerSuffix}");
    }

    private static SafeLeaseOwnerMetadata? TryReadSafeOwnerMetadata(string markerPath)
    {
        try
        {
            using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = ParseBoundedMarkerJson(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetSafeString(root, "schemaVersion", 8, out var schemaVersion)
                || schemaVersion != "1.0"
                || !TryGetSafeString(root, "leaseId", 64, out var leaseIdText)
                || !Guid.TryParse(leaseIdText, out _)
                || !TryGetSafeString(root, "machineName", 255, out var machineName)
                || !root.TryGetProperty("processId", out var processIdValue)
                || processIdValue.ValueKind != JsonValueKind.Number
                || !processIdValue.TryGetInt32(out var processId)
                || processId <= 0
                || !TryGetSafeString(root, "processStartTimeUtc", 64, out var processStartText)
                || !DateTimeOffset.TryParse(
                    processStartText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out _)
                || !TryGetSafeString(root, "command", 32, out var command)
                || command is not "common-module add"
                    and not "common-module update"
                    and not "reference add"
                    and not "reference remove"
                || !TryGetSafeString(root, "acquiredAtUtc", 64, out var acquiredText)
                || !DateTimeOffset.TryParse(
                    acquiredText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var acquiredAtUtc)
                || !TryGetSafeString(root, "toolVersion", 128, out var toolVersion))
            {
                return null;
            }

            return new SafeLeaseOwnerMetadata(
                machineName,
                processId,
                command,
                acquiredAtUtc,
                toolVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGetSafeString(
        JsonElement value,
        string propertyName,
        int maximumLength,
        out string result)
    {
        result = "";
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > maximumLength
            || candidate.Any(char.IsControl))
        {
            return false;
        }

        result = candidate;
        return true;
    }

    private static string ResolveToolVersion()
        => typeof(ProjectManifestMutationLeaseProvider).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
               .InformationalVersion
           ?? typeof(ProjectManifestMutationLeaseProvider).Assembly
               .GetName()
               .Version?
               .ToString()
           ?? "unknown";

    private sealed record LeaseOwnerMetadata(
        string SchemaVersion,
        Guid LeaseId,
        string MachineName,
        int ProcessId,
        DateTime ProcessStartTimeUtc,
        string Command,
        DateTimeOffset AcquiredAtUtc,
        string ToolVersion);

    private sealed record SafeLeaseOwnerMetadata(
        string MachineName,
        int ProcessId,
        string Command,
        DateTimeOffset AcquiredAtUtc,
        string ToolVersion);

    private sealed class Lease(
        FileStream ownerStream,
        FileSystemPathIdentity projectIdentity,
        string manifestPath,
        string markerPath,
        Guid leaseId,
        Action<string>? afterOwnerRelease)
        : IProjectManifestMutationLease
    {
        private FileStream? ownerStream = ownerStream;

        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public ValueTask<ProjectManifestLeaseRelease> ReleaseAsync()
        {
            var stream = Interlocked.Exchange(ref ownerStream, null);
            if (stream is null)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationReleaseFailed",
                    $"Project manifest mutation ownership was released more than once: {ManifestPath}");
            }

            try
            {
                stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationReleaseFailed",
                    $"Project manifest mutation ownership release could not be proved: {ManifestPath}",
                    ex);
            }

            afterOwnerRelease?.Invoke(markerPath);
            var warning = TryRemoveOwnedMarker(markerPath, leaseId);
            return ValueTask.FromResult(new ProjectManifestLeaseRelease(
                warning is null ? [] : [warning]));
        }

        private static ProjectManifestMutationWarning? TryRemoveOwnedMarker(
            string markerPath,
            Guid leaseId)
        {
            FileStream cleanupStream;
            try
            {
                cleanupStream = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException ex)
            {
                var retainedLeaseId = TryReadLeaseId(markerPath);
                return retainedLeaseId is not null && retainedLeaseId != leaseId
                    ? null
                    : CleanupWarning(markerPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CleanupWarning(markerPath, ex);
            }

            using (cleanupStream)
            {
                try
                {
                    using var document = ParseBoundedMarkerJson(cleanupStream);
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("leaseId", out var value)
                        || value.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(value.GetString(), out var currentLeaseId))
                    {
                        return CleanupWarning(
                            markerPath,
                            new JsonException(
                                "The released lease marker did not contain a valid leaseId."));
                    }

                    if (currentLeaseId != leaseId)
                    {
                        return null;
                    }
                }
                catch (JsonException ex)
                {
                    return CleanupWarning(markerPath, ex);
                }

                try
                {
                    File.Delete(markerPath);
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return CleanupWarning(markerPath, ex);
                }
            }
        }

        private static Guid? TryReadLeaseId(string markerPath)
        {
            try
            {
                using var stream = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = ParseBoundedMarkerJson(stream);
                return document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("leaseId", out var value)
                       && value.ValueKind == JsonValueKind.String
                       && Guid.TryParse(value.GetString(), out var leaseId)
                    ? leaseId
                    : null;
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException)
            {
                return null;
            }
        }

        private static ProjectManifestMutationWarning CleanupWarning(
            string markerPath,
            Exception exception)
            => new(
                "leaseMarkerCleanupFailed",
                $"The released project manifest lease marker could not be removed: {markerPath}. {exception.Message}");
    }
}
