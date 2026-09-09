using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionInterfaceTypeTests
{
    [Fact]
    public void Collision_review_keeps_the_original_interface_identity_and_implementation_closure()
    {
        const string originalUri = "file:///C:/work/Original.cls";
        const string existingUri = "file:///C:/work/Existing.cls";
        const string workerUri = "file:///C:/work/Worker.cls";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [originalUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Original\"\nPublic Sub Run()\nEnd Sub\n",
            [existingUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Existing\"\nPublic Sub Run()\nEnd Sub\n",
            [workerUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements Original
                Private Sub Original_Run()
                    Debug.Print 1
                End Sub
                Public Sub Execute()
                    Original_Run
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            originalUri, 1, "Attribute VB_Name = \"".Length, "Revised",
            retainOriginalPaths: true);
        Assert.True(control.Failure is null, "Independent Revised result: " + control.Failure);
        Assert.NotNull(control.Plan);

        var result = inventory.CreateRenameResult(
            originalUri, 1, "Attribute VB_Name = \"".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation,
            retainOriginalPaths: true);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([originalUri, workerUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, Assert.Single(plan.Changes[originalUri]).Range.Start.Line);
        Assert.Equal([2, 3, 7], plan.Changes[workerUri].Select(edit => edit.Range.Start.Line));
        Assert.Equal(["Existing", "Existing", "Existing_Run"], plan.Changes[workerUri].Select(edit => edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.DependentAssociationChanged);
    }
}
