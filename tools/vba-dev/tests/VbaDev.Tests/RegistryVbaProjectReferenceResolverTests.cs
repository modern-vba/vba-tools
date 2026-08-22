using VbaDev.Infrastructure.Workbooks;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

public sealed class RegistryVbaProjectReferenceResolverTests
{
    [Fact]
    public void ResolveReturnsTheRepresentativeNameAndHighestVersionFromEveryGuidLineage()
    {
        var catalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    "WIDGET LIBRARY",
                    [
                        new TypeLibRegistryLineage(
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            [
                                new TypeLibRegistryVersion(9, 15, []),
                                new TypeLibRegistryVersion(10, 16, [])
                            ]),
                        new TypeLibRegistryLineage(
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            [new TypeLibRegistryVersion(2, 0, [])])
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var resolver = new RegistryVbaProjectReferenceResolver(
            new FakeTypeLibRegistryCatalogReader(catalog));

        var batch = resolver.Resolve([" widget library "]);

        Assert.True(batch.Complete);
        var resolution = Assert.Single(batch.References);
        Assert.Equal("widget library", resolution.RequestedName);
        Assert.Equal("WIDGET LIBRARY", resolution.RegisteredName);
        Assert.Collection(
            resolution.Matches,
            match => Assert.Equal(
                new("WIDGET LIBRARY", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 10, 16),
                match),
            match => Assert.Equal(
                new("WIDGET LIBRARY", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 2, 0),
                match));
    }

    [Fact]
    public void ResolveReusesOneCatalogSnapshotAcrossBatches()
    {
        var catalog = new TypeLibRegistryCatalog(
            complete: true,
            names: [],
            warnings: [],
            diagnostic: null);
        var reader = new FakeTypeLibRegistryCatalogReader(catalog);
        var resolver = new RegistryVbaProjectReferenceResolver(reader);

        resolver.Resolve(["First Library"]);
        resolver.Resolve(["Second Library"]);

        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class FakeTypeLibRegistryCatalogReader(TypeLibRegistryCatalog catalog)
        : ITypeLibRegistryCatalogReader
    {
        public int ReadCount { get; private set; }

        public TypeLibRegistryCatalog Read()
        {
            ReadCount++;
            return catalog;
        }
    }
}
