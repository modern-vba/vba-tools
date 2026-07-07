namespace VbaDevTools.App.CommonModules;

public sealed class CommonModulesManifestReader
{
    public const string ManifestFileName = "common-modules-manifest.tsv";

    public IReadOnlyList<CommonModuleManifestEntry> Load(string commonModulesRepositoryPath)
    {
        var manifestPath = Path.Combine(commonModulesRepositoryPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new CommonModulesManifestException($"CommonModules manifest was not found: {manifestPath}");
        }

        var entries = new List<CommonModuleManifestEntry>();
        var headerSeen = false;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(manifestPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!headerSeen)
            {
                if (line != "ModuleFile\tCategories\tDependencies")
                {
                    throw new CommonModulesManifestException($"Invalid CommonModules manifest header at line {lineNumber}.");
                }

                headerSeen = true;
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length != 3)
            {
                throw new CommonModulesManifestException($"Invalid CommonModules manifest record at line {lineNumber}.");
            }

            entries.Add(new CommonModuleManifestEntry(
                columns[0].Trim(),
                SplitList(columns[1]),
                SplitList(columns[2])));
        }

        if (!headerSeen)
        {
            throw new CommonModulesManifestException("CommonModules manifest header was not found.");
        }

        Validate(entries);
        return entries;
    }

    private static void Validate(IReadOnlyList<CommonModuleManifestEntry> entries)
    {
        var byFileName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.ModuleFile))
            {
                throw new CommonModulesManifestException("CommonModules manifest contains an empty ModuleFile value.");
            }

            if (!byFileName.Add(entry.ModuleFile))
            {
                throw new CommonModulesManifestException($"CommonModules manifest duplicates module '{entry.ModuleFile}'.");
            }
        }

        foreach (var entry in entries)
        {
            foreach (var dependency in entry.Dependencies)
            {
                if (!byFileName.Contains(dependency))
                {
                    throw new CommonModulesManifestException($"CommonModules manifest references unknown dependency '{dependency}' from '{entry.ModuleFile}'.");
                }
            }
        }
    }

    private static IReadOnlyList<string> SplitList(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToArray();
}
