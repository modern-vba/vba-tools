using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class RenameCollisionReferenceProjectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collision_review_keeps_the_source_module_closure_when_it_captures_an_authoritative_library_qualifier(
        bool predeclaredClass)
    {
        var originalUri = "file:///C:/work/Original." + (predeclaredClass ? "cls" : "bas");
        const string callerUri = "file:///C:/work/Caller.bas";
        const string referenceName = "Selected Object Library";
        const string libraryName = "Library";
        var identityLine = predeclaredClass ? 1 : 0;
        var original = predeclaredClass
            ? "VERSION 1.0 CLASS\nAttribute VB_Name = \"Original\"\nAttribute VB_PredeclaredId = True\nPublic Sub Run()\nEnd Sub"
            : "Attribute VB_Name = \"Original\"\nPublic Sub Run()\nEnd Sub";
        const string caller = """
            Attribute VB_Name = "Caller"
            Public Sub Execute()
                Library.Run
                Original.Run
            End Sub
            """;
        var selection = VbaProjectReferenceSelection.Create(
            "excel", [new VbaProjectReference(referenceName)]);
        var catalog = new VbaProjectReferenceCatalog(referenceName, [libraryName],
            [new VbaProjectReferenceDefinition(referenceName, "Run", VbaSourceDefinitionKind.Procedure,
                Signature: new VbaCallableSignature("Sub Run()", [], CallableKind: VbaCallableKind.Sub),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)])
        {
            ReferencedVbaProjectName = libraryName
        };
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog);
        VbaSemanticInventory Inventory(string moduleText, string callerText)
            => VbaSemanticInventory.Create(VbaSemanticInventoryFixture.ProjectSourceDocuments(
                    new Dictionary<string, string> { [originalUri] = moduleText, [callerUri] = callerText }),
                selection, catalogs,
                authoritativeReferencedProjectNames: new Dictionary<string, string>
                {
                    [referenceName] = catalog.ReferencedVbaProjectName!
                });
        var inventory = Inventory(original, caller);
        Assert.Null(inventory.ResolveSourceDefinition(callerUri, 2, 4));
        var externalMember = Assert.IsType<VbaSourceDefinition>(inventory.ResolveSourceDefinition(
            callerUri, 2, "    Library.".Length));
        Assert.Equal(VbaDefinitionOrigin.ProjectReference, externalMember.Identity.Origin);
        var safeRename = inventory.CreateRenameResult(originalUri, identityLine, 21, "Revised", retainOriginalPaths: true);
        Assert.True(safeRename.Failure is null, "Strict Revised precondition: " + safeRename.Failure);
        Assert.NotNull(safeRename.Plan);

        var expectedAfter = Inventory(original.Replace("\"Original\"", "\"Library\"", StringComparison.Ordinal),
            caller.Replace("Original.Run", "Library.Run", StringComparison.Ordinal));
        Assert.Equal(originalUri, expectedAfter.ResolveSourceDefinition(callerUri, 2, 4)?.Uri);
        var capturedMember = Assert.IsType<VbaSourceDefinition>(expectedAfter.ResolveSourceDefinition(
            callerUri, 2, "    Library.".Length));
        Assert.Equal(VbaDefinitionOrigin.Source, capturedMember.Identity.Origin);
        Assert.Equal(originalUri, capturedMember.Uri);

        var result = inventory.CreateRenameResult(originalUri, identityLine, 21, libraryName,
            collisionMode: VbaRenameCollisionMode.PrepareConfirmation, retainOriginalPaths: true);

        Assert.True(result.Failure is null, "Confirmed Library result: " + result.Failure);
        var plan = Assert.IsType<VbaRenamePlan>(result.Plan);
        var review = Assert.IsType<VbaRenameCollisionReview>(result.CollisionReview);
        Assert.Equal(2, plan.Changes.Count);
        var declarationEdit = Assert.Single(plan.Changes[originalUri]);
        Assert.Equal((identityLine, 21, identityLine, 29), (declarationEdit.Range.Start.Line, declarationEdit.Range.Start.Character,
            declarationEdit.Range.End.Line, declarationEdit.Range.End.Character));
        var qualifierEdit = Assert.Single(plan.Changes[callerUri]);
        Assert.Equal((3, 4, 3, 12), (qualifierEdit.Range.Start.Line, qualifierEdit.Range.Start.Character,
            qualifierEdit.Range.End.Line, qualifierEdit.Range.End.Character));
        Assert.All(plan.Changes.Values.SelectMany(edits => edits), edit => Assert.Equal(libraryName, edit.NewText));
        var conflict = Assert.Single(review.Conflicts);
        Assert.Equal("referencedProject", conflict.CollisionKind);
        Assert.Equal(referenceName, conflict.ReferenceName);
        Assert.Equal(libraryName, conflict.Name);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.NonTargetBindingChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 2);
        Assert.Contains(review.Impacts, impact => impact.Kind == VbaRenameImpactKind.ClassificationChanged
            && impact.Uri == callerUri && impact.Range?.Start.Line == 2);
        Assert.Empty(plan.FileRenames);
    }
}
