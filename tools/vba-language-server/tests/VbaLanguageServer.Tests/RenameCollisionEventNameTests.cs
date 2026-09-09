using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionEventNameTests
{
    [Fact]
    public void Collision_review_keeps_the_original_Event_and_dependent_handler_suffix_closure()
    {
        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string listenerUri = "file:///C:/work/Listener.cls";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [publisherUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Publisher"
                Public Event Original()
                Public Event Existing()
                Public Sub Fire()
                    RaiseEvent Original
                    RaiseEvent Existing
                End Sub
                """,
            [listenerUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Listener"
                Private WithEvents sink As Publisher
                Private Sub sink_Original()
                    Debug.Print 1
                End Sub
                Private Sub sink_Existing()
                    Debug.Print 2
                End Sub
                Public Sub Run()
                    sink_Original
                    sink_Existing
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            publisherUri, 2, "Public Event ".Length, "Revised");
        Assert.True(control.Failure is null, "Independent Revised result: " + control.Failure);
        Assert.NotNull(control.Plan);

        var result = inventory.CreateRenameResult(
            publisherUri, 2, "Public Event ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([listenerUri, publisherUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal([2, 5], plan.Changes[publisherUri].Select(edit => edit.Range.Start.Line));
        Assert.All(plan.Changes[publisherUri], edit => Assert.Equal("Existing", edit.NewText));
        Assert.Equal([3, 10], plan.Changes[listenerUri].Select(edit => edit.Range.Start.Line));
        Assert.Equal(["Existing", "sink_Existing"], plan.Changes[listenerUri].Select(edit => edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.DependentAssociationChanged);
    }
}
