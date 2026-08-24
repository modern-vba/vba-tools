using VbaDev.Domain;

namespace VbaDev.App.Projects;

internal sealed class NewProjectAncestorSourceSetIsolation
{
    private readonly IProjectManifestStore manifestStore;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

    public NewProjectAncestorSourceSetIsolation(IProjectManifestStore manifestStore)
        : this(manifestStore, new FileSystemPathIdentityResolver())
    {
    }

    internal NewProjectAncestorSourceSetIsolation(
        IProjectManifestStore manifestStore,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        this.manifestStore = manifestStore;
        this.pathIdentityResolver = pathIdentityResolver;
    }

    public FileSystemPathIdentity ValidateInitial(string projectRoot)
    {
        var projectIdentity = ResolveProjectIdentity(projectRoot);
        ValidateAncestorManifests(projectRoot, projectIdentity);
        return projectIdentity;
    }

    public void ValidateFinal(
        string projectRoot,
        FileSystemPathIdentity initialIdentity)
    {
        var currentIdentity = ResolveProjectIdentity(projectRoot);
        if (!HasContinuousProjectRootIdentity(
                initialIdentity,
                currentIdentity)
            || !IsExistingDirectory(currentIdentity.OperationPath))
        {
            throw new ProjectManifestException(
                $"New project root filesystem identity changed before manifest commit: {projectRoot}");
        }

        ValidateAncestorManifests(projectRoot, currentIdentity);
    }

    private static bool HasContinuousProjectRootIdentity(
        FileSystemPathIdentity initialIdentity,
        FileSystemPathIdentity currentIdentity)
    {
        var sameCanonicalPath = Path.TrimEndingDirectorySeparator(
                initialIdentity.CanonicalPath)
            .Equals(
                Path.TrimEndingDirectorySeparator(currentIdentity.CanonicalPath),
                StringComparison.OrdinalIgnoreCase);
        if (!sameCanonicalPath)
        {
            return false;
        }

        if (initialIdentity.ObjectIdentity is not null)
        {
            return currentIdentity.ObjectIdentity is not null
                && initialIdentity.ObjectIdentity == currentIdentity.ObjectIdentity;
        }

        return !OperatingSystem.IsWindows()
            || currentIdentity.ObjectIdentity is not null;
    }

    private static bool IsExistingDirectory(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void ValidateAncestorManifests(
        string projectRoot,
        FileSystemPathIdentity projectIdentity)
    {
        foreach (var ancestorDirectory in EnumerateAncestorDirectories(
                     projectRoot,
                     projectIdentity.OperationPath))
        {
            var manifestPath = Path.Combine(
                ancestorDirectory,
                ProjectManifest.ManifestFileName);
            if (!ManifestEntryExists(manifestPath))
            {
                continue;
            }

            var manifest = manifestStore.Load(manifestPath);
            IReadOnlyDictionary<string, FileSystemPathIdentity> sourceRoots;
            try
            {
                sourceRoots = DocumentSourceSetIsolationValidator.ResolveAndValidate(
                    manifest,
                    manifestPath,
                    manifestPath,
                    pathIdentityResolver);
            }
            catch (VbaProjectManifestException ex)
            {
                throw new ProjectManifestException(ex.Message, ex);
            }

            foreach (var (documentName, document) in manifest.Documents)
            {
                if (!FileSystemPathIdentityRelations.SameOrDescendant(
                        projectIdentity,
                        sourceRoots[documentName]))
                {
                    continue;
                }

                throw new ProjectManifestException(
                    $"New project root '{projectRoot}' is inside ancestor document source set " +
                    $"document '{documentName}' sourcePath '{document.SourcePath}' in '{manifestPath}'.");
            }
        }
    }

    private FileSystemPathIdentity ResolveProjectIdentity(string projectRoot)
    {
        try
        {
            return pathIdentityResolver.Resolve(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException
            or InvalidOperationException)
        {
            throw new ProjectManifestException(
                $"New project root does not have a safely resolvable filesystem identity: {projectRoot}",
                ex);
        }
    }

    private static IReadOnlyList<string> EnumerateAncestorDirectories(
        string lexicalProjectRoot,
        string canonicalProjectRoot)
    {
        var ancestors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAncestors(lexicalProjectRoot, ancestors, seen);
        AddAncestors(canonicalProjectRoot, ancestors, seen);
        return ancestors;
    }

    private static void AddAncestors(
        string projectRoot,
        ICollection<string> ancestors,
        ISet<string> seen)
    {
        var current = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)));
        while (!string.IsNullOrEmpty(current))
        {
            if (seen.Add(current))
            {
                ancestors.Add(current);
            }

            var parent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static bool ManifestEntryExists(string manifestPath)
    {
        try
        {
            var attributes = File.GetAttributes(manifestPath);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                throw new ProjectManifestException(
                    $"Ancestor project manifest path is not a file: {manifestPath}");
            }

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (ProjectManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw new ProjectManifestException(
                $"Ancestor project manifest could not be observed safely: {manifestPath}",
                ex);
        }
    }
}
