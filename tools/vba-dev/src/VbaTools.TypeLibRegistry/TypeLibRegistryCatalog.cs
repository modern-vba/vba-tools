namespace VbaTools.TypeLibRegistry;

public sealed class TypeLibRegistryCatalog
{
    private readonly IReadOnlyDictionary<string, TypeLibRegistryCatalogName> namesByLookup;

    internal TypeLibRegistryCatalog(
        bool complete,
        IReadOnlyList<TypeLibRegistryCatalogName> names,
        IReadOnlyList<TypeLibRegistryCatalogWarning> warnings,
        TypeLibRegistryCatalogDiagnostic? diagnostic)
    {
        Complete = complete;
        Names = names;
        Warnings = warnings;
        Diagnostic = diagnostic;
        namesByLookup = names.ToDictionary(
            name => name.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public bool Complete { get; }

    public IReadOnlyList<TypeLibRegistryCatalogName> Names { get; }

    public IReadOnlyList<TypeLibRegistryCatalogWarning> Warnings { get; }

    public TypeLibRegistryCatalogDiagnostic? Diagnostic { get; }

    public TypeLibRegistryCatalogName? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return namesByLookup.GetValueOrDefault(name.Trim());
    }
}

public sealed record TypeLibRegistryCatalogName(
    string Name,
    IReadOnlyList<TypeLibRegistryLineage> Lineages);

public sealed record TypeLibRegistryLineage(
    string Guid,
    IReadOnlyList<TypeLibRegistryVersion> Versions);

public sealed record TypeLibRegistryVersion(
    int Major,
    int Minor,
    IReadOnlyList<TypeLibRegistryLocale> Locales);

public sealed record TypeLibRegistryLocale(
    int Lcid,
    IReadOnlyList<TypeLibRegistryPath> Paths);

public sealed record TypeLibRegistryPath(string Platform, string Path);

public sealed record TypeLibRegistryCatalogWarning(string Code, string Message, int Count);

public sealed record TypeLibRegistryCatalogDiagnostic(string Code, string Message);
