namespace VbaDev.App.CommonModules;

/// <summary>
/// Describes one source file entry from a CommonModules manifest.
/// </summary>
/// <param name="ModuleFile">The repository-relative module file path.</param>
/// <param name="Categories">The manifest categories assigned to the module.</param>
/// <param name="Dependencies">The module file names that must be installed before this entry.</param>
public sealed record CommonModuleManifestEntry(
    string ModuleFile,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Dependencies)
{
    /// <summary>
    /// Determines whether the entry belongs to a category, ignoring case.
    /// </summary>
    /// <param name="category">The category name to check.</param>
    /// <returns>True when the entry declares the category.</returns>
    public bool HasCategory(string category)
        => Categories.Any(value => string.Equals(value, category, StringComparison.OrdinalIgnoreCase));
}
