using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
    private const int WindowsErrorFileNotFound = 2;
    private const int WindowsErrorPathNotFound = 3;
    private const int WindowsErrorSharingViolation = 32;
    private const int WindowsErrorLockViolation = 33;
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoClass = 4;

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
                            FileShare.Read,
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
                var markerIdentity = pathIdentityResolver.Resolve(markerPath);
                var lease = new Lease(
                    ownerStream,
                    projectIdentity,
                    manifestPath,
                    markerPath,
                    leaseId,
                    markerIdentity,
                    pathIdentityResolver,
                    afterOwnerRelease);
                lease.ProveOwnershipContinuity();
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
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var staleMarker = OpenMarkerForDisposition(
                    markerPath,
                    manifestPath,
                    out _,
                    out var isOrdinary);
                if (!isOrdinary)
                {
                    throw new ProjectManifestMutationException(
                        "manifestMutationLeaseFailed",
                        $"Project manifest mutation ownership marker is not an ordinary sibling file: {manifestPath}");
                }

                SetMarkerDisposition(staleMarker.SafeFileHandle, markerPath);
                return MarkerReclaimResult.Removed;
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is WindowsErrorFileNotFound
                    or WindowsErrorPathNotFound)
            {
                return MarkerReclaimResult.Missing;
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is WindowsErrorSharingViolation
                    or WindowsErrorLockViolation)
            {
                return MarkerReclaimResult.Owned;
            }
            catch (ProjectManifestMutationException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or Win32Exception)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationLeaseFailed",
                    $"Project manifest mutation ownership marker could not be reclaimed safely: {manifestPath}",
                    exception);
            }
        }

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

    private static FileStream OpenMarkerForDisposition(
        string markerPath,
        string manifestPath,
        out FileSystemObjectIdentity identity,
        out bool isOrdinary)
    {
        var handle = CreateFile(
            markerPath,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"The project manifest mutation ownership marker could not be opened safely: {manifestPath}");
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"The project manifest mutation ownership marker identity could not be read: {manifestPath}");
            }

            identity = new FileSystemObjectIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow);
            isOrdinary = (information.FileAttributes
                & (FileAttributeDirectory | FileAttributeReparsePoint)) == 0;
            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void SetMarkerDisposition(
        SafeFileHandle handle,
        string markerPath)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"The exact project manifest mutation ownership marker could not be removed: {markerPath}");
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
            ProjectManifestMutationCommand.NewExcel => "new excel",
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
                || command is not "new excel"
                    and not "common-module add"
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

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
        FileSystemPathIdentity markerIdentity,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        Action<string>? afterOwnerRelease)
        : IProjectManifestMutationLease
    {
        private FileStream? ownerStream = ownerStream;

        public FileSystemPathIdentity ProjectIdentity { get; } = projectIdentity;

        public string ManifestPath { get; } = manifestPath;

        public void ProveOwnershipContinuity()
        {
            if (ownerStream is null)
            {
                throw new ProjectManifestMutationException(
                    "manifestMutationReleaseFailed",
                    $"Project manifest mutation ownership was already released: {ManifestPath}");
            }

            try
            {
                var identityBeforeRead = pathIdentityResolver.Resolve(markerPath);
                var currentLeaseId = TryReadLeaseId(markerPath);
                var identityAfterRead = pathIdentityResolver.Resolve(markerPath);
                if (currentLeaseId != leaseId
                    || !SameExactMarkerObject(markerIdentity, identityBeforeRead)
                    || !SameExactMarkerObject(markerIdentity, identityAfterRead))
                {
                    throw OwnershipChanged();
                }
            }
            catch (ProjectManifestMutationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or InvalidOperationException
                or ArgumentException
                or System.Security.SecurityException)
            {
                throw OwnershipChanged(ex);
            }
        }

        private static bool SameExactMarkerObject(
            FileSystemPathIdentity expected,
            FileSystemPathIdentity current)
            => expected.ObjectIdentity is not null
               && current.ObjectIdentity == expected.ObjectIdentity
               && Path.TrimEndingDirectorySeparator(expected.CanonicalPath).Equals(
                   Path.TrimEndingDirectorySeparator(current.CanonicalPath),
                   StringComparison.OrdinalIgnoreCase);

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
            var warning = TryRemoveOwnedMarker(
                markerPath,
                leaseId,
                markerIdentity,
                ManifestPath);
            return ValueTask.FromResult(new ProjectManifestLeaseRelease(
                warning is null ? [] : [warning]));
        }

        private static ProjectManifestMutationWarning? TryRemoveOwnedMarker(
            string markerPath,
            Guid leaseId,
            FileSystemPathIdentity markerIdentity,
            string manifestPath)
        {
            FileStream cleanupStream;
            FileSystemObjectIdentity? cleanupIdentity = null;
            var cleanupMarkerIsOrdinary = true;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    cleanupStream = OpenMarkerForDisposition(
                        markerPath,
                        manifestPath,
                        out var openedIdentity,
                        out cleanupMarkerIsOrdinary);
                    cleanupIdentity = openedIdentity;
                }
                else
                {
                    cleanupStream = new FileStream(
                        markerPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.Read);
                }
            }
            catch (Win32Exception ex) when (
                ex.NativeErrorCode is WindowsErrorFileNotFound
                    or WindowsErrorPathNotFound)
            {
                return null;
            }
            catch (Win32Exception ex)
            {
                var retainedLeaseId = TryReadLeaseId(markerPath);
                return retainedLeaseId is not null && retainedLeaseId != leaseId
                    ? null
                    : CleanupWarning(markerPath, ex);
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

            if (OperatingSystem.IsWindows()
                && (markerIdentity.ObjectIdentity is null
                    || cleanupIdentity != markerIdentity.ObjectIdentity))
            {
                cleanupStream.Dispose();
                return null;
            }

            if (!cleanupMarkerIsOrdinary)
            {
                cleanupStream.Dispose();
                return CleanupWarning(
                    markerPath,
                    new IOException(
                        $"The released lease marker is no longer an ordinary sibling file: {manifestPath}"));
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
                    if (OperatingSystem.IsWindows())
                    {
                        SetMarkerDisposition(
                            cleanupStream.SafeFileHandle,
                            markerPath);
                    }
                    else
                    {
                        File.Delete(markerPath);
                    }
                    return null;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or Win32Exception)
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

        private ProjectManifestMutationException OwnershipChanged(
            Exception? innerException = null)
            => innerException is null
                ? new ProjectManifestMutationException(
                    "manifestMutationLeaseChanged",
                    $"Project manifest mutation ownership marker changed while the lease was held: {ManifestPath}")
                : new ProjectManifestMutationException(
                    "manifestMutationLeaseChanged",
                    $"Project manifest mutation ownership marker could not be proved while the lease was held: {ManifestPath}",
                    innerException);

        private static ProjectManifestMutationWarning CleanupWarning(
            string markerPath,
            Exception exception)
            => new(
                "leaseMarkerCleanupFailed",
                $"The released project manifest lease marker could not be removed: {markerPath}. {exception.Message}");
    }
}
