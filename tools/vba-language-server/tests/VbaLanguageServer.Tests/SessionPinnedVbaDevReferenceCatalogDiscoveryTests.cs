using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SessionPinnedVbaDevReferenceCatalogDiscoveryTests
{
    [Fact]
    public async Task Session_moves_one_way_from_registry_to_the_first_pinned_companion()
    {
        var registry = new MarkerDiscovery("registry");
        var firstCompanion = new ContextFactoryDiscovery("first-cli");
        var secondCompanion = new ContextFactoryDiscovery("second-cli");
        var firstPath = Path.GetFullPath("first-vba-dev.exe");
        var secondPath = Path.GetFullPath("second-vba-dev.exe");
        var session = new SessionPinnedVbaDevReferenceCatalogDiscovery(
            registry,
            path => path == firstPath ? firstCompanion : secondCompanion);

        var registryResult = await session.DiscoverAsync("Excel");
        Assert.Equal("registry", registryResult.ErrorMessage);
        Assert.False(
            ((IVbaProjectReferenceCatalogContextDiscoveryFactory)session)
                .UsesContextSpecificResolution);

        Assert.Equal(
            VbaDevReferenceCatalogPinResult.Pinned,
            session.TryPin(firstPath));
        Assert.Equal(
            VbaDevReferenceCatalogPinResult.AlreadyPinned,
            session.TryPin(firstPath));
        Assert.Equal(
            VbaDevReferenceCatalogPinResult.Rejected,
            session.TryPin(secondPath));

        var contextDiscovery =
            ((IVbaProjectReferenceCatalogContextDiscoveryFactory)session)
                .CreateContextDiscovery(new VbaProjectReferenceCatalogRefreshContext(
                    "C:\\work",
                    "Book1",
                    null!));
        var companionResult = await contextDiscovery.DiscoverAsync("Excel");

        Assert.Equal("first-cli", companionResult.ErrorMessage);
        Assert.True(
            ((IVbaProjectReferenceCatalogContextDiscoveryFactory)session)
                .UsesContextSpecificResolution);
    }

    private sealed class MarkerDiscovery(string marker)
        : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    marker));
    }

    private sealed class ContextFactoryDiscovery(string marker)
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    referenceName,
                    "registry-fallback"));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => new MarkerDiscovery(marker);
    }
}
