using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogRefreshTests
{
    [Fact]
    public async Task TypeLibDiscoveryResolvesReferenceCatalogIdentity()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(new FakeTypeLibRegistryReader(
            new TypeLibRegistryEntry(
                "Custom Library",
                "{11111111-1111-1111-1111-111111111111}",
                1,
                2,
                0,
                @"C:\TypeLibs\Custom.tlb")));

        var result = await discovery.DiscoverAsync("custom library");

        Assert.False(result.IsFailure);
        Assert.False(result.IsAmbiguous);
        var identity = Assert.Single(result.Identities);
        Assert.Equal("Custom Library", identity.ReferenceName);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", identity.Guid);
        Assert.Equal(1, identity.MajorVersion);
        Assert.Equal(2, identity.MinorVersion);
        Assert.Equal(0, identity.Lcid);
        Assert.Equal(@"C:\TypeLibs\Custom.tlb", identity.Path);
    }

    [Fact]
    public async Task TypeLibDiscoveryReportsAmbiguousMatchesInsteadOfGuessing()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(new FakeTypeLibRegistryReader(
            new TypeLibRegistryEntry(
                "Ambiguous Library",
                "{11111111-1111-1111-1111-111111111111}",
                1,
                0,
                0,
                @"C:\TypeLibs\AmbiguousA.tlb"),
            new TypeLibRegistryEntry(
                "Ambiguous Library",
                "{22222222-2222-2222-2222-222222222222}",
                1,
                0,
                0,
                @"C:\TypeLibs\AmbiguousB.tlb")));

        var result = await discovery.DiscoverAsync("Ambiguous Library");

        Assert.True(result.IsAmbiguous);
        Assert.False(result.HasUsableCatalog);
        Assert.Equal(2, result.Identities.Count);
    }

    [Fact]
    public async Task CatalogRefreshUpdatesCacheAfterDiscoveryWithoutBlockingEditorRequests()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb"),
                new VbaProjectReferenceCatalog(
                    "Generated Library",
                    ["Generated"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Generated Library",
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class,
                            "Generated from refreshed catalog metadata.")
                    ])));
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "End Sub"
            ])
        };

        var refreshTask = service.RefreshAsync(selection);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var beforeRefresh = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 1, 0)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.DoesNotContain("GeneratedType", beforeRefresh);

        discovery.Release();
        await refreshTask;

        var afterRefresh = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 1, 0)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("GeneratedType", afterRefresh);
        Assert.True(cache.Identities.ContainsKey("Generated Library"));
    }

    [Fact]
    public async Task CatalogRefreshReportsFailuresWithoutBreakingSourceFeatures()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new FailingCatalogDiscovery("TypeLib registry is unavailable."));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Unavailable Library")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Function BuildValue() As String",
                "End Function",
                "",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ])
        };

        var results = await service.RefreshAsync(selection);

        var result = Assert.Single(results);
        Assert.True(result.DiscoveryResult.IsFailure);
        Assert.Contains("TypeLib registry is unavailable.", result.DiscoveryResult.ErrorMessage, StringComparison.Ordinal);
        var definitions = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 5, 4)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("BuildValue", definitions);
    }

    [Fact]
    public async Task CatalogRefreshHonorsCancellationWithoutCachingCatalogMetadata()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new CancellationAwareCatalogDiscovery());
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Cancelable Library")]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RefreshAsync(selection, cancellation.Token));

        Assert.Empty(cache.Identities);
        Assert.False(cache.Current.HasCatalog("Cancelable Library"));
    }

    private sealed class FakeTypeLibRegistryReader : ITypeLibRegistryReader
    {
        private readonly IReadOnlyList<TypeLibRegistryEntry> entries;

        public FakeTypeLibRegistryReader(params TypeLibRegistryEntry[] entries)
        {
            this.entries = entries;
        }

        public IReadOnlyList<TypeLibRegistryEntry> ReadTypeLibraries() => entries;
    }

    private sealed class BlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult result;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        {
            this.result = result;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class FailingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string message;

        public FailingCatalogDiscovery(string message)
        {
            this.message = message;
        }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, message));
    }

    private sealed class CancellationAwareCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Cancellation was not observed."));
        }
    }
}
