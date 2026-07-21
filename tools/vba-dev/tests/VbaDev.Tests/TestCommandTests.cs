using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class TestCommandTests
{
    [Fact]
    public void NdjsonFormatEmitsEventRecordsForWorkbookTestRun()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", "", TimeSpan.FromMilliseconds(12.5)),
            new WorkbookTestResultRow("Test_Module", "Test_Fails", "NG", "Expected 1 but was 2"),
            new WorkbookTestResultRow("Test_Module", "Test_Errors", "ERR", "Runtime error"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "{\"type\":\"runStarted\",\"project\":\"Project\",\"document\":\"Book1\"}\n" +
            "{\"type\":\"testStarted\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Passes\"}\n" +
            "{\"type\":\"testFinished\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Passes\",\"outcome\":\"passed\",\"message\":\"\",\"durationMilliseconds\":12.5}\n" +
            "{\"type\":\"testStarted\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Fails\"}\n" +
            "{\"type\":\"testFinished\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Fails\",\"outcome\":\"failed\",\"message\":\"Expected 1 but was 2\"}\n" +
            "{\"type\":\"testStarted\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Errors\"}\n" +
            "{\"type\":\"testFinished\",\"project\":\"Project\",\"document\":\"Book1\",\"module\":\"Test_Module\",\"procedure\":\"Test_Errors\",\"outcome\":\"error\",\"message\":\"Runtime error\"}\n" +
            "{\"type\":\"runFinished\",\"project\":\"Project\",\"document\":\"Book1\",\"outcome\":\"failed\",\"total\":3,\"passed\":1,\"failed\":1,\"errors\":1}\n",
            result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public void NdjsonTestFinishedIncludesTheUniqueProcedureDeclarationRange()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Test_Module.bas", "Attribute VB_Name = \"Test_Module\"\nOption Explicit\n\nPublic Sub Test_Passes()\nEnd Sub\n"));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        var location = finished.RootElement.GetProperty("location");
        Assert.Equal(
            new Uri(Path.Combine(root, "src", "Book1", "Test_Module.bas")).AbsoluteUri,
            location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(3, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(22, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public void Cp932SourceLocationUsesTheEstablishedVbaSourceDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        const string moduleName = "テストモジュール";
        const string procedureName = "Test_Run";
        var source = $"Attribute VB_Name = \"{moduleName}\"\n' 日本語コメント\nPublic Sub {procedureName}()\nEnd Sub\n";
        CreateWorkbookSource(root, "Book1", ("Encoded.bas", string.Empty));
        var sourcePath = Path.Combine(root, "src", "Book1", "Encoded.bas");
        File.WriteAllBytes(sourcePath, Encoding.GetEncoding(932).GetBytes(source));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow(moduleName, procedureName, "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        var location = finished.RootElement.GetProperty("location");
        Assert.Equal(new Uri(sourcePath).AbsoluteUri, location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(19, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public void Utf8NestedFilenameFallbackResolvesCaseInsensitiveMultilineProcedureIdentity()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var nestedDirectory = Path.Combine(root, "src", "Book1", "nested");
        Directory.CreateDirectory(nestedDirectory);
        var sourcePath = Path.Combine(nestedDirectory, "Test_Module.bas");
        var source = "' 日本語😀\nPublic Sub Scenario_Multi( _\n    ByVal value As String)\nEnd Sub\n";
        File.WriteAllBytes(
            sourcePath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(source));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("test_module", "scenario_multi", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        var location = finished.RootElement.GetProperty("location");
        Assert.Equal(new Uri(sourcePath).AbsoluteUri, location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(25, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public void Utf16BomAttributeIdentityTakesPrecedenceOverTheSourceFilename()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        const string source = "Attribute VB_Name = \"Preferred_Module\"\n' 日本語😀\nPublic Sub Test_Utf16()\nEnd Sub\n";
        CreateWorkbookSource(root, "Book1", ("WrongName.bas", string.Empty));
        var sourcePath = Path.Combine(root, "src", "Book1", "WrongName.bas");
        var encoding = Encoding.Unicode;
        File.WriteAllBytes(
            sourcePath,
            encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray());
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("preferred_module", "TEST_UTF16", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        var location = finished.RootElement.GetProperty("location");
        Assert.Equal(new Uri(sourcePath).AbsoluteUri, location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(2, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(2, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(21, range.GetProperty("end").GetProperty("character").GetInt32());
    }

    [Fact]
    public void UnreadableSourceLocationDoesNotChangeTheCompletedTestOutcome()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Test_Module.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n"));
        var sourcePath = Path.Combine(root, "src", "Book1", "Test_Module.bas");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);
        using var sourceLock = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
        Assert.False(finished.RootElement.TryGetProperty("location", out _));
    }

    [Fact]
    public void PartiallyUnreadableSourceInventoryDoesNotClaimAUniqueLocation()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        const string source = "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n";
        CreateWorkbookSource(
            root,
            "Book1",
            ("Readable.bas", source),
            ("Locked.bas", source));
        var lockedSourcePath = Path.Combine(root, "src", "Book1", "Locked.bas");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);
        using var sourceLock = new FileStream(lockedSourcePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
        Assert.False(finished.RootElement.TryGetProperty("location", out _));
    }

    [Fact]
    public void InvalidBomMarkedUtf8OmitsLocationsWithoutChangingTheCompletedOutcome()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Test_Module.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n"),
            ("InvalidUtf8.bas", string.Empty));
        File.WriteAllBytes(
            Path.Combine(root, "src", "Book1", "InvalidUtf8.bas"),
            [0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
        Assert.False(finished.RootElement.TryGetProperty("location", out _));
    }

    [Fact]
    public void MissingModuleLocationIsOmittedWithoutChangingTheCompletedTestOutcome()
    {
        AssertUnavailableSourceLocation(
            "Missing_Module",
            "Test_Passes",
            ("Other_Module.bas", "Attribute VB_Name = \"Other_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n"));
    }

    [Fact]
    public void MissingProcedureLocationIsOmittedWithoutChangingTheCompletedTestOutcome()
    {
        AssertUnavailableSourceLocation(
            "Test_Module",
            "Test_Missing",
            ("Test_Module.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Other()\nEnd Sub\n"));
    }

    [Fact]
    public void DuplicateModuleIdentityOmitsLocationWithoutChangingTheCompletedTestOutcome()
    {
        AssertUnavailableSourceLocation(
            "Test_Module",
            "Test_Passes",
            ("First.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n"),
            ("Second.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n"));
    }

    [Fact]
    public void DuplicateProcedureIdentityOmitsLocationWithoutChangingTheCompletedTestOutcome()
    {
        AssertUnavailableSourceLocation(
            "Test_Module",
            "Test_Passes",
            ("Test_Module.bas", "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\nPublic Sub Test_Passes()\nEnd Sub\n"));
    }

    [Fact]
    public void UnavailableLocationDoesNotChangeTheCompletedFailureOutcome()
    {
        AssertUnavailableSourceLocation(
            new WorkbookTestResultRow("Missing_Module", "Test_Fails", "NG", "Expected 1 but was 2"),
            1,
            "failed",
            ("Other_Module.bas", "Attribute VB_Name = \"Other_Module\"\nPublic Sub Test_Fails()\nEnd Sub\n"));
    }

    [Fact]
    public void TestRunCreatesEventSequenceAsInternalModel()
    {
        var testRun = TestRun.FromResults("Project", "Book1", SampleResults());

        var events = testRun.CreateEvents();

        Assert.Collection(
            events,
            item => Assert.IsType<RunStartedTestRunEvent>(item),
            item =>
            {
                var started = Assert.IsType<TestStartedTestRunEvent>(item);
                Assert.Equal("Test_Module", started.Module);
                Assert.Equal("Test_Passes", started.Procedure);
            },
            item =>
            {
                var finished = Assert.IsType<TestFinishedTestRunEvent>(item);
                Assert.Equal(TestOutcome.Passed, finished.Outcome);
                Assert.Equal(12.5, finished.DurationMilliseconds);
            },
            item => Assert.IsType<TestStartedTestRunEvent>(item),
            item => Assert.IsType<TestFinishedTestRunEvent>(item),
            item => Assert.IsType<TestStartedTestRunEvent>(item),
            item => Assert.IsType<TestFinishedTestRunEvent>(item),
            item =>
            {
                var finished = Assert.IsType<RunFinishedTestRunEvent>(item);
                Assert.Equal(TestOutcome.Failed, finished.Outcome);
                Assert.Equal(3, finished.Total);
                Assert.Equal(1, finished.Passed);
                Assert.Equal(1, finished.Failed);
                Assert.Equal(1, finished.Errors);
            });
    }

    [Fact]
    public void TextFormatEmitsReadableStableTerminalOutput()
    {
        var formatter = new TestResultOutputFormatter();

        var output = formatter.Format("text", "Project", "Book1", SampleResults());

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
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
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
    public void TestForwardsModuleAndProcedureSelectorsWhenBuildIsDisabled()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Foo", "Test_Bar", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--module", "Test_Foo", "--procedure", "Test_Bar", "--format", "ndjson"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([new WorkbookTestSelector("Test_Foo", "Test_Bar")], runner.Selectors);
        Assert.Contains("\"module\":\"Test_Foo\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"procedure\":\"Test_Bar\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"category\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"testName\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestForwardsModuleSelectorThroughDefaultBuildFlow()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Foo", "Test_Bar", "OK", ""));
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--module", "Test_Foo", "--format", "text"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(buildAutomation.OpenedWorkbooks);
        Assert.Equal([new WorkbookTestSelector("Test_Foo", null)], runner.Selectors);
    }

    [Fact]
    public void TestRejectsProcedureSelectorWithoutModuleSelector()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));

        var result = ToolingCompositionRoot
            .CreateCommandLineApplication(root, workbookTestRunner: new FakeWorkbookTestRunner())
            .Run(["test", "--procedure", "Test_Bar"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--procedure requires --module.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void TestReportsSelectorRunnerErrorsAsUsageErrors()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner
        {
            Error = new InvalidOperationException("Test module was not found: MissingModule")
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--module", "MissingModule"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Test module was not found: MissingModule", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void TestReportsComRunnerErrorsAsUsageErrors()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner
        {
            Error = new COMException("0x800A801C", unchecked((int)0x800A801C))
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Excel COM test automation failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("coding agent", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("outside the sandbox", result.StandardError, StringComparison.Ordinal);
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
        Assert.Equal([Path.Combine(root, "bin", "Book1.xlsm")], runner.Workbooks);
        Assert.DoesNotContain("Built ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestNormalizesSuccessFailureAndErrorOutcomesAndReturnsFailureExitCode()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
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
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Book1: 1 passed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TestUsesTextOutputWhenNoFormatOptionOrManifestDefaultExists()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = null
        };
        new JsonProjectManifestStore().Save(root, manifest);
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Book1: 1 passed", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"summary\"", result.StandardOutput, StringComparison.Ordinal);
    }

    private static IReadOnlyList<TestResultRecord> SampleResults()
        =>
        [
            new("Book1", "Test_Module", "Test_Passes", TestOutcome.Passed, "", TimeSpan.FromMilliseconds(12.5)),
            new("Book1", "Test_Module", "Test_Fails", TestOutcome.Failed, "Expected 1 but was 2"),
            new("Book1", "Test_Module", "Test_Errors", TestOutcome.Error, "Runtime error")
        ];

    private static void AssertUnavailableSourceLocation(
        string moduleName,
        string procedureName,
        params (string FileName, string Content)[] sources)
        => AssertUnavailableSourceLocation(
            new WorkbookTestResultRow(moduleName, procedureName, "OK", ""),
            0,
            "passed",
            sources);

    private static void AssertUnavailableSourceLocation(
        WorkbookTestResultRow resultRow,
        int expectedExitCode,
        string expectedOutcome,
        params (string FileName, string Content)[] sources)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", sources);
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(resultRow);
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--format", "ndjson"]);

        Assert.Equal(expectedExitCode, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal(expectedOutcome, finished.RootElement.GetProperty("outcome").GetString());
        Assert.False(finished.RootElement.TryGetProperty("location", out _));
    }

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
    public List<WorkbookTestSelector> Selectors { get; } = [];
    public Exception? Error { get; init; }

    public IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath, WorkbookTestSelector selector)
    {
        if (Error is not null)
        {
            throw Error;
        }

        Workbooks.Add(workbookPath);
        Selectors.Add(selector);
        return results;
    }
}
