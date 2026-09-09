using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionMemberTests
{
    [Fact]
    public void Collision_review_keeps_the_original_class_member_and_With_reference_closure()
    {
        const string containerUri = "file:///C:/work/Container.cls";
        const string callerUri = "file:///C:/work/Caller.bas";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [containerUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Container\"\nPublic Original As Long\nPublic Existing As Long\n",
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim item As Container
                    With item
                        .Original = 1
                        .Existing = 2
                    End With
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            containerUri, 2, "Public ".Length, "Revised");
        Assert.True(control.Failure is null, "Independent Revised result: " + control.Failure);
        Assert.NotNull(control.Plan);
        var original = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 4, "        .".Length));
        Assert.Equal(containerUri, original.Uri);
        Assert.Equal(2, original.Range.Start.Line);
        Assert.Equal("Original", original.Name);
        var existing = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 5, "        .".Length));
        Assert.Equal(containerUri, existing.Uri);
        Assert.Equal(3, existing.Range.Start.Line);
        Assert.Equal("Existing", existing.Name);

        var result = inventory.CreateRenameResult(
            containerUri, 2, "Public ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([callerUri, containerUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, Assert.Single(plan.Changes[containerUri]).Range.Start.Line);
        Assert.Equal(4, Assert.Single(plan.Changes[callerUri]).Range.Start.Line);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.TargetBindingChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 4);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 5);
    }

    [Fact]
    public void Collision_review_keeps_the_original_class_member_and_qualified_reference_closure()
    {
        const string containerUri = "file:///C:/work/Container.cls";
        const string callerUri = "file:///C:/work/Caller.bas";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [containerUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Container\"\nPublic Original As Long\nPublic Existing As Long\n",
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim item As Container
                    item.Original = 1
                    item.Existing = 2
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            containerUri, 2, "Public ".Length, "Revised");
        Assert.True(control.Failure is null, "Independent Revised result: " + control.Failure);
        Assert.NotNull(control.Plan);
        Assert.Equal(containerUri, inventory.ResolveSourceDefinition(
            callerUri, 3, "    item.".Length)?.Uri);

        var result = inventory.CreateRenameResult(
            containerUri, 2, "Public ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([callerUri, containerUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, Assert.Single(plan.Changes[containerUri]).Range.Start.Line);
        Assert.Equal(3, Assert.Single(plan.Changes[callerUri]).Range.Start.Line);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.TargetBindingChanged);
    }
}
