using VbaLanguageServer.Syntax;
using System.Text;
using System.Text.Json;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Reads and validates the tab-separated CommonModules manifest from a repository directory.
/// </summary>
public sealed class CommonModulesManifestReader
{
    private const string Header = "ModuleFile\tCategories\tDependencies\tRequiredReferences";
    private static readonly HashSet<string> CanonicalCategories = new(StringComparer.Ordinal)
    {
        "runtime-baseline",
        "runtime-baseline,public-udf",
        "test-foundation",
        "optional",
        "optional,public-udf",
        "test-double"
    };

    /// <summary>
    /// The expected CommonModules manifest file name.
    /// </summary>
    public const string ManifestFileName = "common-modules-manifest.tsv";

    /// <summary>
    /// Loads the CommonModules manifest entries from a repository path.
    /// </summary>
    /// <param name="commonModulesRepositoryPath">The CommonModulesRepository directory.</param>
    /// <returns>The validated manifest entries in file order.</returns>
    public IReadOnlyList<CommonModuleManifestEntry> Load(string commonModulesRepositoryPath)
    {
        var manifestPath = Path.Combine(commonModulesRepositoryPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new CommonModulesManifestException($"CommonModules manifest was not found: {manifestPath}");
        }

        var text = ReadManifestText(manifestPath);
        var lines = text[..^2].Split("\r\n", StringSplitOptions.None);
        var entries = new List<CommonModuleManifestEntry>();
        var headerSeen = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineNumber = lineIndex + 1;
            if (!headerSeen && line.StartsWith('#'))
            {
                ValidateLeadingComment(line, lineNumber);
                continue;
            }

            if (!headerSeen)
            {
                if (line != Header)
                {
                    throw new CommonModulesManifestException($"Invalid CommonModules manifest header at line {lineNumber}.");
                }

                headerSeen = true;
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length != 4)
            {
                throw new CommonModulesManifestException($"Invalid CommonModules manifest record at line {lineNumber}.");
            }

            entries.Add(new CommonModuleManifestEntry(
                columns[0],
                ParseCategories(columns[1], lineNumber),
                ParseDependencies(columns[2], columns[0], lineNumber),
                ParseRequiredReferences(columns[3], lineNumber)));
        }

        if (!headerSeen)
        {
            throw new CommonModulesManifestException("CommonModules manifest header was not found.");
        }

        if (entries.Count == 0)
        {
            throw new CommonModulesManifestException(
                "CommonModules manifest must contain at least one module row.");
        }

