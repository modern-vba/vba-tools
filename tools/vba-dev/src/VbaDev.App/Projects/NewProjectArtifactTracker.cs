using VbaDev.Domain;

namespace VbaDev.App.Projects;

internal sealed class NewProjectArtifactTracker
{
    private readonly List<CreatedFile> createdFiles = [];
    private readonly List<CreatedDirectory> createdDirectories = [];
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver =
        new FileSystemPathIdentityResolver();

    public void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var missingDirectories = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new IOException($"New project directory path is occupied by a file: {current}");
            }

            missingDirectories.Push(current);
            var parent = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"New project directory could not be created safely: {fullPath}");
            }

            current = parent;
        }

        foreach (var missingDirectory in missingDirectories)
        {
            Directory.CreateDirectory(missingDirectory);
            createdDirectories.Add(new CreatedDirectory(
                missingDirectory,
                pathIdentityResolver.Resolve(missingDirectory)));
        }
    }

    public void RecordCreatedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        createdFiles.Add(new CreatedFile(
            fullPath,
            File.ReadAllBytes(fullPath),
            pathIdentityResolver.Resolve(fullPath)));
    }

    public void Rollback()
    {
        foreach (var file in createdFiles.AsEnumerable().Reverse())
        {
            TryDeleteUnchangedFile(file);
        }

        foreach (var directory in createdDirectories.AsEnumerable().Reverse())
        {
            TryDeleteEmptyDirectory(directory);
        }
    }

    private void TryDeleteUnchangedFile(CreatedFile file)
    {
        try
        {
            if (File.Exists(file.Path)
                && HasSameObjectIdentity(
                    file.Identity,
                    pathIdentityResolver.Resolve(file.Path))
                && File.ReadAllBytes(file.Path).AsSpan().SequenceEqual(file.Contents))
            {
                File.Delete(file.Path);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException)
        {
        }
    }

    private void TryDeleteEmptyDirectory(CreatedDirectory directory)
    {
        try
        {
            if (Directory.Exists(directory.Path)
                && HasSameObjectIdentity(
                    directory.Identity,
                    pathIdentityResolver.Resolve(directory.Path))
                && !Directory.EnumerateFileSystemEntries(directory.Path).Any())
            {
                Directory.Delete(directory.Path);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException)
        {
        }
    }

    private static bool HasSameObjectIdentity(
        FileSystemPathIdentity expected,
        FileSystemPathIdentity current)
    {
        if (expected.ObjectIdentity is not null
            || current.ObjectIdentity is not null)
        {
            return expected.ObjectIdentity is not null
                && current.ObjectIdentity is not null
                && expected.ObjectIdentity == current.ObjectIdentity;
        }

        return FileSystemPathIdentityRelations.Same(expected, current);
    }

    private sealed record CreatedFile(
        string Path,
        byte[] Contents,
        FileSystemPathIdentity Identity);

    private sealed record CreatedDirectory(
        string Path,
        FileSystemPathIdentity Identity);
}
