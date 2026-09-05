using System.ComponentModel;
using VbaDev.App.FileSystem;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Captures the exact raw-byte or absence precondition for one source file.
/// </summary>
internal sealed record CommonModulesExpectedFile(bool Exists, byte[] Bytes)
{
    public static CommonModulesExpectedFile Absent { get; } = new(false, []);

    public static CommonModulesExpectedFile Present(byte[] bytes)
        => new(true, [.. bytes]);
}

/// <summary>
/// Describes one planned create, replacement, deletion, or case-only recase.
/// </summary>
internal sealed record CommonModulesSourceFileMutation(
    string ObservedPath,
    string TargetPath,
    CommonModulesExpectedFile Expected,
    byte[]? DesiredBytes,
    bool VerificationOnly = false);

/// <summary>
/// Reports whether a source mutation crossed its commitment boundary.
/// </summary>
internal sealed record CommonModulesSourceMutationResult(
    bool SourceMutationCommitted,
    bool CancellationDeferred);

/// <summary>
/// Reports a precondition or filesystem failure without rolling back prior mutations.
/// </summary>
internal sealed class CommonModulesSourceMutationException : Exception
{
    public CommonModulesSourceMutationException(
        string message,
        bool sourceMutationCommitted,
        IReadOnlyList<string> manualVerificationPaths,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SourceMutationCommitted = sourceMutationCommitted;
        ManualVerificationPaths = manualVerificationPaths;
    }

    public bool SourceMutationCommitted { get; }

    public IReadOnlyList<string> ManualVerificationPaths { get; }
}

internal sealed class CommonModulesTemporaryFileException : IOException
{
    public CommonModulesTemporaryFileException(
        string temporaryPath,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        TemporaryPath = Path.GetFullPath(temporaryPath);
    }

    public string TemporaryPath { get; }
}

/// <summary>
/// Applies exact-precondition source changes through flushed sibling staging files.
/// </summary>
internal sealed class CommonModulesSourceMutationWriter
{
    private readonly Action<int>? beforeOperation;
    private readonly Action<int>? beforeCommitment;
    private readonly Action<int>? afterTemporaryFileFlushed;
    private readonly Action<string, long>? onTemporaryBytesWritten;

    internal CommonModulesSourceMutationWriter(
        Action<int>? beforeOperation = null,
        Action<int>? beforeCommitment = null,
        Action<int>? afterTemporaryFileFlushed = null,
        Action<string, long>? onTemporaryBytesWritten = null)
    {
        this.beforeOperation = beforeOperation;
        this.beforeCommitment = beforeCommitment;
        this.afterTemporaryFileFlushed = afterTemporaryFileFlushed;
        this.onTemporaryBytesWritten = onTemporaryBytesWritten;
    }

    public CommonModulesSourceMutationResult Execute(
        IReadOnlyList<CommonModulesSourceFileMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            return new CommonModulesSourceMutationResult(
                SourceMutationCommitted: false,
                CancellationDeferred: false);
        }

