namespace VbaDev.Domain;

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

    public static bool HasUsableCatalog(string referenceName)
        => BundledCatalogReferenceNames.Contains(referenceName);
}
