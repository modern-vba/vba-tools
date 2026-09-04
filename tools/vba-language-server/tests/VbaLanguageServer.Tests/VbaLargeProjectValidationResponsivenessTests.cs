using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

[Collection(VbaDocumentAnalysisPerformanceTestCollection.Name)]
public sealed class VbaLargeProjectValidationResponsivenessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Large_manifest_project_semantic_tokens_complete_while_project_validation_is_blocked()
    {
        using var fixture = ManifestBackedLargeProjectFixture.Create();
        var validationObserver = new BlockingProjectValidationObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            validationObserver);
        workspace.OpenDocument(
            fixture.ActiveUri,
            version: 1,
            fixture.ActiveText);

        using var validationCancellation = new CancellationTokenSource();
        using var semanticReadCancellation = new CancellationTokenSource();
        var validation = Task.Run(() =>
            workspace.GetProjectDiagnosticsSnapshots(
                fixture.ActiveUri,
                validationCancellation.Token));
        Task<(
            VbaProjectSnapshot ProjectSnapshot,
            IReadOnlyList<int> SemanticTokenData)>? semanticRead = null;

        var validationWasCancelled = false;
        try
        {
            await validationObserver.ValidationStarted.Task
                .WaitAsync(TestTimeout);
            semanticRead = Task.Run(() =>
            {
                var projectSnapshot = workspace.CreateProjectSnapshot(
                    fixture.ActiveUri,
                    semanticReadCancellation.Token);
                var semanticTokenData = projectSnapshot.SemanticInventory
                    .GetSemanticTokenData(
                        fixture.ActiveUri,
                        semanticReadCancellation.Token);
                return (projectSnapshot, semanticTokenData);
            }, semanticReadCancellation.Token);
            var semanticResult = await semanticRead
                .WaitAsync(TestTimeout);
            var argumentListCount = semanticResult.ProjectSnapshot
                .SourceDocuments
                .Sum(pair =>
                    VbaSyntaxTree.ParseModule(pair.Key, pair.Value)
                        .Module
                        .ArgumentLists
                        .Count);

            Assert.True(
                semanticResult.ProjectSnapshot.SourceDocuments.Count >= 90,
                $"Expected at least 90 source documents, found {semanticResult.ProjectSnapshot.SourceDocuments.Count}.");
            Assert.True(
                argumentListCount >= 40_000,
                $"Expected at least 40,000 argument lists, found {argumentListCount}.");
            Assert.NotEmpty(semanticResult.SemanticTokenData);
            Assert.False(validation.IsCompleted);
        }
        finally
        {
            semanticReadCancellation.Cancel();
            validationCancellation.Cancel();
            validationObserver.ReleaseValidation();
            try
            {
                await validation.WaitAsync(TestTimeout);
            }
            catch (OperationCanceledException)
            {
                validationWasCancelled = true;
            }

            if (semanticRead is not null)
            {
                try
                {
                    await semanticRead.WaitAsync(TestTimeout);
                }
                catch (OperationCanceledException)
                    when (semanticReadCancellation.IsCancellationRequested)
                {
                }
            }
        }

        Assert.True(validationWasCancelled);
    }

    private sealed class BlockingProjectValidationObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();

        public TaskCompletionSource ValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            ValidationStarted.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
        }

        public void ReleaseValidation()
            => release.Set();
    }
}
