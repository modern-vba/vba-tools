namespace VbaDev.Domain;

/// <summary>
/// Reports whether this toolset can provide bundled editor metadata for a VBA project reference.
/// </summary>
public static class VbaProjectReferenceCatalogAvailability
{
    private static readonly HashSet<string> BundledCatalogReferenceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Visual Basic For Applications",
        "Microsoft Excel 16.0 Object Library",
        "Microsoft Scripting Runtime",
        "Microsoft Office 16.0 Object Library",
        "Microsoft Outlook 16.0 Object Library"
    };

    /// <summary>
    /// Determines whether a reference name has a usable bundled catalog.
    /// </summary>
    /// <param name="referenceName">The human-visible VBA project reference name.</param>
    /// <returns>True when a bundled catalog is available for the reference name.</returns>
    public static bool HasUsableCatalog(string referenceName)
        => BundledCatalogReferenceNames.Contains(referenceName);
}
