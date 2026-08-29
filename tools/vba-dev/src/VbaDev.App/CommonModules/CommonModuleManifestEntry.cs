namespace VbaDev.App.CommonModules;

/// <summary>
/// Describes one source file entry from a CommonModules manifest.
/// </summary>
/// <param name="ModuleFile">The repository-relative module file path.</param>
/// <param name="Categories">The manifest categories assigned to the module.</param>
/// <param name="Dependencies">The module file names that must be installed before this entry.</param>
/// <param name="RequiredReferences">The ordered external VBA reference names required directly by this entry.</param>
public sealed record CommonModuleManifestEntry(
    string ModuleFile,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> RequiredReferences)
{
    /// <summary>
    /// Gets the canonical extensionless CommonModule name.
    /// </summary>
    public string Name => Path.GetFileNameWithoutExtension(ModuleFile);

    /// <summary>
    /// Gets the flat exported source file name recorded in a project manifest.
    /// </summary>
    public string InstalledModuleFile => Path.GetFileName(ModuleFile);

    /// <summary>
    /// Gets whether publish excludes the installed source.
    /// </summary>
    public bool TestOnly => HasCategory("test-foundation") || HasCategory("test-double");

    /// <summary>
    /// Gets whether this entry has a runtime primary role.
    /// </summary>
    public bool RuntimeRole => HasCategory("runtime-baseline") || HasCategory("optional");

    /// <summary>
    /// Gets whether this entry has a test primary role.
    /// </summary>
    public bool TestRole => HasCategory("test-foundation") || HasCategory("test-double");

    /// <summary>
    /// Determines whether the entry belongs to a category, ignoring case.
    /// </summary>
    /// <param name="category">The category name to check.</param>
    /// <returns>True when the entry declares the category.</returns>
    public bool HasCategory(string category)
        => Categories.Any(value => string.Equals(value, category, StringComparison.OrdinalIgnoreCase));
}