        Validate(entries);
        return entries;
    }

    private static void ValidateLeadingComment(string line, int lineNumber)
    {
        if (line.Any(char.IsControl)
            || (line.Length > 0 && char.IsWhiteSpace(line[^1])))
        {
            throw new CommonModulesManifestException(
                $"Invalid CommonModules manifest leading comment at line {lineNumber}.");
        }
    }

    private static string ReadManifestText(string manifestPath)
    {
        var bytes = File.ReadAllBytes(manifestPath);
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xfe)
        {
            throw new CommonModulesManifestException(
                "CommonModules manifest must use UTF-16LE BOM encoding.");
        }

        if ((bytes.Length - 2) % 2 != 0)
        {
            throw new CommonModulesManifestException(
                "CommonModules manifest has an invalid UTF-16LE byte count.");
        }

        try
        {
            var text = new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
            if (!text.EndsWith("\r\n", StringComparison.Ordinal)
                || text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                throw new CommonModulesManifestException(
                    "CommonModules manifest must end with exactly one final CRLF.");
            }

            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\r')
                {
                    if (index + 1 >= text.Length || text[index + 1] != '\n')
                    {
                        throw new CommonModulesManifestException(
                            "CommonModules manifest must use CRLF line endings throughout.");
                    }

                    index++;
                    continue;
                }

                if (text[index] == '\n')
                {
                    throw new CommonModulesManifestException(
                        "CommonModules manifest must use CRLF line endings throughout.");
                }
            }

            return text;
        }
        catch (DecoderFallbackException)
        {
            throw new CommonModulesManifestException(
                "CommonModules manifest contains invalid UTF-16LE text.");
        }
    }

    private static void Validate(IReadOnlyList<CommonModuleManifestEntry> entries)
    {
        var byFileName = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ValidateModuleFile(entry.ModuleFile);

            if (!byFileName.TryAdd(entry.ModuleFile, entry))
            {
                throw new CommonModulesManifestException($"CommonModules manifest duplicates module '{entry.ModuleFile}'.");
            }
        }

        foreach (var entry in entries)
        {
            foreach (var dependency in entry.Dependencies)
            {
                if (!byFileName.TryGetValue(dependency, out var dependencyEntry))
                {
                    throw new CommonModulesManifestException($"CommonModules manifest references unknown dependency '{dependency}' from '{entry.ModuleFile}'.");
                }

                if (!dependency.Equals(dependencyEntry.ModuleFile, StringComparison.Ordinal))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules manifest dependency '{dependency}' must use exact ModuleFile spelling '{dependencyEntry.ModuleFile}'.");
                }

                if (entry.RuntimeRole && dependencyEntry.TestRole)
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules manifest runtime-role entry '{entry.ModuleFile}' cannot depend on test-role entry '{dependencyEntry.ModuleFile}'.");
                }
            }
        }
    }

    private static void ValidateModuleFile(string moduleFile)
    {
        var extension = Path.GetExtension(moduleFile);
        var name = Path.GetFileNameWithoutExtension(moduleFile);
        var hasCanonicalExtension = extension is ".bas" or ".cls" or ".frm";
        var containsInvalidCharacter = moduleFile.Any(character =>
            char.IsControl(character)
            || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or ',');
        if (moduleFile.Length == 0
            || !moduleFile.Equals(moduleFile.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(moduleFile)
            || !Path.GetFileName(moduleFile).Equals(moduleFile, StringComparison.Ordinal)
            || containsInvalidCharacter
            || !hasCanonicalExtension
            || name.Length == 0
            || !name.Equals(name.Trim(), StringComparison.Ordinal)
            || name.Contains(".", StringComparison.Ordinal))
        {
            throw new CommonModulesManifestException(
                $"CommonModules manifest contains invalid flat ModuleFile '{moduleFile}'.");
        }
    }

    private static IReadOnlyList<string> ParseCategories(string value, int lineNumber)
    {
        if (!CanonicalCategories.Contains(value))
        {
            throw new CommonModulesManifestException(
                $"CommonModules manifest contains invalid Categories at line {lineNumber}.");
        }

        return value.Split(',');
    }

    private static IReadOnlyList<string> ParseDependencies(
        string value,
        string moduleFile,
        int lineNumber)
    {
        if (value.Length == 0)
        {
            return [];
        }

        if (value.Any(char.IsWhiteSpace))
        {
            throw new CommonModulesManifestException(
                $"CommonModules manifest requires whitespace-free Dependencies at line {lineNumber}.");
        }

        var dependencies = value.Split(',');
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            if (dependency.Length == 0)
            {
                throw new CommonModulesManifestException(
                    $"CommonModules manifest contains an empty dependency at line {lineNumber}.");
            }

            if (!unique.Add(dependency))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules manifest contains duplicate dependency '{dependency}' at line {lineNumber}.");
            }

            if (dependency.Equals(moduleFile, StringComparison.OrdinalIgnoreCase))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules manifest contains self dependency '{dependency}' at line {lineNumber}.");
            }
        }

        return dependencies;
    }

    private static IReadOnlyList<string> ParseRequiredReferences(string value, int lineNumber)
    {
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new CommonModulesManifestException(
                    $"Invalid RequiredReferences JSON array at line {lineNumber}.");
            }

            var references = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in json.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    throw new CommonModulesManifestException(
                        $"Invalid RequiredReferences JSON array at line {lineNumber}.");
                }

                var reference = element.GetString()!;
                if (reference.Length == 0)
                {
                    throw new CommonModulesManifestException(
                        $"RequiredReferences must be nonempty at line {lineNumber}.");
                }

                if (!reference.Equals(reference.Trim(), StringComparison.Ordinal))
                {
                    throw new CommonModulesManifestException(
                        $"RequiredReferences must already be trimmed at line {lineNumber}.");
                }

                if (reference.Equals(
                    "Visual Basic For Applications",
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new CommonModulesManifestException(
                        $"RequiredReferences must not declare the always-active VBA standard library at line {lineNumber}.");
                }

                if (!unique.Add(reference))
                {
                    throw new CommonModulesManifestException(
                        $"RequiredReferences contains duplicate name '{reference}' at line {lineNumber}.");
                }

                references.Add(reference);
            }

            return references;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new CommonModulesManifestException(
                $"Invalid RequiredReferences JSON array at line {lineNumber}.");
        }
    }
}
