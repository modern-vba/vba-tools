using VbaDev.Domain;

namespace VbaDev.App.Projects;

internal static class NewProjectInitialManifestStager
{
    public static NewProjectInitialManifestStage Stage(
        string manifestPath,
        ProjectManifest manifest,
        NewProjectArtifactTracker artifacts,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(artifacts);
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var bytes = ProjectManifestCodec.Encode(manifest, fullManifestPath, pathIdentityResolver);
        var directory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new ArgumentException(
                "The initial project manifest path must have a parent directory.",
                nameof(manifestPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"The tracked initial project directory does not exist: {directory}");
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(fullManifestPath)}.vba-dev.{Guid.NewGuid():N}.tmp");
            try
            {
                artifacts.CreateFile(temporaryPath, bytes);
            }
            catch (NewProjectArtifactAlreadyExistsException)
            {
                continue;
            }
            return new NewProjectInitialManifestStage(
                fullManifestPath,
                temporaryPath);
        }

        throw new IOException(
            $"A unique initial project manifest staging file could not be created beside: {fullManifestPath}");
    }

}

internal sealed class NewProjectInitialManifestStage
{
    public NewProjectInitialManifestStage(
        string manifestPath,
        string temporaryPath)
    {
        ManifestPath = manifestPath;
        TemporaryPath = temporaryPath;
    }

    public string ManifestPath { get; }

    public string TemporaryPath { get; }

    public bool IsCommitted { get; private set; }

    public void CommitCreateOnly()
    {
        if (IsCommitted)
        {
            throw new InvalidOperationException(
                "The initial project manifest stage was already committed.");
        }

        File.Move(TemporaryPath, ManifestPath, overwrite: false);
        IsCommitted = true;
    }
}
