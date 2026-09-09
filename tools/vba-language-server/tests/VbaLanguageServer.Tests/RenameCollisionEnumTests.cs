using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionEnumTests
{
    [Fact]
    public void Collision_review_keeps_the_original_enum_type_declaration_and_explicit_type_reference()
    {
        const string uri = "file:///C:/work/EnumTypes.bas";
        const string source = """
            Attribute VB_Name = "EnumTypes"
            Public Enum Original
                Value = 1
            End Enum
            Public Enum Existing
                Value = 2
            End Enum
            Public Sub Run()
                Dim state As Original
            End Sub
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = VbaSourceDocumentProjector.Project(uri, VbaSyntaxTree.ParseModule(uri, source))
            });
        var safeRename = inventory.CreateRenameResult(uri, 1, "Public Enum ".Length, "Revised");
        Assert.True(safeRename.Failure is null, "Strict Revised precondition: " + safeRename.Failure);
        Assert.NotNull(safeRename.Plan);

        var result = inventory.CreateRenameResult(
            uri, 1, "Public Enum ".Length, "Existing",
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.True(result.Failure is null, "Confirmed Existing result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(uri, change.Key);
        Assert.Equal(new[] { (1, 12), (8, 17) },
            change.Value.Select(edit => (edit.Range.Start.Line, edit.Range.Start.Character)));
        Assert.All(change.Value, edit =>
        {
            Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
            Assert.Equal(edit.Range.Start.Character + "Original".Length, edit.Range.End.Character);
            Assert.Equal("Existing", edit.NewText);
        });
        var updatedLines = source.Split('\n');
        foreach (var edit in change.Value.Reverse())
        {
            var line = updatedLines[edit.Range.Start.Line];
            updatedLines[edit.Range.Start.Line] = line[..edit.Range.Start.Character]
                + edit.NewText + line[edit.Range.End.Character..];
        }
        Assert.Equal(source
                .Replace("Public Enum Original", "Public Enum Existing", StringComparison.Ordinal)
                .Replace("As Original", "As Existing", StringComparison.Ordinal),
            string.Join('\n', updatedLines));
        Assert.Contains(review.Conflicts, conflict => conflict.Uri == uri && conflict.Range?.Start.Line == 4);
        Assert.NotEmpty(review.Impacts);
        Assert.Empty(plan.FileRenames);
    }

    [Fact]
    public void Collision_review_keeps_the_original_enum_member_closure_when_an_unqualified_existing_use_becomes_ambiguous()
    {
        const string uri = "file:///C:/work/EnumMembers.bas";
        const string source = """
            Attribute VB_Name = "EnumMembers"
            Public Enum FirstState
                Ready = 1
            End Enum
            Public Enum SecondState
                Waiting = 2
            End Enum
            Public Sub Run()
                Debug.Print Ready
                Debug.Print SecondState.Waiting
            End Sub
            """;
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase)
            {
                [uri] = VbaSourceDocumentProjector.Project(uri, VbaSyntaxTree.ParseModule(uri, source))
            });
        var safeRename = inventory.CreateRenameResult(uri, 5, 4, "Revised");
        Assert.True(safeRename.Failure is null, "Strict Revised precondition: " + safeRename.Failure);
        Assert.NotNull(safeRename.Plan);

        var result = inventory.CreateRenameResult(
            uri, 5, 4, "Ready", collisionMode: VbaRenameCollisionMode.PrepareConfirmation);

        Assert.True(result.Failure is null, "Confirmed Ready result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(uri, change.Key);
        Assert.Equal(new[] { (5, 4), (9, "    Debug.Print SecondState.".Length) },
            change.Value.Select(edit => (edit.Range.Start.Line, edit.Range.Start.Character)));
        Assert.All(change.Value, edit =>
        {
            Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
            Assert.Equal(edit.Range.Start.Character + "Waiting".Length, edit.Range.End.Character);
            Assert.Equal("Ready", edit.NewText);
        });
        Assert.Contains(review.Conflicts, conflict => conflict.Uri == uri && conflict.Range?.Start.Line == 2);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == uri && impact.Range?.Start.Line == 8);
        Assert.DoesNotContain(change.Value, edit => edit.Range.Start.Line is 1 or 2 or 3 or 8);
        Assert.Empty(plan.FileRenames);
    }
}
