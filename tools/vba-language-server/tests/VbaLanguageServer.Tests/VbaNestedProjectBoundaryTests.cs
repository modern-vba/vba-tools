using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaNestedProjectBoundaryTests
{
    [Fact]
    public void Outer_project_semantic_inventory_excludes_sources_owned_by_a_nested_manifest()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nested-project-boundary-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "OuterBook", "src");
            WriteManifest(innerProjectRoot, "InnerBook", "src/Inner");

            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            const string innerText =
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n";
            File.WriteAllText(
                outerPath,
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n");
            File.WriteAllText(innerPath, innerText);

            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(
                new Uri(innerPath).AbsoluteUri,
                version: 1,
                innerText);
            var inner = workspace.CreateProjectSnapshot(
                new Uri(innerPath).AbsoluteUri);
            var outer = workspace.CreateProjectSnapshot(
                new Uri(outerPath).AbsoluteUri);

            Assert.Empty(outer.SemanticInventory.GetWorkspaceSymbols("RunInner"));
            Assert.Contains(
                inner.SemanticInventory.GetWorkspaceSymbols("RunInner"),
                symbol => symbol.Name == "RunInner");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Unsaved_descendant_manifest_overlay_rebuilds_only_the_containing_outer_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-open-nested-project-boundary-").FullName;
        var unrelatedRoot = Directory.CreateTempSubdirectory(
            "vba-ls-unrelated-project-boundary-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "OuterBook", "src");
            WriteManifest(unrelatedRoot, "OtherBook", "src");

            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            var unrelatedPath = Path.Combine(
                unrelatedRoot,
                "src",
                "Other.bas");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelatedPath)!);
            File.WriteAllText(
                outerPath,
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n");
            File.WriteAllText(
                innerPath,
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n");
            File.WriteAllText(
                unrelatedPath,
                "Attribute VB_Name = \"Other\"\n"
                + "Public Sub RunOther()\n"
                + "End Sub\n");

            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var outerUri = new Uri(outerPath).AbsoluteUri;
            var innerUri = new Uri(innerPath).AbsoluteUri;
            var unrelatedUri = new Uri(unrelatedPath).AbsoluteUri;
            var nestedManifestUri = new Uri(Path.Combine(
                innerProjectRoot,
                "vba-project.json")).AbsoluteUri;

            var before = workspace.CreateProjectSnapshot(outerUri);
            var unrelatedBefore =
                workspace.CreateProjectSnapshot(unrelatedUri);
            Assert.Contains(
                before.SemanticInventory.GetWorkspaceSymbols("RunInner"),
                symbol => symbol.Uri == innerUri);

            var opened = workspace.ManifestWorkspace.OpenManifest(
                nestedManifestUri,
                documentVersion: 1,
                CreateManifestText("InnerBook", "src/Inner"));
            Assert.True(opened.Accepted);
            Assert.True(opened.EffectiveChanged);
            var whileOpen = workspace.CreateProjectSnapshot(outerUri);

            Assert.NotSame(before, whileOpen);
            Assert.Empty(
                whileOpen.SemanticInventory.GetWorkspaceSymbols("RunInner"));
            Assert.Same(
                unrelatedBefore,
                workspace.CreateProjectSnapshot(unrelatedUri));

            Assert.True(
                workspace.ManifestWorkspace.CloseManifest(
                    nestedManifestUri));
            var afterClose = workspace.CreateProjectSnapshot(outerUri);

            Assert.NotSame(whileOpen, afterClose);
            Assert.Contains(
                afterClose.SemanticInventory.GetWorkspaceSymbols("RunInner"),
                symbol => symbol.Uri == innerUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(unrelatedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Outer_project_reconciliation_scan_excludes_sources_owned_by_a_nested_manifest()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nested-reconciliation-boundary-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "OuterBook", "src");
            WriteManifest(innerProjectRoot, "InnerBook", "src/Inner");

            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            File.WriteAllText(
                outerPath,
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n");
            File.WriteAllText(
                innerPath,
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n");

            var resolution = new VbaProjectResolution(
                VbaProjectResolutionKind.ManifestDocument,
                outerSourceRoot,
                Path.Combine(projectRoot, "vba-project.json"),
                "OuterBook",
                "excel",
                []);
            var scan = await new VbaFileSystemProjectDiskInventory()
                .ObserveReconciliationAsync(
                    new VbaProjectDiskObservationRequest(
                        new VbaProjectDiskProjectScope(
                            resolution.Kind,
                            resolution.RootPath,
                            resolution.ManifestPath),
                        manifestCandidates: [],
                        barrierOverrides: [],
                        observedManifestBarrierUris: []),
                    CancellationToken.None);

            Assert.Contains(
                scan.Sources,
                source => source.FullPath.Equals(
                    outerPath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                scan.Sources,
                source => source.FullPath.Equals(
                    innerPath,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void WriteManifest(
        string projectRoot,
        string documentName,
        string sourcePath)
        => File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            CreateManifestText(documentName, sourcePath));

    private static string CreateManifestText(
        string documentName,
        string sourcePath)
        =>
            $$"""
            {
              "schemaVersion": 1,
              "projectName": "{{documentName}}Project",
              "primaryDocument": "{{documentName}}",
              "documents": {
                "{{documentName}}": {
                  "kind": "excel",
                  "sourcePath": "{{sourcePath}}",
                  "templatePath": "{{documentName}}.xlsm",
                  "binPath": "bin/{{documentName}}.xlsm",
                  "publishPath": "publish/{{documentName}}.xlsm",
                  "references": []
                }
              }
            }
            """;
}
