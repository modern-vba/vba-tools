using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaRetainedSemanticInventoryTests
{
    [Fact]
    public void Retention_budget_accounts_for_analysis_shape_and_reserves_lazy_editor_caches()
    {
        const string uri = "file:///C:/work/Module.bas";
        var denseText = "Public Sub Run()\n"
            + string.Concat(Enumerable.Repeat("    Dim value As Long\n", 80))
            + "End Sub\n";
        var commentText = "'" + new string('x', denseText.Length - 2) + "\n";
        var dense = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [uri] = denseText });
        var comment = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [uri] = commentText });

        var estimatedBytes = dense.EstimateRetainedAnalysisBytes();

        Assert.True(estimatedBytes > comment.EstimateRetainedAnalysisBytes());
        Assert.True(estimatedBytes > denseText.Length * sizeof(char));
        _ = dense.GetSemanticTokenData(uri);
        _ = dense.FindReferences(uri, 1, 9);
        Assert.Equal(estimatedBytes, dense.EstimateRetainedAnalysisBytes());
        Assert.Equal(estimatedBytes, dense.CreateForRetainedAnalysis().EstimateRetainedAnalysisBytes());
    }

    [Fact]
    public void Fresh_snapshot_reuses_editor_analysis_but_builds_validation_for_its_own_lifecycle()
    {
        const string callerUri = "file:///C:/work/Caller.bas";
        const string helperUri = "file:///C:/work/Helper.bas";
        var sources = VbaSemanticInventoryFixture.ProjectSourceDocuments(
            new Dictionary<string, string>
            {
                [callerUri] = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    ReadValue\nEnd Sub\n",
                [helperUri] = "Attribute VB_Name = \"Helper\"\nPublic Sub ReadValue()\nEnd Sub\n"
            });
        var originalObserver = new ValidationObserver();
        var original = VbaSemanticInventory.CreateForProjectSnapshot(
            sources,
            referenceSelection: null,
            VbaProjectReferenceCatalogSet.Empty,
            intrinsicHostEventCatalog: null,
            referenceCatalogSources: null,
            referenceCatalogIdentities: null,
            projectResolution: null,
            authoritativeReferencedProjectNames: null,
            callerUri,
            originalObserver,
            CancellationToken.None);
        var tokens = original.GetSemanticTokenData(callerUri);
        var expectedDiagnostics = original.GetProjectValidationDiagnostics(callerUri);
        var freshObserver = new ValidationObserver();

        var reopened = original.CreateForRetainedAnalysis()
            .CreateFromRetainedAnalysis(helperUri, freshObserver);

        Assert.Same(tokens, reopened.GetSemanticTokenData(callerUri));
        var oracle = VbaSemanticInventory.Create(sources);
        Assert.Equal(oracle.GetSemanticTokenData(callerUri), reopened.GetSemanticTokenData(callerUri));
        Assert.Equal(
            oracle.ResolveDefinition(callerUri, 2, 5),
            reopened.ResolveDefinition(callerUri, 2, 5));
        Assert.Equal(
            oracle.FindReferences(callerUri, 2, 5),
            reopened.FindReferences(callerUri, 2, 5));
        Assert.Equal(expectedDiagnostics, reopened.GetProjectValidationDiagnostics(callerUri));
        Assert.Equal([callerUri], originalObserver.ValidationUris);
        Assert.Equal([helperUri], freshObserver.ValidationUris);
    }

    private sealed class ValidationObserver : IVbaProjectSnapshotBuildObserver
    {
        public List<string> ValidationUris { get; } = [];

        public void BeforeBuildProjectValidation(string activeUri, CancellationToken cancellationToken)
            => ValidationUris.Add(activeUri);

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
        }
    }
}
