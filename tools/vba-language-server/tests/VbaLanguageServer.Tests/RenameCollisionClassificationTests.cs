using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionClassificationTests
{
    [Fact]
    public void Collision_review_resolves_a_known_ambiguous_reference_through_the_renamed_local_without_editing_it()
    {
        const string firstUri = "file:///C:/work/First.bas";
        const string secondUri = "file:///C:/work/Second.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [firstUri] = "Attribute VB_Name = \"First\"\nPublic Existing As Long\n",
            [secondUri] = "Attribute VB_Name = \"Second\"\nPublic Existing As Long\n",
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim Original As Long
                    Original = 1
                    Debug.Print Existing
                    Debug.Print First.Existing, Second.Existing
                End Sub
                Public Sub Outside()
                    Debug.Print Existing
                End Sub
                """
        });
        var inventory = VbaSemanticInventory.Create(documents);
        var control = inventory.CreateRenameResult(callerUri, 2, "    Dim ".Length, "Revised");
        Assert.Null(control.Failure);
        Assert.NotNull(control.Plan);
        Assert.Equal([2, 3], Assert.Single(control.Plan.Changes).Value.Select(edit => edit.Range.Start.Line));
        var resolver = new VbaNameResolutionService(
            documents.Values.ToArray(), referenceSelection: null, VbaProjectReferenceCatalogSet.Empty);
        Assert.Equal(VbaNameResolutionKind.Ambiguous, resolver.ResolveValueOutcome(
            callerUri, new VbaPosition(4, "    Debug.Print ".Length), qualifier: null, "Existing").Kind);
        Assert.Equal(VbaNameResolutionKind.Ambiguous, resolver.ResolveValueOutcome(
            callerUri, new VbaPosition(8, "    Debug.Print ".Length), qualifier: null, "Existing").Kind);
        Assert.Equal(firstUri, inventory.ResolveSourceDefinition(
            callerUri, 5, "    Debug.Print First.".Length)?.Uri);
        Assert.Equal(secondUri, inventory.ResolveSourceDefinition(
            callerUri, 5, "    Debug.Print First.Existing, Second.".Length)?.Uri);
        Assert.Equal([2, 3], inventory.FindReferences(callerUri, 2, "    Dim ".Length)
            .Select(occurrence => occurrence.Range.Start.Line));

        var result = inventory.CreateRenameResult(
            callerUri, 2, "    Dim ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(callerUri, change.Key);
        Assert.Equal([2, 3], change.Value.Select(edit => edit.Range.Start.Line));
        Assert.All(change.Value, edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Equal([firstUri, secondUri], review.Conflicts.Select(conflict => conflict.Uri)
            .Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(review.Conflicts, conflict =>
        {
            Assert.Equal("Existing", conflict.Name);
            Assert.Equal(1, conflict.Range?.Start.Line);
        });
        var impact = Assert.Single(review.Impacts, candidate =>
            candidate.Kind == VbaRenameImpactKind.ClassificationChanged
            && candidate.Uri == callerUri && candidate.Range?.Start.Line == 4);
        var evidence = Assert.IsType<VbaRenameImpactEvidence>(impact.Evidence);
        Assert.Equal(VbaNameResolutionKind.Ambiguous, evidence.BeforeClassification);
        Assert.Equal(VbaNameResolutionKind.Ambiguous, evidence.ControlClassification);
        Assert.Equal(VbaNameResolutionKind.Resolved, evidence.AfterClassification);
    }

    [Fact]
    public void Collision_review_allows_a_previously_inaccessible_reference_to_resolve_without_editing_it()
    {
        const string libraryUri = "file:///C:/work/Library.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [libraryUri] = """
                Attribute VB_Name = "Library"
                Public Function Original() As Long
                    Original = 1
                End Function
                Private Function Existing() As Long
                    Existing = 2
                End Function
                """,
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Debug.Print Library.Original
                    Debug.Print Library.Existing
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            libraryUri, 1, "Public Function ".Length, "Revised");
        Assert.Null(control.Failure);
        Assert.NotNull(control.Plan);
        Assert.Equal(libraryUri, inventory.ResolveSourceDefinition(
            callerUri, 2, "    Debug.Print Library.".Length)?.Uri);
        Assert.Null(inventory.ResolveSourceTarget(
            callerUri, 3, "    Debug.Print Library.".Length));
        Assert.Equal([4, 5], inventory.FindReferences(
            libraryUri, 4, "Private Function ".Length).Select(occurrence => occurrence.Range.Start.Line));

        var result = inventory.CreateRenameResult(
            libraryUri, 1, "Public Function ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([callerUri, libraryUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal([1, 2], plan.Changes[libraryUri].Select(edit => edit.Range.Start.Line));
        Assert.Equal(2, Assert.Single(plan.Changes[callerUri]).Range.Start.Line);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Conflicts, conflict => conflict.Name == "Existing"
            && conflict.Uri == libraryUri && conflict.Range?.Start.Line == 4);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.ClassificationChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 3);
    }
}
