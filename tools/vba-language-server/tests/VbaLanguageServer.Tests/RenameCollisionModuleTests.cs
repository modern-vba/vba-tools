using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionModuleTests
{
    [Fact]
    public void Collision_review_keeps_the_original_module_and_qualifier_closure_with_duplicate_member_names()
    {
        const string originalUri = "file:///C:/work/Original.bas";
        const string existingUri = "file:///C:/work/Existing.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        const string original = "Attribute VB_Name = \"Original\"\nPublic Sub Run()\nEnd Sub";
        const string existing = "Attribute VB_Name = \"Existing\"\nPublic Sub Run()\nEnd Sub";
        const string caller = """
            Attribute VB_Name = "Caller"
            Public Sub Execute()
                Original.Run
                Existing.Run
            End Sub
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [originalUri] = VbaSourceDocumentProjector.Project(
                    originalUri, VbaSyntaxTree.ParseModule(originalUri, original)),
                [existingUri] = VbaSourceDocumentProjector.Project(
                    existingUri, VbaSyntaxTree.ParseModule(existingUri, existing)),
                [callerUri] = VbaSourceDocumentProjector.Project(
                    callerUri, VbaSyntaxTree.ParseModule(callerUri, caller))
            });
        var safeRename = inventory.CreateRenameResult(originalUri, 0, 21, "Revised");
        Assert.True(safeRename.Failure is null, "Strict Revised precondition: " + safeRename.Failure);
        Assert.NotNull(safeRename.Plan);

        var result = inventory.CreateRenameResult(
            originalUri, 0, 21, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation,
            retainOriginalPaths: true);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal(2, plan.Changes.Count);
        var declarationEdit = Assert.Single(plan.Changes[originalUri]);
        Assert.Equal((0, 21, 0, 29), (declarationEdit.Range.Start.Line, declarationEdit.Range.Start.Character,
            declarationEdit.Range.End.Line, declarationEdit.Range.End.Character));
        Assert.Equal("Existing", declarationEdit.NewText);
        var qualifierEdit = Assert.Single(plan.Changes[callerUri]);
        Assert.Equal((2, 4, 2, 12), (qualifierEdit.Range.Start.Line, qualifierEdit.Range.Start.Character,
            qualifierEdit.Range.End.Line, qualifierEdit.Range.End.Character));
        Assert.Equal("Existing", qualifierEdit.NewText);
        Assert.False(plan.Changes.ContainsKey(existingUri));
        Assert.Contains(review.Conflicts, conflict => conflict.Uri == existingUri
            && conflict.Range?.Start.Line == 0);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == callerUri);
        Assert.Empty(plan.FileRenames);
    }
}
