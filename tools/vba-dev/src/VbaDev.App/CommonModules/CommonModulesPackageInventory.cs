namespace VbaDev.App.CommonModules;

/// <summary>
/// Adapts ordinary live files and captured names to the same deterministic inventory order.
/// </summary>
internal static class CommonModulesPackageInventory
{
    internal static IReadOnlyList<FileInfo> ReadLive(string repositoryPath)
    {
        try
        {
            var repository = new DirectoryInfo(repositoryPath);
            if (!repository.Exists)
            {
                throw new CommonModulesManifestException(
                    $"CommonModulesRepository was not found: {repositoryPath}");
            }

            if (repository.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package root must be an ordinary directory: {repositoryPath}");
            }

            return NormalizeLive(repository.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false
                }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package inventory could not be read: {Path.GetFullPath(repositoryPath)}");
        }
    }

    internal static IReadOnlyList<FileInfo> NormalizeLive(IEnumerable<FileSystemInfo> entries)
    {
        var files = new List<FileInfo>();
        foreach (var entry in OrderUniqueEntries(entries, entry => entry.Name))
        {
            if (entry is not FileInfo file
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package entry must be an ordinary file: {entry.FullName}");
            }

            files.Add(file);
        }

        return files;
    }

    internal static IReadOnlyList<string> NormalizeCapturedNames(IEnumerable<string> names)
        => OrderUniqueEntries(names, name => name).ToArray();

    private static IEnumerable<T> OrderUniqueEntries<T>(
        IEnumerable<T> entries,
        Func<T, string> getName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(getName, StringComparer.Ordinal))
        {
            var name = getName(entry);
            if (!names.Add(name))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package contains case-insensitive duplicate entry '{name}'.");
            }

            yield return entry;
        }
    }
}
