using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class UnqualifiedClassMemberResolutionTests
{
    [Theory]
    [InlineData("bas", true)]
    [InlineData("cls", false)]
    [InlineData("frm", false)]
    public void OnlyStandardModuleMembersEnterTheUnqualifiedProjectScope(string extension, bool visible)
    {
        const string callerUri = "file:///C:/scope/Caller.bas";
        var ownerUri = $"file:///C:/scope/Owner.{extension}";
        var sources = new Dictionary<string, string>
        {
            [callerUri] = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Work 1, 2\nEnd Sub\n",
            [ownerUri] = Header(extension) + "Public Sub Work(ByVal value As Long)\nEnd Sub\n"
        };
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(sources);
        var resolver = new VbaNameResolutionService(documents.Values.ToArray(), null, VbaProjectReferenceCatalogSet.Empty);

        var target = resolver.Resolve(callerUri, new VbaPosition(2, 4), null, "Work");
        var completions = resolver.GetCompletionDefinitions(callerUri, new VbaPosition(2, 4));
        var diagnostics = VbaSemanticInventoryFixture.Create(sources).GetProjectValidationDiagnostics(callerUri);

        Assert.Equal(visible ? ownerUri : null, target?.Uri);
        Assert.Equal(visible, completions.Any(definition => definition.Name == "Work"));
        Assert.Equal(visible, diagnostics.Any(diagnostic => diagnostic.Code == "validation.incompatibleCallArgumentList"));
    }

    [Fact]
    public void EnclosingClassAndTypedReceiverKeepTheirMemberSignatures()
    {
        const string ownerUri = "file:///C:/scope/Owner.cls";
        const string callerUri = "file:///C:/scope/Caller.bas";
        var sources = new Dictionary<string, string>
        {
            [ownerUri] = Header("cls") + """
                Public Sub Work(ByVal value As Long)
                End Sub
                Public Sub Run()
                    Work 1, 2
                End Sub
                """,
            [callerUri] = """
                Attribute VB_Name = "Caller"
                Public Sub Run()
                    Dim worker As Owner
                    Call worker.Work(1, 2)
                End Sub
                """
        };
        var inventory = VbaSemanticInventoryFixture.Create(sources);

        foreach (var uri in new[] { ownerUri, callerUri })
        {
            var diagnostic = Assert.Single(inventory.GetProjectValidationDiagnostics(uri));
            Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.Code);
            Assert.Equal(ownerUri, Assert.Single(diagnostic.Details!).Location?.Uri);
        }
    }

    [Fact]
    public void AnotherClassFieldDoesNotShadowAReferenceQualifier()
    {
        const string callerUri = "file:///C:/scope/Caller.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [callerUri] = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n",
            ["file:///C:/scope/Owner.cls"] = Header("cls") + "Public HostLibrary As Object\n"
        });
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new("Example Host Library", ["HostLibrary"], []));
        var selection = VbaReferenceSelection.Capture(["Example Host Library"], null);
        var resolver = new VbaNameResolutionService(documents.Values.ToArray(), selection, catalogs);

        Assert.Contains("HostLibrary", resolver.GetCompletionReferenceQualifiers(callerUri, new VbaPosition(1, 0)));
    }

    private static string Header(string extension)
        => (extension switch
        {
            "cls" => "VERSION 1.0 CLASS\n",
            "frm" => "VERSION 5.00\nBegin VB.UserForm Owner\nEnd\n",
            _ => ""
        }) + "Attribute VB_Name = \"Owner\"\n";
}
