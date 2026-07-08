using System.Text;
using VbaDevTools.App.Testing;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class TestCommandTests
{
    [Fact]
    public void NdjsonFormatEmitsOneRecordPerResultPlusSummary()
    {
        var formatter = new TestResultOutputFormatter();
        var results = SampleResults();

        var output = formatter.Format("ndjson", results);

        Assert.Equal(
            "{\"type\":\"result\",\"document\":\"Book1\",\"category\":\"Test_Module\",\"testName\":\"Test_Passes\",\"outcome\":\"passed\",\"message\":\"\",\"durationMilliseconds\":12.5}\n" +
            "{\"type\":\"result\",\"document\":\"Book1\",\"category\":\"Test_Module\",\"testName\":\"Test_Fails\",\"outcome\":\"failed\",\"message\":\"Expected 1 but was 2\"}\n" +
            "{\"type\":\"result\",\"document\":\"Book1\",\"category\":\"Test_Module\",\"testName\":\"Test_Errors\",\"outcome\":\"error\",\"message\":\"Runtime error\"}\n" +
            "{\"type\":\"summary\",\"document\":\"Book1\",\"total\":3,\"passed\":1,\"failed\":1,\"errors\":1}\n",
            output);
    }

    [Fact]
    public void TextFormatEmitsReadableStableTerminalOutput()
    {
        var formatter = new TestResultOutputFormatter();

        var output = formatter.Format("text", SampleResults());

        Assert.Equal(
            "Book1: 1 passed, 1 failed, 1 errors, 3 total\n" +
            "[passed] Test_Module.Test_Passes\n" +
            "[failed] Test_Module.Test_Fails - Expected 1 but was 2\n" +
            "[error] Test_Module.Test_Errors - Runtime error\n",
            output);
    }

    [Fact]
    public void TestRunsAgainstManifestResolvedBinWorkbookWhenBuildIsDisabled()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "text"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([binPath], runner.Workbooks);
        Assert.Empty(buildAutomation.OpenedWorkbooks);
        Assert.Contains("Book1: 1 passed, 0 failed, 0 errors, 1 total", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestBuildsBeforeRunningTestsByDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--format", "text"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(buildAutomation.OpenedWorkbooks);
        Assert.Equal([Path.Combine(root, "bin", "Book1", "Book1.xlsm")], runner.Workbooks);
        Assert.DoesNotContain("Built ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestNormalizesSuccessFailureAndErrorOutcomesAndReturnsFailureExitCode()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""),
            new WorkbookTestResultRow("Test_Module", "Test_Fails", "NG", "failed"),
            new WorkbookTestResultRow("Test_Module", "Test_Errors", "ERR", "errored"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("\"outcome\":\"passed\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"failed\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"error\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void TestUsesManifestDefaultFormatWhenFormatOptionIsOmitted()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(Test: new TestCommandDefaults(Format: "text"))
        };
        new JsonProjectManifestStore().Save(root, manifest);
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Book1: 1 passed", result.StandardOutput, StringComparison.Ordinal);
    }

    private static IReadOnlyList<TestResultRecord> SampleResults()
        =>
        [
            new("Book1", "Test_Module", "Test_Passes", TestOutcome.Passed, "", TimeSpan.FromMilliseconds(12.5)),
            new("Book1", "Test_Module", "Test_Fails", TestOutcome.Failed, "Expected 1 but was 2"),
            new("Book1", "Test_Module", "Test_Errors", TestOutcome.Error, "Runtime error")
        ];

    private static void CreateWorkbookSource(string root, string documentName, params (string FileName, string Content)[] sources)
    {
        var sourceDirectory = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var source in sources)
        {
            File.WriteAllText(Path.Combine(sourceDirectory, source.FileName), source.Content, Encoding.UTF8);
        }
    }
}

internal sealed class FakeWorkbookTestRunner : IWorkbookTestRunner
{
    private readonly IReadOnlyList<WorkbookTestResultRow> results;

    public FakeWorkbookTestRunner(params WorkbookTestResultRow[] results)
    {
        this.results = results;
    }

    public List<string> Workbooks { get; } = [];

    public IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath)
    {
        Workbooks.Add(workbookPath);
        return results;
    }
}
