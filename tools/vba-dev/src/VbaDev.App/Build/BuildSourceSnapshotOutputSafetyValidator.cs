using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.Build;

internal sealed record BuildSourceSnapshotValidatedPaths(
    string SourceSnapshotPath,
    string OutputPath);

internal sealed class BuildSourceSnapshotOutputSafetyValidator
{
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

    internal BuildSourceSnapshotOutputSafetyValidator(
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        this.pathIdentityResolver = pathIdentityResolver;
    }

    public BuildSourceSnapshotValidatedPaths Validate(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        var snapshotIdentity = pathIdentityResolver.Resolve(sourceSnapshotPath);
        var outputIdentity = pathIdentityResolver.Resolve(outputPath);
        if (FileSystemPathIdentityRelations.SameOrDescendant(
                outputIdentity,
                snapshotIdentity))
        {
            throw new InvalidOperationException(
                $"Snapshot build output must be outside the caller-owned source snapshot: {outputPath}");
        }

        RejectIfSame(
            outputIdentity,
            pathIdentityResolver.Resolve(context.ManifestPath),
            "the resolved project manifest",
            outputPath);

        foreach (var (documentName, document) in context.Manifest.Documents)
        {
            var sourceSetPath = ResolveManifestPath(
                context.ProjectRoot,
                document.SourcePath);
            var sourceSetIdentity = pathIdentityResolver.Resolve(sourceSetPath);
            if (FileSystemPathIdentityRelations.SameOrDescendant(
                    outputIdentity,
                    sourceSetIdentity))
            {
                throw new InvalidOperationException(
                    $"Snapshot build output must be outside manifest document source set '{documentName}': {outputPath}");
            }

            RejectIfSame(
                outputIdentity,
                pathIdentityResolver.Resolve(ResolveManifestPath(
                    context.ProjectRoot,
                    document.TemplatePath)),
                $"manifest source template '{documentName}'",
                outputPath);
            RejectIfSame(
                outputIdentity,
                pathIdentityResolver.Resolve(ResolveManifestPath(
                    context.ProjectRoot,
                    document.BinPath)),
                $"manifest bin workbook '{documentName}'",
                outputPath);
            RejectIfSame(
                outputIdentity,
                pathIdentityResolver.Resolve(ResolveManifestPath(
                    context.ProjectRoot,
                    document.PublishPath)),
                $"manifest publish workbook '{documentName}'",
                outputPath);
        }

        return new BuildSourceSnapshotValidatedPaths(
            snapshotIdentity.OperationPath,
            outputIdentity.OperationPath);
    }

    private static void RejectIfSame(
        FileSystemPathIdentity outputIdentity,
        FileSystemPathIdentity protectedIdentity,
        string protectedDescription,
        string outputPath)
    {
        if (FileSystemPathIdentityRelations.Same(outputIdentity, protectedIdentity))
        {
            throw new InvalidOperationException(
                $"Snapshot build output must be distinct from {protectedDescription}: {outputPath}");
        }
    }

    private static string ResolveManifestPath(string projectRoot, string path)
    {
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(
            Path.IsPathRooted(normalizedPath)
                ? normalizedPath
                : Path.Combine(projectRoot, normalizedPath));
    }
}
