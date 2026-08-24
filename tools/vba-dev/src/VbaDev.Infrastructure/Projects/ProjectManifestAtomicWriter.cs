using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.Infrastructure.Projects;

/// <summary>
/// Persists complete project manifests through flushed sibling staging files and atomic moves.
/// </summary>
public sealed class ProjectManifestAtomicWriter : IProjectManifestAtomicWriter
{
    private readonly TimeProvider timeProvider;
    private readonly Action<FileStream, ReadOnlyMemory<byte>> persistTemporaryFile;

    /// <summary>
    /// Creates a writer using the system clock for recovery artifact names.
    /// </summary>
    public ProjectManifestAtomicWriter()
        : this(TimeProvider.System, PersistTemporaryFile)
    {
    }

    /// <summary>
    /// Creates a writer with an explicit clock for recovery artifact names.
    /// </summary>
    public ProjectManifestAtomicWriter(TimeProvider timeProvider)
        : this(timeProvider, PersistTemporaryFile)
    {
    }

    internal ProjectManifestAtomicWriter(
        TimeProvider timeProvider,
        Action<FileStream, ReadOnlyMemory<byte>> persistTemporaryFile)
    {
        this.timeProvider = timeProvider;
        this.persistTemporaryFile = persistTemporaryFile;
    }

    /// <inheritdoc />
    public void Save(string manifestPath, ProjectManifest manifest)
    {
        var bytes = ValidateAndSerialize(manifestPath, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var temporaryPath = WriteTemporaryFile(manifestPath, bytes);
        try
        {
            if (File.Exists(manifestPath))
            {
                File.Replace(temporaryPath, manifestPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, manifestPath, overwrite: false);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <inheritdoc />
    public void ReplaceExisting(
        string manifestPath,
        ReadOnlyMemory<byte> expectedRawBytes,
        ProjectManifest manifest,
        CancellationToken cancellationToken)
    {
        var bytes = ValidateAndSerialize(manifestPath, manifest);
        var temporaryPath = WriteTemporaryFile(manifestPath, bytes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureManifestBytesUnchanged(manifestPath, expectedRawBytes.Span);
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporaryPath, manifestPath, destinationBackupFileName: null);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <inheritdoc />
    public void EstablishNoOp(
        string manifestPath,
        ReadOnlyMemory<byte> expectedRawBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureManifestBytesUnchanged(manifestPath, expectedRawBytes.Span);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public string CreateRecovery(string projectRoot, ProjectManifest manifest)
    {
        Directory.CreateDirectory(projectRoot);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var timestamp = timeProvider.GetUtcNow().ToString(
                "yyyyMMdd'T'HHmmss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture);
            var finalPath = Path.Combine(
                projectRoot,
                $"vba-project.failed-{timestamp}-{Guid.NewGuid():N}.json");
            var bytes = ValidateAndSerialize(finalPath, manifest);
            var temporaryPath = WriteTemporaryFile(finalPath, bytes);
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: false);
                return finalPath;
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // A final-name collision never replaces prior recovery authority.
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }

        throw new IOException(
            $"A unique project manifest recovery artifact could not be created under: {projectRoot}");
    }

    private static byte[] ValidateAndSerialize(
        string manifestPath,
        ProjectManifest manifest)
    {
        try
        {
            ProjectManifestValidator.Validate(manifest, ProjectManifest.ManifestFileName);
            _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                manifestPath,
                ProjectManifest.ManifestFileName);
            return ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest);
        }
        catch (VbaProjectManifestException ex)
        {
            throw new ProjectManifestException(ex.Message, ex);
        }
    }

    private string WriteTemporaryFile(
        string manifestPath,
        ReadOnlyMemory<byte> bytes)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(directory);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(manifestPath)}.vba-dev.{Guid.NewGuid():N}.tmp");
            FileStream stream;
            try
            {
                stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(temporaryPath))
            {
                // A collision never authorizes overwriting an existing sibling.
                continue;
            }

            try
            {
                using (stream)
                {
                    persistTemporaryFile(stream, bytes);
                }

                return temporaryPath;
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryPath);
                throw;
            }
        }

        throw new IOException(
            $"A unique project manifest staging file could not be created beside: {manifestPath}");
    }

    private static void PersistTemporaryFile(
        FileStream stream,
        ReadOnlyMemory<byte> bytes)
    {
        stream.Write(bytes.Span);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureManifestBytesUnchanged(
        string manifestPath,
        ReadOnlySpan<byte> expectedBytes)
    {
        byte[] currentBytes;
        try
        {
            currentBytes = File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectManifestMutationException(
                "manifestExternalEditConflict",
                $"The project manifest changed after the mutation rebase: {manifestPath}",
                ex);
        }

        if (!currentBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new ProjectManifestMutationException(
                "manifestExternalEditConflict",
                $"The project manifest changed after the mutation rebase: {manifestPath}");
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The primary operation remains authoritative; retained staging is non-authoritative.
        }
    }
}
