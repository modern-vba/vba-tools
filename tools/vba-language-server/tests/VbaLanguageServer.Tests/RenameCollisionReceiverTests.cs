using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionReceiverTests
{
    [Fact]
    public void Collision_review_retains_the_original_chained_receiver_member_without_editing_the_call()
    {
        const string originalUri = "file:///C:/work/Original.cls";
        const string existingUri = "file:///C:/work/Existing.cls";
        const string holderUri = "file:///C:/work/Holder.cls";
        const string callerUri = "file:///C:/work/Caller.bas";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [originalUri] = "Attribute VB_Name = \"Original\"\nPublic Value As Long\n",
            [existingUri] = "Attribute VB_Name = \"Existing\"\nPublic Value As Long\n",
            [holderUri] = "Attribute VB_Name = \"Holder\"\nPublic Child As Original\n",
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim holder As Holder
                    Debug.Print holder.Child.Value
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            originalUri, 0, "Attribute VB_Name = \"".Length, "Revised",
            retainOriginalPaths: true);
        Assert.Null(control.Failure);
        Assert.NotNull(control.Plan);
        var child = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 3, "    Debug.Print holder.".Length));
        Assert.Equal(holderUri, child.Uri);
        Assert.Equal(1, child.Range.Start.Line);
        Assert.Equal("Child", child.Name);
        var member = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 3, "    Debug.Print holder.Child.".Length));
        Assert.Equal(originalUri, member.Uri);
        Assert.Equal(1, member.Range.Start.Line);
        Assert.Equal("Value", member.Name);

        var result = inventory.CreateRenameResult(
            originalUri, 0, "Attribute VB_Name = \"".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation,
            retainOriginalPaths: true);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([holderUri, originalUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, Assert.Single(plan.Changes[originalUri]).Range.Start.Line);
        Assert.Equal(1, Assert.Single(plan.Changes[holderUri]).Range.Start.Line);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.TypeNameResolutionChanged);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 3
            && impact.Range.Start.Character == "    Debug.Print holder.Child.".Length);
    }

    [Fact]
    public void Collision_review_retains_the_original_With_receiver_member_without_editing_it()
    {
        const string originalUri = "file:///C:/work/Original.cls";
        const string existingUri = "file:///C:/work/Existing.cls";
        const string callerUri = "file:///C:/work/Caller.bas";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [originalUri] = "Attribute VB_Name = \"Original\"\nPublic Value As Long\n",
            [existingUri] = "Attribute VB_Name = \"Existing\"\nPublic Value As Long\n",
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim value As Original
                    With value
                        .Value = 1
                    End With
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            originalUri, 0, "Attribute VB_Name = \"".Length, "Revised",
            retainOriginalPaths: true);
        Assert.Null(control.Failure);
        Assert.NotNull(control.Plan);
        var member = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 4, "        .".Length));
        Assert.Equal(originalUri, member.Uri);
        Assert.Equal(1, member.Range.Start.Line);
        Assert.Equal("Value", member.Name);

        var result = inventory.CreateRenameResult(
            originalUri, 0, "Attribute VB_Name = \"".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation,
            retainOriginalPaths: true);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([callerUri, originalUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, Assert.Single(plan.Changes[originalUri]).Range.Start.Line);
        Assert.Equal(2, Assert.Single(plan.Changes[callerUri]).Range.Start.Line);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.TypeNameResolutionChanged);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 4);
    }
}
