using System.Text.Json;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLanguageWorkspaceTests
{
    [Fact]
    public void ProjectSnapshotScopesDocumentsAndReferenceSelectionForFeatureHandlers()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-workspace-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var book1HelperUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helper.bas"));
            var book1CallerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var secondBookHelperUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Helper.bas"));
            var callerText = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(book1HelperUri, string.Join('\n', [
                "Attribute VB_Name = \"Book1Helper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(secondBookHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"SecondBookHelper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(book1CallerUri, callerText);

            var snapshot = workspace.CreateProjectSnapshot(book1CallerUri);
            var definition = snapshot.SourceIndex.ResolveDefinition(
                book1CallerUri,
                line: 2,
                character: "    ".Length);

            Assert.NotNull(definition);
            Assert.Equal(book1HelperUri, definition.Uri);
            Assert.Equal("Book1", snapshot.Resolution.DocumentName);
            Assert.Equal("excel", snapshot.Resolution.DocumentKind);
            Assert.NotNull(snapshot.ReferenceSelection);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                snapshot.ReferenceSelection.MainVbaProjectReference?.Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void WriteProjectManifest(string projectRoot)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "WorkspaceSnapshotProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    references = new[]
                    {
                        new { name = "Visual Basic For Applications" },
                        new { name = "Microsoft Excel 16.0 Object Library" }
                    }
                },
                ["SecondBook"] = new
                {
                    kind = "excel",
                    sourcePath = "src/SecondBook",
                    templatePath = "src/SecondBook/SecondBook.xlsm",
                    binPath = "bin/SecondBook/SecondBook.xlsm",
                    publishPath = "publish/SecondBook/SecondBook.xlsm",
                    references = new[]
                    {
                        new { name = "Visual Basic For Applications" }
                    }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "project.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ToFileUri(string path)
        => new Uri(path).AbsoluteUri;
}