        var verificationPaths = GetVerificationPaths(mutations);
        using var ownership = ExactFileSystemObjectOwnership.Open();
        var deletionReceipts = new ExactFileSystemObjectOwnership.FileReceipt?[mutations.Count];
        try
        {
            for (var index = 0; index < mutations.Count; index++)
            {
                var mutation = mutations[index];
                EnsurePrecondition(mutation);
                if (mutation.DesiredBytes is null && !mutation.VerificationOnly)
                {
                    var capture = ownership.CaptureTrustedStableFile(mutation.ObservedPath);
                    if (!mutation.Expected.Exists
                        || !capture.Bytes.AsSpan().SequenceEqual(mutation.Expected.Bytes))
                    {
                        throw new IOException(
                            $"CommonModules source deletion target changed after planning: {mutation.ObservedPath}");
                    }

                    deletionReceipts[index] = capture.Receipt;
                }
            }
        }
        catch (Exception ex) when (IsSourceMutationFailure(ex))
        {
            throw CreateFailure(ex, sourceMutationCommitted: false, verificationPaths);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceMutationCommitted = false;
        for (var index = 0; index < mutations.Count; index++)
        {
            try
            {
                beforeOperation?.Invoke(index);
                if (!sourceMutationCommitted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var mutation = mutations[index];
                EnsurePrecondition(mutation);
                if (mutation.VerificationOnly)
                {
                    if (!sourceMutationCommitted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    continue;
                }

                void CrossCommitmentBoundary()
                {
                    if (sourceMutationCommitted)
                    {
                        return;
                    }

                    beforeCommitment?.Invoke(index);
                    cancellationToken.ThrowIfCancellationRequested();
                    sourceMutationCommitted = true;
                }

                Apply(mutation, index, CrossCommitmentBoundary, ownership, deletionReceipts[index]);
            }
            catch (OperationCanceledException) when (!sourceMutationCommitted)
            {
                throw;
            }
            catch (Exception ex) when (IsSourceMutationFailure(ex))
            {
                throw CreateFailure(ex, sourceMutationCommitted, verificationPaths);
            }
        }

        if (!sourceMutationCommitted)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new CommonModulesSourceMutationResult(
            sourceMutationCommitted,
            sourceMutationCommitted && cancellationToken.IsCancellationRequested);
    }

    private void Apply(
        CommonModulesSourceFileMutation mutation,
        int index,
        Action crossCommitmentBoundary,
        ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.FileReceipt? deletionReceipt)
    {
        if (mutation.DesiredBytes is null)
        {
            EnsurePrecondition(mutation);
            var deletion = ownership.TryDelete(
                deletionReceipt ?? throw new IOException("CommonModules source deletion has no trusted capture."),
                onDispositionStarting: _ => crossCommitmentBoundary());
            if (!deletion.Removed)
            {
                throw new IOException(
                    $"CommonModules source deletion could not be proven: {mutation.ObservedPath}");
            }
            return;
        }

        var targetDirectory = Path.GetDirectoryName(mutation.TargetPath)!;
        var temporary = WriteFlushedTemporaryFile(
            ownership,
            mutation.TargetPath,
            mutation.DesiredBytes);
        var temporaryTransferred = false;
        try
        {
            afterTemporaryFileFlushed?.Invoke(index);
            EnsurePrecondition(mutation);
            crossCommitmentBoundary();
            if (mutation.Expected.Exists)
            {
                File.Replace(
                    temporary.Route,
                    mutation.ObservedPath,
                    destinationBackupFileName: null);
                temporaryTransferred = true;
                if (!Path.GetFullPath(mutation.ObservedPath).Equals(
                        Path.GetFullPath(mutation.TargetPath),
                        StringComparison.Ordinal))
                {
                    EnsureExactBytes(mutation.ObservedPath, mutation.DesiredBytes);
                    EnsureCaseOnlyDestinationAvailable(
                        mutation.ObservedPath,
                        mutation.TargetPath);
                    File.Move(
                        mutation.ObservedPath,
                        mutation.TargetPath,
                        overwrite: false);
                }
            }
            else
            {
                Directory.CreateDirectory(targetDirectory);
                File.Move(temporary.Route, mutation.TargetPath, overwrite: false);
                temporaryTransferred = true;
            }
        }
        catch (Exception operationException)
        {
            if (!temporaryTransferred)
            {
                DeleteTemporaryFileAfterFailure(ownership, temporary, operationException);
            }

            throw;
        }
        // Successful replacement or placement transfers the staging object to
        // durable source. Its old receipt never authorizes source cleanup.
    }

    private static void EnsurePrecondition(CommonModulesSourceFileMutation mutation)
    {
        if (mutation.Expected.Exists)
        {
            EnsureExactBytes(mutation.ObservedPath, mutation.Expected.Bytes);
            if (IsCaseOnlyRecase(mutation)
                && !OperatingSystem.IsWindows()
                && PathExists(mutation.TargetPath))
            {
                throw new IOException(
                    $"CommonModules source target is occupied: {Path.GetFullPath(mutation.TargetPath)}");
            }

            return;
        }

        if (PathExists(mutation.ObservedPath) || PathExists(mutation.TargetPath))
        {
            throw new IOException(
                $"CommonModules source target no longer has the planned absent state: "
                + Path.GetFullPath(mutation.TargetPath));
        }
    }

    private static void EnsureExactBytes(string path, ReadOnlySpan<byte> expectedBytes)
    {
        if (!File.Exists(path))
        {
            throw new IOException(
                $"CommonModules source target no longer exists: {Path.GetFullPath(path)}");
        }

        byte[] actualBytes;
        try
        {
            actualBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"CommonModules source target could not be verified: {Path.GetFullPath(path)}",
                ex);
        }

        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new IOException(
                $"CommonModules source target changed after planning: {Path.GetFullPath(path)}");
        }
    }

