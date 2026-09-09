using System.Collections.Immutable;

namespace VbaTools.TypeLibRegistry;

/// <summary>Shares one observed registry catalog between an invocation's consumers.</summary>
public sealed class TypeLibRegistryCatalogSnapshot(ITypeLibRegistryCatalogReader reader) : ITypeLibRegistryCatalogReader
{
    private readonly Lazy<TypeLibRegistryCatalog> catalog = new(() => Capture(reader.Read()), LazyThreadSafetyMode.ExecutionAndPublication);

    public TypeLibRegistryCatalog Read() => catalog.Value;

    private static TypeLibRegistryCatalog Capture(TypeLibRegistryCatalog source)
        => new(source.Complete, source.Names.Select(name => name with
        {
            Lineages = name.Lineages.Select(lineage => lineage with
            {
                Versions = lineage.Versions.Select(version => version with
                {
                    Locales = version.Locales.Select(locale => locale with
                    {
                        Paths = locale.Paths.ToImmutableArray()
                    }).ToImmutableArray()
                }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray(), source.Warnings.ToImmutableArray(), source.Diagnostic);
}
