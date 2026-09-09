using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionEventTypeTests
{
    [Fact]
    public void Collision_review_retains_established_handlers_when_the_event_source_type_name_becomes_ambiguous()
    {
        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string existingUri = "file:///C:/work/Existing.cls";
        const string listenerUri = "file:///C:/work/Listener.cls";
        var inventory = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [publisherUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Publisher\"\nPublic Event Changed()\n",
            [existingUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Existing\"\nPublic Event Changed()\n",
            [listenerUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Listener"
                Private WithEvents sink As Publisher
                Private Sub sink_Changed()
                    Debug.Print 1
                End Sub
                """
        });
        var control = inventory.CreateRenameResult(
            publisherUri, 1, "Attribute VB_Name = \"".Length, "Revised",
            retainOriginalPaths: true);
        Assert.True(control.Failure is null, "Independent Revised result: " + control.Failure);
        Assert.NotNull(control.Plan);
        Assert.Equal(publisherUri, inventory.ResolveSourceDefinition(
            listenerUri, 3, "Private Sub sink_".Length)?.Uri);

        var result = inventory.CreateRenameResult(
            publisherUri, 1, "Attribute VB_Name = \"".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation,
            retainOriginalPaths: true);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal([listenerUri, publisherUri], plan.Changes.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, Assert.Single(plan.Changes[publisherUri]).Range.Start.Line);
        var typeEdit = Assert.Single(plan.Changes[listenerUri]);
        Assert.Equal(2, typeEdit.Range.Start.Line);
        Assert.Equal("Private WithEvents sink As ".Length, typeEdit.Range.Start.Character);
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal("Existing", edit.NewText));
        Assert.Empty(plan.FileRenames);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.DependentAssociationChanged);
    }
}
