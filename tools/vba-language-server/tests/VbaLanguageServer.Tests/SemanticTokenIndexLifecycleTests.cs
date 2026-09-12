using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SemanticTokenIndexLifecycleTests
{
    private const string CallerUri = "file:///C:/token-index-lifecycle/Caller.bas";
    private const string LibraryUri = "file:///C:/token-index-lifecycle/Library.bas";

    [Fact]
    public void CancelledValidationLeavesTheSameInventoryUsableForSignatureHelpAndValidRetry()
    {
        var caller = VbaSyntaxTree.ParseModule(CallerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nCall RecordValue()\n"
            + string.Concat(Enumerable.Repeat("Call RecordValue(1)\n", 64))
            + "End Sub\n");
        var inventory = CreateInventory(caller,
            "Public Sub RecordValue(ByVal value As Long)\nEnd Sub\n");
        using var cancellation = new CancellationTokenSource();
        using (var interrupted = VbaSemanticWorkObservation.Begin(
            tokenIndexPreparationProgress: count =>
            {
                if (count == 1) cancellation.Cancel();
            }))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                inventory.GetProjectValidationDiagnostics(CallerUri, cancellationToken: cancellation.Token));
            Assert.InRange(interrupted.TokenIndexPreparationTokens, 1, caller.TokenStream.Tokens.Count - 1);
        }

        using (var retry = VbaSemanticWorkObservation.Begin())
        {
            var help = Assert.IsType<VbaSignatureHelp>(inventory.GetSignatureHelp(
                CallerUri, 2, "Call RecordValue(".Length));
            Assert.Equal("Sub RecordValue(value As Long)", help.Signature.Label);
            Assert.Equal(0, help.ActiveParameter);
            Assert.True(retry.TokenIndexPreparationTokens > 0,
                "Cancelled preparation must not publish partial lexical evidence.");
        }
        var diagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(CallerUri));
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(new VbaRange(new VbaPosition(2, 5), new VbaPosition(2, 16)), diagnostic.Range);
        Assert.Empty(inventory.GetProjectValidationDiagnostics(LibraryUri));
    }

    [Fact]
    public async Task ConcurrentFirstSignatureHelpReadsReturnTheSameCompleteSelection()
    {
        var caller = VbaSyntaxTree.ParseModule(CallerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nCall RecordValue(1, 2)\nEnd Sub\n");
        var inventory = CreateInventory(caller,
            "Public Sub RecordValue(ByVal first As Long, ByVal second As Long)\nEnd Sub\n");
        using var ready = new CountdownEvent(4);
        using var start = new ManualResetEventSlim();
        var reads = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            return inventory.GetSignatureHelp(CallerUri, 2, "Call RecordValue(1, ".Length);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var allReady = ready.Wait(TimeSpan.FromSeconds(10));
        start.Set();
        var results = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(allReady, "All independent readers must be admitted before releasing the query gate.");
        foreach (var result in results)
        {
            var help = Assert.IsType<VbaSignatureHelp>(result);
            Assert.Equal("Sub RecordValue(first As Long, second As Long)", help.Signature.Label);
            Assert.Equal(1, help.ActiveParameter);
        }
        Assert.Equal(new VbaDefinitionLocation(LibraryUri,
            new VbaRange(new VbaPosition(1, 11), new VbaPosition(1, 22))),
            inventory.ResolveDefinition(CallerUri, 2, 10));
        Assert.Empty(inventory.GetProjectValidationDiagnostics(CallerUri));
    }

    [Fact]
    public void NewSourceRevisionHasFreshCallEvidenceWhileTheCapturedOldInventoryStaysUsable()
    {
        var originalCaller = VbaSyntaxTree.ParseModule(CallerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nCall RecordValue(1, 2)\nEnd Sub\n");
        var original = CreateInventory(originalCaller,
            "Public Sub RecordValue(ByVal first As Long, ByVal second As Long)\nEnd Sub\n");
        Assert.Equal("Sub RecordValue(first As Long, second As Long)",
            original.GetSignatureHelp(CallerUri, 2, "Call RecordValue(1, ".Length)?.Signature.Label);

        var changedCaller = VbaSyntaxTree.ParseModule(CallerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n' New physical line and logical boundary\n"
            + "If True Then Call RecordValue(2)\nEnd Sub\n");
        var changed = CreateInventory(changedCaller,
            "Public Sub RecordValue(ByVal changedValue As Long)\nEnd Sub\n");
        using (var changedWork = VbaSemanticWorkObservation.Begin())
        {
            var help = Assert.IsType<VbaSignatureHelp>(changed.GetSignatureHelp(
                CallerUri, 3, "If True Then Call RecordValue(".Length));
            Assert.Equal("Sub RecordValue(changedValue As Long)", help.Signature.Label);
            Assert.Equal(0, help.ActiveParameter);
            Assert.True(changedWork.TokenIndexPreparationTokens > 0,
                "An old URI must not supply token evidence for newly captured source.");
        }
        Assert.Empty(changed.GetProjectValidationDiagnostics(CallerUri));

        using var originalWork = VbaSemanticWorkObservation.Begin();
        var originalHelp = Assert.IsType<VbaSignatureHelp>(original.GetSignatureHelp(
            CallerUri, 2, "Call RecordValue(1, ".Length));
        Assert.Equal("Sub RecordValue(first As Long, second As Long)", originalHelp.Signature.Label);
        Assert.Equal(1, originalHelp.ActiveParameter);
        Assert.Equal(0, originalWork.TokenIndexPreparationTokens);
        Assert.Empty(original.GetProjectValidationDiagnostics(CallerUri));
    }

    [Fact]
    public void RetainedCapacityAlreadyReservesColdSignatureHelpTokenEvidence()
    {
        var caller = VbaSyntaxTree.ParseModule(CallerUri,
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n"
            + string.Concat(Enumerable.Repeat("Call RecordValue(1)\n", 64))
            + "End Sub\n");
        var inventory = CreateInventory(caller,
            "Public Sub RecordValue(ByVal value As Long)\nEnd Sub\n");
        var reservedBeforeQueries = inventory.EstimateRetainedAnalysisBytes();
        Assert.InRange(reservedBeforeQueries, 1, long.MaxValue - 1);

        using (var coldRead = VbaSemanticWorkObservation.Begin())
        {
            Assert.Equal("Sub RecordValue(value As Long)",
                inventory.GetSignatureHelp(CallerUri, 2, "Call RecordValue(".Length)?.Signature.Label);
            Assert.True(coldRead.TokenIndexPreparationTokens > 0);
        }
        Assert.Equal(reservedBeforeQueries, inventory.EstimateRetainedAnalysisBytes());

        var retained = inventory.CreateForRetainedAnalysis();
        using var retainedRead = VbaSemanticWorkObservation.Begin();
        var help = Assert.IsType<VbaSignatureHelp>(retained.GetSignatureHelp(
            CallerUri, 2, "Call RecordValue(".Length));
        Assert.Equal("Sub RecordValue(value As Long)", help.Signature.Label);
        Assert.Equal(0, help.ActiveParameter);
        Assert.Equal(0, retainedRead.TokenIndexPreparationTokens);
        Assert.Equal(reservedBeforeQueries, retained.EstimateRetainedAnalysisBytes());
    }

    private static VbaSemanticInventory CreateInventory(VbaSyntaxTree caller, string libraryBody)
        => VbaSemanticInventoryFixture.CreateFromSyntaxTrees(new Dictionary<string, VbaSyntaxTree>
        {
            [CallerUri] = caller,
            [LibraryUri] = VbaSyntaxTree.ParseModule(LibraryUri,
                "Attribute VB_Name = \"Library\"\n" + libraryBody)
        }, referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);
}