    private static void EnsureCaseOnlyDestinationAvailable(
        string observedPath,
        string targetPath)
    {
        if (!IsCaseOnlyRecase(observedPath, targetPath))
        {
            throw new IOException(
                $"CommonModules source recase changed filesystem identity: {Path.GetFullPath(targetPath)}");
        }

        if (!OperatingSystem.IsWindows() && PathExists(targetPath))
        {
            throw new IOException(
                $"CommonModules source recase target is occupied: {Path.GetFullPath(targetPath)}");
        }
    }

    private static bool IsCaseOnlyRecase(CommonModulesSourceFileMutation mutation)
        => IsCaseOnlyRecase(mutation.ObservedPath, mutation.TargetPath);

    private static bool IsCaseOnlyRecase(string observedPath, string targetPath)
    {
        var observed = Path.GetFullPath(observedPath);
        var target = Path.GetFullPath(targetPath);
        return !observed.Equals(target, StringComparison.Ordinal)
               && observed.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    private ExactFileSystemObjectOwnership.FileReceipt WriteFlushedTemporaryFile(
        ExactFileSystemObjectOwnership ownership,
        string targetPath,
        ReadOnlyMemory<byte> bytes)
    {
        var directory = FindExistingStagingDirectory(targetPath);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var temporaryName = $"{Path.GetFileName(targetPath)}.vba-dev.{Guid.NewGuid():N}.tmp";
            var fileCreated = false;
            try
            {
                return ownership.CreateOnlyFile(
                    directory,
                    temporaryName,
                    bytes.Span,
                    onFileCreated: _ => fileCreated = true,
                    onBytesWritten: onTemporaryBytesWritten);
            }
            catch (Win32Exception exception) when (
                !fileCreated && exception.NativeErrorCode is 80 or 183)
            {
                // A staging-name collision never authorizes replacement.
                continue;
            }
            catch (ExactFileSystemObjectOwnership.FileCreationCleanupException exception)
            {
                // The creator has already attempted same-handle partial-file
                // cleanup. Do not retry, re-capture, or claim proven retention.
                throw new CommonModulesTemporaryFileException(
                    exception.Route,
                    $"CommonModules source staging cleanup could not be proven: {exception.Route}. {exception.Message}",
                    exception);
            }
        }

        throw new IOException(
            $"A unique CommonModules source staging file could not be created on the target volume for: {targetPath}");
    }

    private static string FindExistingStagingDirectory(string targetPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        while (directory is not null && !Directory.Exists(directory))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new IOException(
            $"No existing staging directory was found for CommonModules source target: {targetPath}");
    }

    private static void DeleteTemporaryFileAfterFailure(
        ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.FileReceipt temporary,
        Exception operationException)
    {
        try
        {
            var deletion = ownership.TryDelete(temporary);
            if (!deletion.Removed)
            {
                throw new IOException("The unchanged staging object could not be proven removed.");
            }
        }
        catch (Exception cleanupException) when (IsSourceMutationFailure(cleanupException))
        {
            throw new CommonModulesTemporaryFileException(
                temporary.Route,
                $"CommonModules source staging failed ({operationException.Message}) and cleanup could not be proven: "
                + $"{temporary.Route}. {cleanupException.Message}",
                new AggregateException(operationException, cleanupException));
        }
    }

    private static IReadOnlyList<string> GetVerificationPaths(
        IReadOnlyList<CommonModulesSourceFileMutation> mutations)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            foreach (var path in new[] { mutation.ObservedPath, mutation.TargetPath })
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    paths.Add(fullPath);
                }
            }
        }

        return paths;
    }

    private static CommonModulesSourceMutationException CreateFailure(
        Exception exception,
        bool sourceMutationCommitted,
        IReadOnlyList<string> verificationPaths)
    {
        var completeVerificationPaths = verificationPaths.ToList();
        if (exception is CommonModulesTemporaryFileException temporaryFailure
            && !completeVerificationPaths.Contains(
                temporaryFailure.TemporaryPath,
                StringComparer.Ordinal))
        {
            completeVerificationPaths.Add(temporaryFailure.TemporaryPath);
        }

        var renderedPaths = string.Join(
            ", ",
            completeVerificationPaths.Select(path => $"\"{path}\""));
        var phase = sourceMutationCommitted
            ? "after source mutation began"
            : "before source mutation began";
        return new CommonModulesSourceMutationException(
            $"CommonModules source mutation failed {phase}. "
            + $"Manually verify: {renderedPaths}. {exception.Message}",
            sourceMutationCommitted,
            completeVerificationPaths,
            exception);
    }

    private static bool IsSourceMutationFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or Win32Exception
            or ExactFileSystemObjectOwnership.RollbackException
            or CommonModulesSourceMutationException;

    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);
}
