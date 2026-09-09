using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

public sealed class TypeLibRegistryCatalogSnapshotTests
{
    [Fact]
    public void InvocationSnapshotKeepsAcceptedNestedRegistryFactsAfterTheReaderChangesItsLists()
    {
        var paths = new List<TypeLibRegistryPath> { new("win64", "C:/accepted/Library.dll") };
        var locales = new List<TypeLibRegistryLocale> { new(1033, paths) };
        var versions = new List<TypeLibRegistryVersion> { new(4, 2, locales) };
        var lineages = new List<TypeLibRegistryLineage> { new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", versions) };
        var names = new List<TypeLibRegistryCatalogName> { new("Example Library", lineages) };
        var warnings = new List<TypeLibRegistryCatalogWarning> { new("example", "Observed warning.", 1) };
        var reader = new Reader(new(true, names, warnings, null));
        var snapshot = new TypeLibRegistryCatalogSnapshot(reader);
        var first = snapshot.Read();

        paths.Clear();
        locales.Clear();
        versions.Clear();
        lineages.Clear();
        names.Clear();
        warnings.Clear();
        var second = snapshot.Read();

        Assert.Same(first, second);
        Assert.Equal(1, reader.Reads);
        Assert.Equal("Example Library", Assert.Single(second.Names).Name);
        var observedLineage = Assert.Single(second.Find("example library")!.Lineages);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", observedLineage.Guid);
        var observedVersion = Assert.Single(observedLineage.Versions);
        Assert.Equal((4, 2), (observedVersion.Major, observedVersion.Minor));
        Assert.Equal(new TypeLibRegistryLocation(1033, "win64", "C:/accepted/Library.dll"),
            Assert.Single(observedVersion.GetOrderedLocations()));
        Assert.Equal("Observed warning.", Assert.Single(second.Warnings).Message);
    }

    private sealed class Reader(TypeLibRegistryCatalog catalog) : ITypeLibRegistryCatalogReader
    {
        internal int Reads { get; private set; }
        public TypeLibRegistryCatalog Read()
        {
            Reads++;
            return catalog;
        }
    }
}
