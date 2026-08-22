using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Resolves effective command option values from explicit options and project manifest defaults.
/// </summary>
public static class CommandDefaultResolver
{
    private static readonly TimeSpan DefaultWorkbookOpenTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DefaultWorkbookSaveTimeout = TimeSpan.FromSeconds(300);

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

    /// <summary>
    /// Resolves the workbook-open timeout from the project manifest or the built-in default.
    /// </summary>
    /// <param name="manifest">The project manifest that may define Excel automation defaults.</param>
    /// <returns>The effective workbook-open timeout.</returns>
    public static TimeSpan ResolveWorkbookOpenTimeout(ProjectManifest manifest)
        => manifest.CommandDefaults?.ExcelAutomation?.WorkbookOpenTimeoutSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultWorkbookOpenTimeout;

    /// <summary>
    /// Resolves the workbook-save timeout from the project manifest or the built-in default.
    /// </summary>
    /// <param name="manifest">The project manifest that may define Excel automation defaults.</param>
    /// <returns>The effective workbook-save timeout.</returns>
    public static TimeSpan ResolveWorkbookSaveTimeout(ProjectManifest manifest)
        => manifest.CommandDefaults?.ExcelAutomation?.WorkbookSaveTimeoutSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultWorkbookSaveTimeout;
}
