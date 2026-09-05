using VbaDev.Infrastructure.FileSystem;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using VbaDev.App.Build;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class TestCommandTests
{
    [Fact]
    public async Task NdjsonFormatEmitsEventRecordsForWorkbookTestRun()
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
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                workbookTestRunner: runner));

        var result = await application.RunAsync(["test", "--no-build", "--format", "ndjson"]);

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
    public void LegacySynchronousWorkbookTestRunnerStillRunsThroughTestCommand()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new LegacySynchronousWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(
            root,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([binPath], runner.Workbooks);
        Assert.Contains("1 passed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTestCommandRequestConstructionAndDeconstructionRemainAvailable()
    {
        var selector = new WorkbookTestSelector("Test_Module", "Test_Passes");

        var request = new TestCommandRequest("text", BuildFirst: true, selector);
        var (format, buildFirst, deconstructedSelector) = request;

        Assert.Equal("text", format);
        Assert.True(buildFirst);
        Assert.Same(selector, deconstructedSelector);
        Assert.Equal(TimeSpan.FromSeconds(600), request.ExecutionTimeout);
        Assert.Null(request.SourceSnapshotPath);
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
    public void SnapshotSourceLocationUsesTheOperationFixedActiveCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("snapshot");
        var persistentPath = temp.CreateDirectory("persistent");
        var sourcePath = Path.Combine(snapshotPath, "Encoded.bas");
        var source = "Attribute VB_Name = \"Test_Café\"\r\nPublic Sub Test_Passes()\r\nEnd Sub\r\n";
        File.WriteAllBytes(sourcePath, Encoding.GetEncoding(1252).GetBytes(source));
        var admission = new VbaSourceAdmission(() => 1252).Admit(snapshotPath, VbaSourceAdmissionIntent.Build);
        var locator = new TestProcedureSourceLocator();
        var result = new TestResultRecord(
            "Book1",
            "Test_Café",
            "Test_Passes",
            TestOutcome.Passed,
            "");

        var located = Assert.Single(locator.LocateSnapshot(
            admission,
            snapshotPath,
            persistentPath,
            [result]));

        var location = Assert.IsType<TestProcedureSourceLocation>(located.Location);
        Assert.Equal(
            new Uri(Path.Combine(persistentPath, "Encoded.bas")).AbsoluteUri,
            location.Uri);
        Assert.Equal(new TestProcedureSourcePosition(1, 11), location.Range.Start);
        Assert.Equal(new TestProcedureSourcePosition(1, 22), location.Range.End);
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--module", "Test_Foo", "--format", "text"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(buildAutomation.OpenedWorkbooks);
        Assert.Equal([new WorkbookTestSelector("Test_Foo", null)], runner.Selectors);
    }

    [Fact]
    public void TestForwardsAnExactCodePageModuleSelector()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("\u00A0", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build", "--module", "\u00A0"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([new WorkbookTestSelector("\u00A0", null)], runner.Selectors);
    }

    [Theory]
    [InlineData("--module", "CDecl")]
    [InlineData("--procedure", "Test_Run$")]
    public void TestRejectsSelectorsThatAreNotExactVbaIdentifiers(
        string option,
        string value)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);
        var arguments = option == "--module"
            ? new[] { "test", "--no-build", option, value }
            : new[] { "test", "--no-build", "--module", "Test_Module", option, value };

        var result = application.Run(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("VBA IDENTIFIER", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(runner.Selectors);
    }

    [Fact]
    public void TestRejectsAModuleSelectorLongerThan31Runes()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(
            ["test", "--no-build", "--module", new string('\u00A0', 32)]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("31 characters", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(runner.Selectors);
    }

    [Fact]
    public void TestRejectsAProcedureSelectorLongerThan255Characters()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(
            [
                "test",
                "--no-build",
                "--module",
                "Test_Module",
                "--procedure",
                new string('A', 256)
            ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("255 characters", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(runner.Selectors);
    }

    [Fact]
    public void TestRejectsProcedureSelectorWithoutModuleSelector()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));

        var result = CommandLineTestFactory
            .Create(root, workbookTestRunner: new FakeWorkbookTestRunner())
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--format", "text"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(buildAutomation.OpenedWorkbooks);
        Assert.Equal([Path.Combine(root, "bin", "Book1.xlsm")], runner.Workbooks);
        Assert.DoesNotContain("Built ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("ndjson")]
    public void TestKeepsResultOutputExactAndForwardsBuildRecasingWarnings(string format)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var exactResult = application.Run(["test", "--format", format]);
        buildAutomation.VerificationReport = new VbeImportVerificationReport(
        [
            new VbeIdentifierRecasingWarning(
                "Local",
                [new VbeIdentifierRecasingPair("FileName", "Filename")])
        ]);
        var warnedResult = application.Run(["test", "--format", format]);

        Assert.Equal(0, exactResult.ExitCode);
        Assert.Equal(0, warnedResult.ExitCode);
        Assert.Equal(exactResult.StandardOutput, warnedResult.StandardOutput);
        Assert.Empty(exactResult.StandardError);
        Assert.Equal(
            "[WARN] vbeIdentifierRecased: Imported component 'Local' identifier casing (source -> VBE): 'FileName' -> 'Filename'."
            + Environment.NewLine,
            warnedResult.StandardError);
    }

    [Fact]
    public void TestRetainsBuildRecasingWarningWhenAWorkbookTestFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow(
                "Test_Module",
                "Test_Fails",
                "NG",
                "Expected 1 but was 2"));
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var exactResult = application.Run(["test", "--format", "ndjson"]);
        buildAutomation.VerificationReport = new VbeImportVerificationReport(
        [
            new VbeIdentifierRecasingWarning(
                "Local",
                [new VbeIdentifierRecasingPair("FileName", "Filename")])
        ]);
        var warnedResult = application.Run(["test", "--format", "ndjson"]);

        Assert.Equal(1, exactResult.ExitCode);
        Assert.Equal(1, warnedResult.ExitCode);
        Assert.Equal(exactResult.StandardOutput, warnedResult.StandardOutput);
        Assert.Empty(exactResult.StandardError);
        Assert.Equal(
            "[WARN] vbeIdentifierRecased: Imported component 'Local' identifier casing (source -> VBE): 'FileName' -> 'Filename'."
            + Environment.NewLine,
            warnedResult.StandardError);
    }

    [Fact]
    public void DefaultBuildBeforeTestStopsOnSourceIdentityConflictBeforeTheRunner()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Alpha.bas", "Attribute VB_Name = \"CollisionName\"\r\n"),
            ("Zeta.bas", "Attribute VB_Name = \"collisionname\"\r\n"));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "previous-bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner();
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(["test", "--format", "text"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Source identity", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Alpha.bas", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Zeta.bas", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(buildAutomation.OpenedWorkbooks);
        Assert.Empty(runner.Workbooks);
        Assert.Equal("previous-bin", File.ReadAllText(binPath, Encoding.UTF8));
    }

    [Fact]
    public void SnapshotTestBuildsAndRunsSameFilenameWorkbookWithoutTouchingManifestBin()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var persistentSourcePath = Path.Combine(root, "missing-source", "Book1");
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            SourcePath = Path.GetRelativePath(root, persistentSourcePath),
            TemplatePath = Path.GetRelativePath(root, templatePath),
            BinPath = Path.GetRelativePath(root, binPath)
        };
        new JsonProjectManifestStore().Save(root, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        var manifestBinBytes = Encoding.UTF8.GetBytes("persistent-bin");
        File.WriteAllBytes(binPath, manifestBinBytes);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "nested", "Test_Module.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotSourcePath)!);
        var snapshotBytes = Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n");
        File.WriteAllBytes(snapshotSourcePath, snapshotBytes);
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        var testWorkbookPath = Assert.Single(runner.Workbooks);
        Assert.Equal("Book1.xlsm", Path.GetFileName(testWorkbookPath));
        Assert.NotEqual(binPath, testWorkbookPath);
        Assert.False(File.Exists(testWorkbookPath));
        Assert.Equal(manifestBinBytes, File.ReadAllBytes(binPath));
        Assert.False(Directory.Exists(persistentSourcePath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotSourcePath));
        Assert.Contains("import:Test_Module.bas", buildAutomation.Events);
        Assert.Contains("\"type\":\"runFinished\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotTestPreflightFailureSuppressesRunnerAndPreservesCallerArtifacts()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var persistentSourcePath = Path.Combine(root, "missing-source", "Book1");
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            SourcePath = Path.GetRelativePath(root, persistentSourcePath),
            TemplatePath = Path.GetRelativePath(root, templatePath),
            BinPath = Path.GetRelativePath(root, binPath)
        };
        new JsonProjectManifestStore().Save(root, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        var manifestBinBytes = Encoding.UTF8.GetBytes("persistent-bin");
        File.WriteAllBytes(binPath, manifestBinBytes);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var firstSourcePath = Path.Combine(snapshotPath, "first", "Alpha.bas");
        var secondSourcePath = Path.Combine(snapshotPath, "second", "Zeta.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(firstSourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondSourcePath)!);
        var firstSourceBytes = Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"CollisionName\"\r\n");
        var secondSourceBytes = Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"collisionname\"\r\n");
        File.WriteAllBytes(firstSourcePath, firstSourceBytes);
        File.WriteAllBytes(secondSourcePath, secondSourceBytes);
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Source identity", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(firstSourcePath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondSourcePath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(buildAutomation.OpenedWorkbooks);
        Assert.Empty(runner.Workbooks);
        Assert.Equal(manifestBinBytes, File.ReadAllBytes(binPath));
        Assert.Equal(firstSourceBytes, File.ReadAllBytes(firstSourcePath));
        Assert.Equal(secondSourceBytes, File.ReadAllBytes(secondSourcePath));
        Assert.False(Directory.Exists(persistentSourcePath));
    }

    [Fact]
    public void SnapshotTestRejectsNoBuildBeforeCreatingOrRunningAnything()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--no-build"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--source-snapshot", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--no-build", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(buildAutomation.OpenedWorkbooks);
        Assert.Empty(runner.Workbooks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SnapshotTestRejectsAnExplicitBlankSnapshotBeforeBuild(string snapshotPath)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var buildAutomation = new FakeWorkbookBuildAutomation();
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--source-snapshot", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("non-empty", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(buildAutomation.OpenedWorkbooks);
        Assert.Empty(runner.Workbooks);
        Assert.False(File.Exists(Path.Combine(root, "bin", "Book1.xlsm")));
    }

    [Fact]
    public void SnapshotTestDoesNotAcceptAnOutputOption()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--output",
            Path.Combine(temp.Path, "caller-output.xlsm")
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--output", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void SnapshotWorkspaceOverlapWithCallerSnapshotFailsBeforeWorkspaceCreation()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var callerSourcePath = Path.Combine(snapshotPath, "Test_Module.bas");
        File.WriteAllText(
            callerSourcePath,
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = Path.Combine(snapshotPath, "vba-dev-snapshot-test");
        var runner = new FakeWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("outside the caller-owned source snapshot", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(scratchRoot, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(runner.Workbooks);
        Assert.False(Directory.Exists(scratchRoot));
        Assert.Equal(
            "Attribute VB_Name = \"Test_Module\"",
            File.ReadAllText(callerSourcePath, Encoding.UTF8));
    }

    [Fact]
    public void SnapshotTestUsesFrozenRangesWithPersistentSourceUris()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var persistentSourcePath = Path.Combine(root, "persistent-source", "Book1");
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            SourcePath = Path.GetRelativePath(root, persistentSourcePath),
            TemplatePath = Path.GetRelativePath(root, templatePath)
        };
        new JsonProjectManifestStore().Save(root, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "nested", "Test_Module.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotSourcePath)!);
        File.WriteAllText(
            snapshotSourcePath,
            "Attribute VB_Name = \"Test_Module\"\n' frozen line one\n' frozen line two\nPublic Sub Test_Passes()\nEnd Sub\n",
            Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var buildAutomation = new FakeWorkbookBuildAutomation
        {
            OnImport = () => File.WriteAllText(
                snapshotSourcePath,
                "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n",
                Encoding.UTF8)
        };
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: buildAutomation,
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        var location = finished.RootElement.GetProperty("location");
        Assert.Equal(
            new Uri(Path.Combine(persistentSourcePath, "nested", "Test_Module.bas")).AbsoluteUri,
            location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(3, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.DoesNotContain(snapshotPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n",
            File.ReadAllText(snapshotSourcePath, Encoding.UTF8));
        Assert.False(Directory.Exists(persistentSourcePath));
        Assert.False(File.Exists(Path.Combine(root, "bin", "Book1.xlsm")));
    }

    [Fact]
    public void SnapshotTestPreservesPersistentUrisThatResemblePrivateStagingPaths()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var persistentSourcePath = Path.Combine(
            Path.GetTempPath(),
            "vba-dev-vbe-import",
            Guid.NewGuid().ToString("N"));
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            SourcePath = persistentSourcePath,
            TemplatePath = Path.GetRelativePath(root, templatePath)
        };
        new JsonProjectManifestStore().Save(root, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n",
            Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal(
            new Uri(Path.Combine(persistentSourcePath, "Test_Module.bas")).AbsoluteUri,
            finished.RootElement.GetProperty("location").GetProperty("uri").GetString());
    }

    [Fact]
    public void AmbiguousSnapshotLocationRemainsACompletedNonFailingOutcome()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\nPublic Sub Test_Passes()\nEnd Sub\n",
            Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(0, result.ExitCode);
        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
        Assert.False(finished.RootElement.TryGetProperty("location", out _));
        Assert.Contains("\"type\":\"runFinished\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Warning:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Test_Module.Test_Passes", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("safely or unambiguously", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OK", 0)]
    [InlineData("NG", 1)]
    public void SnapshotCleanupFailurePreservesCompleteNdjsonAndReportsRetainedWorkspace(
        string workbookOutcome,
        int expectedExitCode)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n",
            Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow(
                "Test_Module",
                "Test_Passes",
                workbookOutcome,
                workbookOutcome == "OK" ? "" : "synthetic assertion failure"));
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var fileSystem = new AlwaysFailingSnapshotWorkspaceFileSystem();
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(
                new FileSystemPathIdentityResolver(),
                temp.CreateDirectory("snapshot-test-scratch"),
                fileSystem,
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Contains("\"type\":\"runFinished\"", result.StandardOutput, StringComparison.Ordinal);
        var retainedPath = Assert.Single(fileSystem.DeletePaths.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3, fileSystem.DeletePaths.Count);
        Assert.True(Path.IsPathFullyQualified(retainedPath));
        Assert.Contains("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(retainedPath));
    }

    [Fact]
    public async Task SnapshotCaptureCancellationPreserves130AndReportsFailedRollback()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var fileSystem = new AlwaysFailingSnapshotWorkspaceFileSystem();
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(
                new FileSystemPathIdentityResolver(),
                scratchRoot,
                fileSystem,
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await application.RunAsync(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var retainedPath = Assert.Single(fileSystem.DeletePaths.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3, fileSystem.DeletePaths.Count);
        Assert.Contains("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(retainedPath));
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public async Task NestedSnapshotCaptureCancellationPreserves130AfterSuccessfulRollback()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(
                new FileSystemPathIdentityResolver(),
                scratchRoot,
                new SnapshotTestWorkspaceFileSystem(),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero,
                sourceCaptureFactory: new NestedCancellationSnapshotSourceCaptureFactory()));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await application.RunAsync(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void SnapshotCapturePreparationFailureDoesNotExposePrivateWorkspacePaths()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner();
        var captureFactory = new PathReportingFailingSnapshotSourceCaptureFactory();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(
                new FileSystemPathIdentityResolver(),
                scratchRoot,
                new SnapshotTestWorkspaceFileSystem(),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero,
                sourceCaptureFactory: captureFactory));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("synthetic snapshot capture failure", result.StandardError, StringComparison.Ordinal);
        Assert.NotNull(captureFactory.CaptureScratchRoot);
        Assert.DoesNotContain(
            captureFactory.CaptureScratchRoot,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            new Uri(captureFactory.CaptureScratchRoot).AbsoluteUri,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scratchRoot, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retained", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void SnapshotBuildReleaseProofFailureRetainsWorkspaceAndEmitsNoNdjson()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new CleanupFailingWorkbookGenerationAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.DoesNotContain("runFinished", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(runner.Workbooks);
        var retainedPath = Assert.Single(Directory.EnumerateDirectories(scratchRoot));
        Assert.Contains("could not be verified as released", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(retainedPath));
    }

    [Fact]
    public void SnapshotBuildValidationFailureUsesCallerProvenanceWithoutInternalPaths()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        var brokenSourcePath = Path.Combine(snapshotPath, "nested", "Broken.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenSourcePath)!);
        File.WriteAllBytes(brokenSourcePath, [0xff, 0xfe, 0x00, 0x00]);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(brokenSourcePath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scratchRoot, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "vba-dev-build-source-snapshot",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void SnapshotBuildAutomationFailureDoesNotExposePrivateWorkbookOrStagingPaths()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner();
        var privateVbePath = Path.Combine(
            Path.GetTempPath(),
            "vba-dev-vbe-import",
            Guid.NewGuid().ToString("N"));
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new PathReportingFailingWorkbookBuildAutomation(
                privateVbePath),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("synthetic workbook automation failure", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(scratchRoot, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateVbePath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            new Uri(privateVbePath).AbsoluteUri,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vba-dev-vbe-import", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void WrappedTestReleaseProofFailureRetainsWorkspaceAndEmitsNoNdjson()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner
        {
            Error = new InvalidOperationException(
                "The test runner failed after cleanup.",
                new WorkbookAutomationCleanupException(
                    "The owned Excel process could not be verified as released."))
        };
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.DoesNotContain("runFinished", result.StandardOutput, StringComparison.Ordinal);
        var retainedPath = Assert.Single(Directory.EnumerateDirectories(scratchRoot));
        Assert.Contains("test runner failed after cleanup", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(retainedPath));
    }

    [Fact]
    public void LegacyCleanupExceptionFromSnapshotRunnerRetainsWorkspaceAndEmitsNoNdjson()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner
        {
            Error = new WorkbookAutomationCleanupException(
                "The owned Excel process could not be verified as released.")
        };
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var retainedPath = Assert.Single(Directory.EnumerateDirectories(scratchRoot));
        Assert.Contains("could not be verified as released", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(retainedPath));
    }

    [Fact]
    public void UnexpectedSnapshotRunnerFailureRemovesWorkspaceAndEmitsNoNdjson()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new FakeWorkbookTestRunner
        {
            Error = new Exception("synthetic unexpected runner failure")
        };
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("synthetic unexpected runner failure", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void SnapshotRunnerFailureDoesNotExposeItsPrivateWorkbookPath()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new PathReportingFailingWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("synthetic test runner failure", result.StandardError, StringComparison.Ordinal);
        Assert.NotNull(runner.WorkbookPath);
        Assert.DoesNotContain(runner.WorkbookPath, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            new Uri(runner.WorkbookPath).AbsoluteUri,
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(scratchRoot, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void SnapshotResultMessageDoesNotExposeItsPrivateWorkbookPathInNdjson()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        new JsonProjectManifestStore().Save(root, manifest);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(templatePath, "snapshot-test-template", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Fails()\nEnd Sub\n",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("snapshot-test-scratch");
        var runner = new PathMessageWorkbookTestRunner();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(
            root,
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            workbookTestRunner: runner);
        var testCommand = new TestCommand(
            composition.BuildCommand,
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot));
        var application = VbaDevCommandLine.Create(composition with { TestCommand = testCommand });

        var result = application.Run(
        [
            "test",
            "--source-snapshot",
            snapshotPath,
            "--format",
            "ndjson"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.NotNull(runner.WorkbookPath);
        Assert.DoesNotContain(runner.WorkbookPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            runner.WorkbookPath.Replace("\\", "\\\\", StringComparison.Ordinal),
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            new Uri(runner.WorkbookPath).AbsoluteUri,
            result.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var _ = JsonDocument.Parse(line);
        }

        var finishedLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var finished = JsonDocument.Parse(finishedLine);
        Assert.Contains(
            "<snapshot-test-workspace>",
            finished.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    [Fact]
    public void TestTimeoutOptionOverridesManifestDefaultForMacroExecution()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                Test: new TestCommandDefaults(
                    Format: "text",
                    ExecutionTimeoutSeconds: 77))
        };
        new JsonProjectManifestStore().Save(root, manifest);
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(
        [
            "test",
            "--no-build",
            "--timeout-seconds",
            "42"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([TimeSpan.FromSeconds(42)], runner.ExecutionTimeouts);
    }

    [Fact]
    public void TestKeepsWorkbookOpenTimeoutIndependentFromMacroExecutionTimeout()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(
                Test: new TestCommandDefaults(ExecutionTimeoutSeconds: 77),
                ExcelAutomation: new ExcelAutomationCommandDefaults(
                    WorkbookOpenTimeoutSeconds: 41))
        };
        new JsonProjectManifestStore().Save(root, manifest);
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "bin", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""));
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--no-build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([TimeSpan.FromSeconds(77)], runner.ExecutionTimeouts);
        Assert.Equal(
            TimeSpan.FromSeconds(41),
            Assert.Single(runner.AutomationTimeouts).WorkbookOpen);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("Infinity")]
    public void TestRejectsInvalidMacroExecutionTimeout(string value)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var runner = new FakeWorkbookTestRunner();
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

        var result = application.Run(["test", "--timeout-seconds", value]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(runner.Workbooks);
        Assert.Contains("timeout", result.StandardError, StringComparison.OrdinalIgnoreCase);
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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
        var application = CommandLineTestFactory.Create(root, workbookTestRunner: runner);

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
    public List<TimeSpan> ExecutionTimeouts { get; } = [];
    public List<WorkbookAutomationTimeouts> AutomationTimeouts { get; } = [];
    public Exception? Error { get; init; }

    public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        string workbookPath,
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionTimeouts.Add(executionTimeout);
        AutomationTimeouts.Add(automationTimeouts);
        if (Error is not null)
        {
            throw Error;
        }

        Workbooks.Add(workbookPath);
        Selectors.Add(selector);
        return Task.FromResult(results);
    }
}

internal sealed class LegacySynchronousWorkbookTestRunner : IWorkbookTestRunner
{
    public List<string> Workbooks { get; } = [];

    public IReadOnlyList<WorkbookTestResultRow> RunTests(
        string workbookPath,
        WorkbookTestSelector selector)
    {
        Workbooks.Add(workbookPath);
        return [new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", "")];
    }
}

internal sealed class PathReportingFailingWorkbookTestRunner : IWorkbookTestRunner
{
    public string? WorkbookPath { get; private set; }

    public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        string workbookPath,
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        WorkbookPath = workbookPath;
        var workbookUri = new Uri(workbookPath).AbsoluteUri;
        return Task.FromException<IReadOnlyList<WorkbookTestResultRow>>(
            new InvalidOperationException(
                $"synthetic test runner failure for '{workbookPath}' ({workbookUri})."));
    }
}

internal sealed class PathMessageWorkbookTestRunner : IWorkbookTestRunner
{
    public string? WorkbookPath { get; private set; }

    public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        string workbookPath,
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        WorkbookPath = workbookPath;
        var workbookUri = new Uri(workbookPath).AbsoluteUri;
        IReadOnlyList<WorkbookTestResultRow> results =
        [
            new WorkbookTestResultRow(
                "Test_Module",
                "Test_Fails",
                "NG",
                $"synthetic failure in '{workbookPath}' ({workbookUri})")
        ];
        return Task.FromResult(results);
    }
}

internal sealed class AlwaysFailingSnapshotWorkspaceFileSystem
    : ISnapshotTestWorkspaceFileSystem
{
    public List<string> DeletePaths { get; } = [];

    public void DeleteDirectory(string path)
    {
        DeletePaths.Add(path);
        throw new IOException("synthetic retained workspace");
    }

    public void Delay(TimeSpan delay)
    {
    }
}

internal sealed class NestedCancellationSnapshotSourceCaptureFactory
    : ISnapshotSourceCaptureFactory
{
    public BuildSourceSnapshotCapture Create(
        string scratchRoot,
        string sourceSnapshotPath,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(
            "The nested snapshot capture rollback failed.",
            new OperationCanceledException(cancellationToken));
}

internal sealed class PathReportingFailingSnapshotSourceCaptureFactory
    : ISnapshotSourceCaptureFactory
{
    public string? CaptureScratchRoot { get; private set; }

    public BuildSourceSnapshotCapture Create(
        string scratchRoot,
        string sourceSnapshotPath,
        CancellationToken cancellationToken)
    {
        CaptureScratchRoot = scratchRoot;
        var scratchRootUri = new Uri(scratchRoot).AbsoluteUri;
        throw new InvalidOperationException(
            $"synthetic snapshot capture failure in '{scratchRoot}' ({scratchRootUri}).");
    }
}

internal sealed class CleanupFailingWorkbookGenerationAutomation :
    IWorkbookBuildAutomation,
    IWorkbookGenerationAutomation
{
    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
        => throw new InvalidOperationException("The native generation path must be used.");

    public Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
        => Task.FromException<TResult>(new WorkbookAutomationCleanupException(
            "The owned Excel process could not be verified as released."));
}

internal sealed class PathReportingFailingWorkbookBuildAutomation(string privateVbePath)
    : IWorkbookBuildAutomation
{
    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
    {
        var workbookUri = new Uri(workbookPath).AbsoluteUri;
        var privateVbeUri = new Uri(privateVbePath).AbsoluteUri;
        throw new InvalidOperationException(
            $"synthetic workbook automation failure for '{workbookPath}' ({workbookUri}) "
            + $"after staging '{privateVbePath}' ({privateVbeUri}).");
    }
}
