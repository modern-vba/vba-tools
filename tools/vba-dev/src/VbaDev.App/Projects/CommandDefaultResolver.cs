using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Resolves effective command option values from explicit options and project manifest defaults.
/// </summary>
public static class CommandDefaultResolver
{
    private static readonly HashSet<string> SupportedTestFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "ndjson",
        "text"
    };

    /// <summary>
    /// Resolves the test output format from an explicit option or manifest default.
    /// </summary>
    /// <param name="manifest">The project manifest that may define command defaults.</param>
    /// <param name="optionValue">The explicit command-line format option.</param>
    /// <returns>The effective test output format.</returns>
    public static string ResolveTestFormat(ProjectManifest manifest, string? optionValue)
    {
        var format = string.IsNullOrWhiteSpace(optionValue)
            ? manifest.CommandDefaults?.Test?.Format
            : optionValue;

        if (string.IsNullOrWhiteSpace(format))
        {
            return "text";
        }

        if (!SupportedTestFormats.Contains(format))
        {
            throw new ProjectManifestException($"Unsupported test format default '{format}'.");
        }

        return format;
    }
}
