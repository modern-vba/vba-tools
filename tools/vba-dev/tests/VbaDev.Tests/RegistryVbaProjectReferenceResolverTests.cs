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
    public void ResolveRetainsEveryVersionInDescendingProbeFallbackOrder()
    {
        var catalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    "Widget Library",
                    [
                        new TypeLibRegistryLineage(
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            [
                                new TypeLibRegistryVersion(9, 15, []),
                                new TypeLibRegistryVersion(10, 16, [])
                            ])
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var resolver = new RegistryVbaProjectReferenceResolver(
            new FakeTypeLibRegistryCatalogReader(catalog));

        var resolution = Assert.Single(resolver.Resolve(["Widget Library"]).References);

        var lineage = Assert.Single(resolution.CandidateLineages);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", lineage.Guid);
        Assert.Equal(
            [(10, 16), (9, 15)],
            lineage.Versions.Select(version => (version.Major, version.Minor)));
        Assert.Equal(
            [(10, 16)],
            resolution.Matches.Select(match => (match.Major, match.Minor)));
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

    [Fact]
    public void ResolveAvailableReturnsEveryCatalogDescriptionFromOneSnapshot()
    {
        var warning = new TypeLibRegistryCatalogWarning(
            "malformedRegistrationsSkipped",
            "Skipped one malformed TypeLib registration.",
            1);
        var catalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    "Alpha Library",
                    [
                        new TypeLibRegistryLineage(
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                            [new TypeLibRegistryVersion(1, 0, [])])
                    ]),
                new TypeLibRegistryCatalogName("Broken Library", [])
            ],
            warnings: [warning],
            diagnostic: null);
        var reader = new FakeTypeLibRegistryCatalogReader(catalog);
        var resolver = new RegistryVbaProjectReferenceResolver(reader);

        var first = resolver.ResolveAvailable();
        var second = resolver.ResolveAvailable();

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal([warning], first.Warnings);
        Assert.Equal(
            ["Alpha Library", "Broken Library"],
            first.References.Select(reference => reference.RequestedName));
        Assert.All(first.References, reference => Assert.True(reference.IsRegistered));
        Assert.Single(first.References[0].Matches);
        Assert.Empty(first.References[1].Matches);
        Assert.Equal(
            first.References.Select(reference => reference.RequestedName),
            second.References.Select(reference => reference.RequestedName));
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
