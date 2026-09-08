using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class TestTerminalFactsTests
{
    public static IEnumerable<object[]> FailureMatrix()
    {
        foreach (var mode in new[] { "no-build", "ordinary", "snapshot" })
        foreach (var phase in mode == "no-build" ? new[] { "execution" } : new[] { "preparation", "execution" })
        foreach (var category in new[] { "cancel", "timeout", "process-loss", "com", "process-release", "dispatcher", "released-cleanup" })
        foreach (var cancelled in new[] { false, true })
        {
            yield return [mode, phase, category, cancelled];
        }
    }

    [Theory]
    [MemberData(nameof(FailureMatrix))]
    public async Task TestPathsRetainEquivalentPrimaryAndLifecycleFacts(
        string mode, string phase, string category, bool cancelled)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var stage = new WorkbookAutomationStage(phase == "execution"
            ? WorkbookAutomationStageKind.TestExecution : WorkbookAutomationStageKind.ModuleImport, "Test_Module");
        var error = CreateFailure(category, stage, cancellation.Token, cancelled);
        var runnerReached = false;
        var runner = new FakeWorkbookTestRunner
        {
            OnRun = () => { runnerReached = true; if (cancelled) cancellation.Cancel(); },
            Error = error
        };
        var generation = phase == "preparation"
            ? new FailingTestPreparationAutomation(() => { if (cancelled) cancellation.Cancel(); return error; })
            : (IWorkbookGenerationAutomation?)null;
        var fixture = CreateFixture(temp, runner, generation);

        var result = await fixture.Application.RunAsync(Arguments(fixture, mode), cancellation.Token);

        Assert.Equal(category == "cancel" ? 130 : 1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(stage.Description, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(phase == "execution", runnerReached);
        Assert.True(File.Exists(Path.Combine(fixture.SnapshotPath, "Test_Module.bas")));
        if (mode == "snapshot" && category == "process-release")
        {
            var retained = Assert.Single(Directory.GetDirectories(fixture.ScratchRoot));
            Assert.Contains(retained, result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(Directory.GetDirectories(fixture.ScratchRoot));
        }
    }

    public static IEnumerable<object[]> CompletedResultMatrix()
    {
        foreach (var mode in new[] { "no-build", "ordinary", "snapshot" })
        foreach (var status in new[] { "OK", "NG", "ERR", "empty" })
        foreach (var cancelled in new[] { false, true })
        {
            yield return [mode, status, cancelled];
        }
    }

    [Theory]
    [MemberData(nameof(CompletedResultMatrix))]
    public async Task CompletedResultsKeepAssertionSemanticsAndNdjsonDespiteLateCancellation(
        string mode, string status, bool cancelled)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeWorkbookTestRunner(status == "empty" ? []
            : [new WorkbookTestResultRow("Test_Module", "Test_Passes", status, "result message")])
        {
            OnRun = () => { if (cancelled) cancellation.Cancel(); }
        };
        var fixture = CreateFixture(temp, runner);

        var result = await fixture.Application.RunAsync(Arguments(fixture, mode), cancellation.Token);

        Assert.Equal(status is "OK" or "empty" ? 0 : 1, result.ExitCode);
        var records = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(status == "empty" ? ["runStarted", "runFinished"]
            : new[] { "runStarted", "testStarted", "testFinished", "runFinished" },
            records.Select(record => record.GetProperty("type").GetString()));
        Assert.Equal(status is "OK" or "empty" ? "passed" : "failed",
            records[^1].GetProperty("outcome").GetString());
        if (status != "empty")
        {
            Assert.Equal(status == "OK" ? "passed" : status == "NG" ? "failed" : "error",
                records[2].GetProperty("outcome").GetString());
        }
        Assert.DoesNotContain(fixture.ScratchRoot, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(fixture.ScratchRoot));
        Assert.Equal(cancelled, cancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData("cancel", 130)]
    [InlineData("input", 1)]
    [InlineData("defect", 1)]
    public async Task CapturePreparationKeepsPrimaryFailureSeparateFromCleanupEvidence(string failure, int expectedExit)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var cleanup = new AlwaysFailingSnapshotWorkspaceFileSystem();
        var runner = new FakeWorkbookTestRunner();
        var fixture = CreateFixture(temp, runner, sourceCapture: new FailingTestSourceCapture(token =>
        {
            if (failure == "cancel")
            {
                cancellation.Cancel();
                return new OperationCanceledException(token);
            }
            return failure == "input" ? new IOException("Primary capture input failure")
                : new NullReferenceException("Primary capture defect");
        }), cleanupFileSystem: cleanup);

        var result = await fixture.Application.RunAsync(Arguments(fixture, "snapshot"), cancellation.Token);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(runner.Workbooks);
        var retained = Assert.Single(Directory.GetDirectories(fixture.ScratchRoot));
        Assert.Contains(retained, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(failure == "cancel" ? "snapshot test preparation" : "Primary capture",
            result.StandardError, StringComparison.Ordinal);
        Assert.Contains("workspace could not be removed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(3, cleanup.DeletePaths.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownDefectCannotBecomeCancellationOrEraseUnprovedRelease(bool unproved)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var failures = new List<Exception> { new OperationCanceledException(), new NullReferenceException("Defect") };
        if (unproved) failures.Add(new WorkbookAutomationCleanupException("Unproved release"));
        var fixture = CreateFixture(temp, new FakeWorkbookTestRunner
        {
            OnRun = cancellation.Cancel,
            Error = new AggregateException(failures)
        });

        var result = await fixture.Application.RunAsync(Arguments(fixture, "snapshot"), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(unproved ? 1 : 0, Directory.GetDirectories(fixture.ScratchRoot).Length);
        Assert.Contains("Defect", result.StandardError, StringComparison.Ordinal);
    }

    private static Exception CreateFailure(string category, WorkbookAutomationStage stage,
        CancellationToken token, bool cancelled)
    {
        Exception error = category switch
        {
            "cancel" => new WorkbookAutomationCanceledException(stage, token),
            "timeout" => new WorkbookAutomationTimeoutException(stage, TimeSpan.FromSeconds(1)),
            "process-loss" => new WorkbookAutomationProcessLostException(stage),
            "com" => new System.Runtime.InteropServices.COMException("COM detail"),
            "process-release" or "dispatcher" => new WorkbookAutomationCleanupException("Release detail"),
            _ => new WorkbookAutomationReleasedProcessCleanupException("Secondary cleanup detail")
        };
        if (error is IWorkbookAutomationLifecycleFailure lifecycle)
        {
            lifecycle.LifecycleEvidence = new(stage, category != "process-release", category != "dispatcher", cancelled);
        }
        if (cancelled && category != "cancel")
        {
            error = new AggregateException(new WorkbookAutomationCanceledException(stage, token), error);
        }
        return new WorkbookAutomationStageFailureException(stage, error);
    }

    private sealed class FailingTestPreparationAutomation(Func<Exception> failure) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(string workbookPath, WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
            => Task.FromException<TResult>(failure());
    }

    private sealed class FailingTestSourceCapture(Func<CancellationToken, Exception> failure) : ISnapshotSourceCaptureFactory
    {
        public BuildSourceSnapshotCapture Create(string scratchRoot, string sourceSnapshotPath,
            CancellationToken cancellationToken) => throw failure(cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationWithUnprovedReleaseFailsAndRetainsDependentWorkspace(bool snapshot)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeWorkbookTestRunner
        {
            OnRun = cancellation.Cancel,
            Error = new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.TestExecution), cancellation.Token,
                new WorkbookAutomationCleanupException("Owned release could not be proved."))
        };
        var fixture = CreateFixture(temp, runner);

        var result = await fixture.Application.RunAsync(Arguments(fixture, snapshot ? "snapshot" : "no-build"),
            cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        if (snapshot)
        {
            var retained = Assert.Single(Directory.GetDirectories(fixture.ScratchRoot));
            Assert.Contains(retained, result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(retained, "Book1.xlsm")));
        }
    }

    private sealed record Fixture(VbaDevCommandLine Application, string SnapshotPath, string ScratchRoot);

    private static string[] Arguments(Fixture fixture, string mode)
        => mode switch
        {
            "snapshot" => ["test", "--source-snapshot", fixture.SnapshotPath, "--format", "ndjson"],
            "no-build" => ["test", "--no-build", "--format", "ndjson"],
            _ => ["test", "--format", "ndjson"]
        };

    private static Fixture CreateFixture(TempDirectory temp, FakeWorkbookTestRunner runner,
        IWorkbookGenerationAutomation? generation = null,
        ISnapshotSourceCaptureFactory? sourceCapture = null,
        ISnapshotTestWorkspaceFileSystem? cleanupFileSystem = null)
    {
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var source = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Book1.xlsm"), "template", Encoding.UTF8);
        const string module = "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n";
        File.WriteAllText(Path.Combine(source, "Test_Module.bas"), module, Encoding.UTF8);
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "Book1.xlsm"), "previous-bin", Encoding.UTF8);
        var snapshot = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(Path.Combine(snapshot, "Test_Module.bas"), module, new UTF8Encoding(false));
        var scratch = temp.CreateDirectory("snapshot-test-scratch");
        var composition = ToolingCompositionRoot.CreateApplicationComposition(root,
            workbookGenerationAutomation: generation ?? new FakeWorkbookGenerationAutomation(),
            workbookTestRunner: runner);
        var command = new TestCommand(composition.BuildCommand, runner,
            new TestResultOutputFormatter(), new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new WindowsExactFileSystemObjectOwnershipFactory(), new FileSystemPathIdentityResolver(), scratch,
                cleanupFileSystem ?? new SnapshotTestWorkspaceFileSystem(), 3, TimeSpan.Zero,
                sourceCaptureFactory: sourceCapture));
        return new(VbaDevCommandLine.Create(composition with { TestCommand = command }), snapshot, scratch);
    }
}
