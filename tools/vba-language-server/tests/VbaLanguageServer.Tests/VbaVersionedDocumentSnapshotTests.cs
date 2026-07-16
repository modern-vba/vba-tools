using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaVersionedDocumentSnapshotTests
{
    [Fact]
    public void Exact_version_snapshot_keeps_text_tree_module_kind_and_diagnostics_together()
    {
        const string uri = "file:///C:/work/Snapshot.bas";
        const string text = "Attribute VB_Name = \"Snapshot\"\nPublic Sub Run()\n    ";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 9, text);

        var snapshot = workspace.GetDocumentSnapshot(uri, expectedVersion: 9);

        Assert.NotNull(snapshot);
        Assert.Equal(uri, snapshot.Uri);
        Assert.Equal(9, snapshot.Version);
        Assert.Equal(text, snapshot.Text);
        Assert.Equal(text, snapshot.SyntaxTree.Text);
        Assert.Equal(VbaModuleKind.StandardModule, snapshot.ModuleKind);
        Assert.Contains(
            snapshot.Diagnostics.SyntaxDiagnostics,
            diagnostic => diagnostic.Code == "syntax.missingBlockTerminator"
                && diagnostic.Message.Contains("End Sub", StringComparison.Ordinal));
        Assert.Null(workspace.GetDocumentSnapshot(uri, expectedVersion: 8));
    }
}
