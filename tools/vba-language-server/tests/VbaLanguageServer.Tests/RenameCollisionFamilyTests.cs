using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionFamilyTests
{
    [Fact]
    public void Collision_review_keeps_the_established_Implements_member_closure()
    {
        const string contractUri = "file:///C:/work/Contract.cls";
        const string workerUri = "file:///C:/work/Worker.cls";
        var sources = new Dictionary<string, string>
        {
            [contractUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Contract"
                Public Sub Original()
                End Sub
                Public Sub Existing()
                End Sub
                """,
            [workerUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements Contract
                Private Sub Contract_Original()
                    Debug.Print 1
                End Sub
                Private Sub Contract_Existing()
                    Debug.Print 2
                End Sub
                Public Sub Run()
                    Contract_Original
                    Contract_Existing
                End Sub
                """
        };
        var inventory = VbaSemanticInventory.Create(sources.ToDictionary(
            pair => pair.Key,
            pair => VbaSourceDocumentProjector.Project(pair.Key, VbaSyntaxTree.ParseModule(pair.Key, pair.Value)),
            StringComparer.OrdinalIgnoreCase));
        var strict = inventory.CreateRenameResult(contractUri, 2, "Public Sub ".Length, "Revised");
        Assert.Null(strict.Failure);
        Assert.NotNull(strict.Plan);

        var result = inventory.CreateRenameResult(
            contractUri, 2, "Public Sub ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal(2, plan.Changes.Count);
        var declaration = Assert.Single(plan.Changes[contractUri]);
        Assert.Equal(2, declaration.Range.Start.Line);
        Assert.Equal("Existing", declaration.NewText);
        Assert.Equal([3, 10], plan.Changes[workerUri].Select(edit => edit.Range.Start.Line));
        Assert.Equal(["Existing", "Contract_Existing"], plan.Changes[workerUri].Select(edit => edit.NewText));
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.DependentAssociationChanged);
    }

    [Fact]
    public void Collision_review_keeps_the_established_WithEvents_handler_closure()
    {
        const string publisherUri = "file:///C:/work/Publisher.cls";
        const string listenerUri = "file:///C:/work/Listener.cls";
        var sources = new Dictionary<string, string>
        {
            [publisherUri] = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Publisher\"\nPublic Event Changed()\n",
            [listenerUri] = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Listener"
                Private WithEvents original As Publisher
                Private WithEvents existing As Publisher
                Private Sub original_Changed()
                    Debug.Print 1
                End Sub
                Private Sub existing_Changed()
                    Debug.Print 2
                End Sub
                Public Sub Run()
                    Set original = New Publisher
                    original_Changed
                    Set existing = New Publisher
                    existing_Changed
                End Sub
                """
        };
        var inventory = VbaSemanticInventory.Create(sources.ToDictionary(
            pair => pair.Key,
            pair => VbaSourceDocumentProjector.Project(pair.Key, VbaSyntaxTree.ParseModule(pair.Key, pair.Value)),
            StringComparer.OrdinalIgnoreCase));
        Assert.Null(inventory.CreateRenameResult(
            listenerUri, 2, "Private WithEvents ".Length, "revised").Failure);

        var result = inventory.CreateRenameResult(
            listenerUri, 2, "Private WithEvents ".Length, "existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        var edits = Assert.Single(plan.Changes).Value;
        Assert.Equal([2, 4, 11, 12], edits.Select(edit => edit.Range.Start.Line));
        Assert.Equal(["existing", "existing", "existing", "existing_Changed"], edits.Select(edit => edit.NewText));
        Assert.DoesNotContain(publisherUri, plan.Changes.Keys);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.DependentAssociationChanged);
    }

    [Fact]
    public void Collision_review_keeps_every_original_conditional_variant_and_its_calls()
    {
        const string uri = "file:///C:/work/ConditionalConsolidation.bas";
        const string source = """
            Attribute VB_Name = "ConditionalConsolidation"
            #If FIRST_CONFIGURATION Then
            Public Function Original() As Long
                Original = 1
            End Function
            Public Function Existing() As Long
                Existing = 2
            End Function
            #Else
            Public Function Original() As Long
                Original = 3
            End Function
            Public Function Existing() As Long
                Existing = 4
            End Function
            #End If
            Public Sub Run()
                Debug.Print Original(), Existing()
            End Sub
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = VbaSourceDocumentProjector.Project(uri, VbaSyntaxTree.ParseModule(uri, source))
            });
        var original = Assert.IsType<VbaConditionalFamilyNameTarget>(
            inventory.ResolveSourceTarget(uri, 17, "    Debug.Print ".Length));
        Assert.Equal([2, 9], original.PhysicalDefinitions.Select(definition => definition.Range.Start.Line));
        Assert.Null(inventory.CreateRenameResult(uri, 2, "Public Function ".Length, "Revised").Failure);

        var result = inventory.CreateRenameResult(
            uri, 2, "Public Function ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        var edits = Assert.Single(plan.Changes).Value;
        Assert.Equal([2, 3, 9, 10, 17], edits.Select(edit => edit.Range.Start.Line));
        Assert.All(edits, edit => Assert.Equal("Existing", edit.NewText));
        Assert.DoesNotContain(edits, edit => edit.Range.Start.Line is 5 or 6 or 12 or 13);
        Assert.Equal([2, 9], plan.TargetCorrespondence!.PhysicalDefinitions
            .Select(pair => pair.BeforeDefinition.Range.Start.Line));
    }

    [Fact]
    public void Collision_review_renames_the_original_property_family_without_acquiring_the_existing_family()
    {
        const string uri = "file:///C:/work/PropertyConsolidation.cls";
        const string source = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "PropertyConsolidation"
            Private originalValue As Long
            Private existingValue As Long
            Public Property Get Original() As Long
                Original = originalValue
            End Property
            Public Property Let Original(ByVal value As Long)
                originalValue = value
            End Property
            Public Property Get Existing() As Long
                Existing = existingValue
            End Property
            Public Property Let Existing(ByVal value As Long)
                existingValue = value
            End Property
            Public Sub Run()
                Original = 1
                Debug.Print Original
                Existing = 2
                Debug.Print Existing
            End Sub
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = VbaSourceDocumentProjector.Project(
                    uri, VbaSyntaxTree.ParseModule(uri, source))
            });
        var original = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(uri, 4, "Public Property Get ".Length));
        var existing = Assert.IsType<VbaPropertyNameTarget>(
            inventory.ResolveSourceTarget(uri, 10, "Public Property Get ".Length));
        Assert.NotEqual(original.Identity, existing.Identity);
        Assert.Equal([4, 7], original.PhysicalDefinitions.Select(definition => definition.Range.Start.Line));
        Assert.Equal([10, 13], existing.PhysicalDefinitions.Select(definition => definition.Range.Start.Line));

        var result = inventory.CreateRenameResult(
            uri, 4, "Public Property Get ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.Null(result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal("Original", review.OriginalName);
        Assert.Equal("Existing", review.RequestedName);
        Assert.Contains(review.Conflicts, conflict => conflict.Name == "Existing"
            && conflict.Uri == uri && conflict.Range?.Start.Line is 10 or 13);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(uri, change.Key);
        Assert.Equal(
            new[] { (4, 20), (5, 4), (7, 20), (17, 4), (18, 16) },
            change.Value.Select(edit => (edit.Range.Start.Line, edit.Range.Start.Character)));
        Assert.All(change.Value, edit =>
        {
            Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
            Assert.Equal(edit.Range.Start.Character + "Original".Length, edit.Range.End.Character);
            Assert.Equal("Existing", edit.NewText);
        });
        Assert.Empty(plan.FileRenames);
    }
}
