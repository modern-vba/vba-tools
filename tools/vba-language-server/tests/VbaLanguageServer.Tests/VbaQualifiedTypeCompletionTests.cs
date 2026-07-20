using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaQualifiedTypeCompletionTests
{
    private const string MainUri = "file:///C:/work/Main.bas";
    private const string LegacyModuleUri = "file:///C:/work/Legacy.bas";
    private const string ReferenceName = "Legacy Library";

    [Theory]
    [InlineData("file:///C:/work/Widget.cls", "Widget")]
    [InlineData("file:///C:/work/Dialog.frm", "Dialog")]
    public void SourceObjectModuleQualifierDoesNotOfferItself(
        string qualifiedModuleUri,
        string qualifiedModuleName)
    {
        var mainSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            $"    Dim candidate As {qualifiedModuleName}.",
            "End Sub"
        ]);
        var qualifiedModuleSource = string.Join('\n', [
            $"Attribute VB_Name = \"{qualifiedModuleName}\"",
            "Option Explicit"
        ]);
        var index = VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
        {
            [MainUri] = mainSource,
            [qualifiedModuleUri] = qualifiedModuleSource
        });

        var completion = index.GetCompletionResult(
            MainUri,
            2,
            $"    Dim candidate As {qualifiedModuleName}.".Length);

        Assert.DoesNotContain(
            completion.Candidates,
            candidate => string.Equals(
                candidate.Label,
                qualifiedModuleName,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SourceModuleQualifierShadowsSameNamedReferenceQualifier()
    {
        var mainSource = string.Join('\n', [
            "Attribute VB_Name = \"Main\"",
            "Public Sub Probe()",
            "    Dim candidate As Legacy.",
            "    Dim concrete As Legacy.SharedType",
            "End Sub"
        ]);
        var sourceModule = string.Join('\n', [
            "Attribute VB_Name = \"Legacy\"",
            "Public Type SharedType",
            "    Value As Long",
            "End Type"
        ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(ReferenceName)]);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new VbaProjectReferenceCatalog(
                ReferenceName,
                ["Legacy"],
                [
                    new VbaProjectReferenceDefinition(
                        ReferenceName,
                        "ReferenceOnlyType",
                        VbaSourceDefinitionKind.Class),
                    new VbaProjectReferenceDefinition(
                        ReferenceName,
                        "SharedType",
                        VbaSourceDefinitionKind.Class)
                ]));
        var index = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string>
            {
                [MainUri] = mainSource,
                [LegacyModuleUri] = sourceModule
            },
            selection,
            catalogs);

        var completion = index.GetCompletionResult(
            MainUri,
            2,
            "    Dim candidate As Legacy.".Length);
        var resolved = Assert.IsType<VbaSourceDefinition>(index.ResolveSourceDefinition(
            MainUri,
            3,
            "    Dim concrete As Legacy.".Length));

        Assert.Equal(["SharedType"], completion.Candidates.Select(candidate => candidate.Label));
        Assert.Equal(VbaDefinitionOrigin.Source, resolved.Identity.Origin);
        Assert.Equal(LegacyModuleUri, resolved.Uri);
    }
}
