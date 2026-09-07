using System.Text;
using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed partial class VbaRetainedProjectAnalysisTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preview_reopening_reuses_complete_analysis_with_fresh_ownership(
        bool keepOneSourceOpen)
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer);
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        if (keepOneSourceOpen)
        {
            workspace.OpenDocument(project.HelperUri, 1, project.HelperText);
        }

        var first = workspace.CreateProjectSnapshot(project.CallerUri);
        var expectedTokens = first.SemanticInventory
            .GetSemanticTokenData(project.CallerUri).ToArray();
        Assert.NotEmpty(expectedTokens);
        Assert.True(workspace.CloseDocument(project.CallerUri));
        if (!keepOneSourceOpen)
        {
            Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
            using var reconciliation = workspace.CaptureProjectReconciliation();
            Assert.Empty(reconciliation.Scopes);
        }

        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(1, observer.SemanticBuilds);
        Assert.NotSame(first, reopened);
        Assert.NotSame(first.DiagnosticsOwnership, reopened.DiagnosticsOwnership);
        Assert.Equal(expectedTokens, reopened.SemanticInventory
            .GetSemanticTokenData(project.CallerUri));
        using var oracleProject = project.CreateFreshWorkspace();
        var oracle = oracleProject.Workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.Equal(oracle.SemanticInventory.FindReferences(project.CallerUri, 2, 8),
            reopened.SemanticInventory.FindReferences(project.CallerUri, 2, 8));
    }

    private static VbaLanguageWorkspace CreateWorkspace(
        BuildObserver observer, VbaRetainedAnalysisLimits? limits = null)
        => new(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            observer,
            SystemVbaProjectFileSystem.Instance,
            retainedAnalysisLimits: limits);

    [Fact]
    public void Oversized_analysis_uses_the_correct_cold_path_without_retention()
    {
        using var project = new ProjectFixture();
        var observer = new BuildObserver();
        var workspace = CreateWorkspace(observer, new(MaximumBytes: 1));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var first = workspace.CreateProjectSnapshot(project.CallerUri);
        var expectedTokens = first.SemanticInventory.GetSemanticTokenData(project.CallerUri).ToArray();
        Assert.True(workspace.CloseDocument(project.CallerUri));
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        var reopened = workspace.CreateProjectSnapshot(project.CallerUri);

        Assert.Equal(2, observer.SemanticBuilds);
        Assert.Equal(expectedTokens, reopened.SemanticInventory.GetSemanticTokenData(project.CallerUri));
    }

    [Fact]
    public void Workspace_teardown_releases_retained_analysis_and_active_authority()
    {
        using var project = new ProjectFixture();
        var workspace = CreateWorkspace(new BuildObserver());
        workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
        _ = workspace.CreateProjectSnapshot(project.CallerUri);
        Assert.True(workspace.RetainedReusableAnalysisBytes > 0);
        workspace.Dispose();

        Assert.Equal(0, workspace.RetainedReusableAnalysisCount);
        Assert.Equal(0, workspace.RetainedReusableAnalysisBytes);
        Assert.Equal(0, workspace.RetainedProjectSnapshotCount);
        Assert.Equal(0, workspace.RetainedReconciliationScopeCount);
    }

    [Fact]
    public void Retention_evicts_the_least_recently_used_project_after_four_entries()
    {
        var projects = Enumerable.Range(0, 5).Select(_ => new ProjectFixture()).ToArray();
        try
        {
            var observer = new BuildObserver();
            var workspace = CreateWorkspace(observer);
            foreach (var project in projects.Take(4))
            {
                workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
                _ = workspace.CreateProjectSnapshot(project.CallerUri);
                Assert.True(workspace.CloseDocument(project.CallerUri));
            }

            var recentlyUsed = projects[0];
            workspace.OpenDocument(recentlyUsed.CallerUri, 1, recentlyUsed.CallerText);
            _ = workspace.CreateProjectSnapshot(recentlyUsed.CallerUri);
            Assert.True(workspace.CloseDocument(recentlyUsed.CallerUri));

            var newest = projects[^1];
            workspace.OpenDocument(newest.CallerUri, 1, newest.CallerText);
            _ = workspace.CreateProjectSnapshot(newest.CallerUri);
            Assert.True(workspace.CloseDocument(newest.CallerUri));
            Assert.Equal(5, observer.SemanticBuilds);

            workspace.OpenDocument(recentlyUsed.CallerUri, 1, recentlyUsed.CallerText);
            _ = workspace.CreateProjectSnapshot(recentlyUsed.CallerUri);
            Assert.True(workspace.CloseDocument(recentlyUsed.CallerUri));
            Assert.Equal(5, observer.SemanticBuilds);

            var evicted = projects[1];
            workspace.OpenDocument(evicted.CallerUri, 1, evicted.CallerText);
            var rebuilt = workspace.CreateProjectSnapshot(evicted.CallerUri);
            Assert.Equal(6, observer.SemanticBuilds);
            Assert.NotEmpty(rebuilt.SemanticInventory.GetSemanticTokenData(evicted.CallerUri));
        }
        finally
        {
            foreach (var project in projects)
            {
                project.Dispose();
            }
        }
    }

    [Fact]
    public void Total_analysis_size_evicts_entries_that_individually_fit_the_budget()
    {
        using var first = new ProjectFixture();
        using var second = new ProjectFixture();
        using var measurement = CreateWorkspace(new BuildObserver());
        measurement.OpenDocument(first.CallerUri, 1, first.CallerText);
        _ = measurement.CreateProjectSnapshot(first.CallerUri);
        var limit = measurement.RetainedReusableAnalysisBytes * 3 / 2;
        Assert.True(limit > 0);
        var observer = new BuildObserver();
        using var workspace = CreateWorkspace(observer, new(MaximumBytes: limit));
        foreach (var project in new[] { first, second })
        {
            workspace.OpenDocument(project.CallerUri, 1, project.CallerText);
            _ = workspace.CreateProjectSnapshot(project.CallerUri);
            Assert.True(workspace.CloseDocument(project.CallerUri));
            Assert.Equal(1, workspace.RetainedReusableAnalysisCount);
            Assert.InRange(workspace.RetainedReusableAnalysisBytes, 1, limit);
        }

        for (var cycle = 0; cycle < 20; cycle++)
        {
            workspace.OpenDocument(second.CallerUri, 1, second.CallerText);
            _ = workspace.CreateProjectSnapshot(second.CallerUri);
            Assert.True(workspace.CloseDocument(second.CallerUri));
            Assert.Equal(1, workspace.RetainedReusableAnalysisCount);
            Assert.InRange(workspace.RetainedReusableAnalysisBytes, 1, limit);
        }

        Assert.Equal(2, observer.SemanticBuilds);
        workspace.OpenDocument(first.CallerUri, 1, first.CallerText);
        _ = workspace.CreateProjectSnapshot(first.CallerUri);
        Assert.Equal(3, observer.SemanticBuilds);
    }

    private sealed class BuildObserver : IVbaProjectSnapshotBuildObserver
    {
        public int SemanticBuilds { get; private set; }

        public void BeforeBuildSemanticInventory(string activeUri, CancellationToken cancellationToken)
            => SemanticBuilds++;

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
        }
    }

    private sealed class ProjectFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("vba-retained-analysis-").FullName;
        public string CallerText { get; } = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    BuildValue\nEnd Sub\n";
        public string HelperText { get; } = "Attribute VB_Name = \"Helper\"\nPublic Sub BuildValue()\nEnd Sub\n";
        public string CallerUri => new Uri(Path.Combine(Root, "src", "Caller.bas")).AbsoluteUri;
        public string HelperUri => new Uri(Path.Combine(Root, "src", "Helper.bas")).AbsoluteUri;

        public ProjectFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "vba-project.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "RetainedAnalysis",
                primaryDocument = "Book1",
                documents = new Dictionary<string, object>
                {
                    ["Book1"] = new
                    {
                        kind = "excel", sourcePath = "src", templatePath = "Book1.xlsm",
                        binPath = "bin/Book1.xlsm", publishPath = "publish/Book1.xlsm",
                        commonModules = Array.Empty<object>(), references = Array.Empty<object>()
                    }
                }
            }));
            File.WriteAllText(new Uri(CallerUri).LocalPath, CallerText, new UTF8Encoding(true));
            File.WriteAllText(new Uri(HelperUri).LocalPath, HelperText, new UTF8Encoding(true));
        }

        public OracleWorkspace CreateFreshWorkspace()
        {
            var workspace = CreateWorkspace(new BuildObserver());
            workspace.OpenDocument(CallerUri, 1, File.ReadAllText(new Uri(CallerUri).LocalPath));
            return new OracleWorkspace(workspace, CallerUri);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record OracleWorkspace(VbaLanguageWorkspace Workspace, string Uri) : IDisposable
    {
        public void Dispose() => Workspace.CloseDocument(Uri);
    }
}
