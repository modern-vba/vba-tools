namespace VbaDev.Domain;

/// <summary>
/// Resolves and validates filesystem identities for one manifest's document source sets.
/// </summary>
public static class DocumentSourceSetIsolationValidator
{
    /// <summary>
    /// Resolves source roots and rejects source sets that identify the same filesystem root.
    /// </summary>
    /// <param name="manifest">The structurally valid project manifest.</param>
    /// <param name="manifestPath">The absolute or relative manifest path.</param>
    /// <param name="manifestName">The display name retained in diagnostics.</param>
    /// <returns>Filesystem identities keyed by the original document names.</returns>
    public static IReadOnlyDictionary<string, FileSystemPathIdentity> ResolveAndValidate(
        ProjectManifest manifest,
        string manifestPath,
        string manifestName)
        => ResolveAndValidate(
            manifest,
            manifestPath,
            manifestName,
            new FileSystemPathIdentityResolver());

    /// <summary>
    /// Resolves source roots with an explicit filesystem identity boundary.
    /// </summary>
    /// <param name="manifest">The structurally valid project manifest.</param>
    /// <param name="manifestPath">The absolute or relative manifest path.</param>
    /// <param name="manifestName">The display name retained in diagnostics.</param>
    /// <param name="pathIdentityResolver">The filesystem identity resolver.</param>
    /// <returns>Filesystem identities keyed by the original document names.</returns>
    public static IReadOnlyDictionary<string, FileSystemPathIdentity> ResolveAndValidate(
        ProjectManifest manifest,
        string manifestPath,
        string manifestName,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pathIdentityResolver);

        string manifestDirectory;
        try
        {
            var fullManifestPath = Path.GetFullPath(manifestPath);
            manifestDirectory = Path.GetDirectoryName(fullManifestPath)
                ?? throw new InvalidOperationException(
                    $"Project manifest path has no parent directory: {manifestPath}");
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            throw new VbaProjectManifestException(
                $"Project manifest path does not have a safely resolvable filesystem identity: {manifestName}",
                ex);
        }

        var roots = new Dictionary<string, SourceRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in manifest.Documents)
        {
            FileSystemPathIdentity identity;
            try
            {
                var sourcePath = document.SourcePath.Replace('/', Path.DirectorySeparatorChar);
                var absoluteSourcePath = Path.GetFullPath(
                    Path.IsPathRooted(sourcePath)
                        ? sourcePath
                        : Path.Combine(manifestDirectory, sourcePath));
                identity = pathIdentityResolver.Resolve(absoluteSourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException
                or InvalidOperationException)
            {
                throw new VbaProjectManifestException(
                    $"Document '{documentName}' sourcePath '{document.SourcePath}' does not have a safely resolvable filesystem identity in project manifest: {manifestName}",
                    ex);
            }

            roots.Add(documentName, new SourceRoot(documentName, document.SourcePath, identity));
        }

        var orderedRoots = roots.Values.ToArray();
        for (var leftIndex = 0; leftIndex < orderedRoots.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < orderedRoots.Length; rightIndex++)
            {
                var left = orderedRoots[leftIndex];
                var right = orderedRoots[rightIndex];
                if (FileSystemPathIdentityRelations.RootsOverlap(
                        left.Identity,
                        right.Identity))
                {
                    throw new VbaProjectManifestException(
                        $"Project manifest document source roots overlap: document '{left.DocumentName}' sourcePath '{left.SourcePath}' conflicts with document '{right.DocumentName}' sourcePath '{right.SourcePath}': {manifestName}");
                }
            }
        }

        return roots.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Identity,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SourceRoot(
        string DocumentName,
        string SourcePath,
        FileSystemPathIdentity Identity);
}
