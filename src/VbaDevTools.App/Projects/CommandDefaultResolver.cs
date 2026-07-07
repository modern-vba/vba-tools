using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

public static class CommandDefaultResolver
{
    private static readonly HashSet<string> SupportedTestFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "ndjson",
        "json",
        "text"
    };

    public static string ResolveTestFormat(ProjectManifest manifest, string? optionValue)
    {
        var format = string.IsNullOrWhiteSpace(optionValue)
            ? manifest.CommandDefaults?.Test?.Format
            : optionValue;

        if (string.IsNullOrWhiteSpace(format))
        {
            return "ndjson";
        }

        if (!SupportedTestFormats.Contains(format))
        {
            throw new ProjectManifestException($"Unsupported test format default '{format}'.");
        }

        return format;
    }
}
