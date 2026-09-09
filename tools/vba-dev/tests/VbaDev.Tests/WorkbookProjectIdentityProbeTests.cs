using VbaDev.App.Build;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.FileSystem;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookProjectIdentityProbeTests
{
    [Fact]
    public async Task CleanupFailureRetainsTheOriginalInspectionFailure()
    {
        using var temp = TempDirectory.Create();
        var source = Path.Combine(temp.Path, "Book1.xlsm");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(source, original);
        var cause = new IOException("The captured workbook could not be inspected.");
        string? probePath = null;
        var probe = new WorkbookProjectIdentityProbe(new WindowsExactFileSystemObjectOwnershipFactory(),
            new FailingAutomation(cause, path => { probePath = path; File.WriteAllBytes(path, [9, 8, 7]); }));

        try
        {
            var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
                () => probe.ReadAsync(CapturedWorkbookTemplate.Capture(source), ["Visual Basic For Applications"], CancellationToken.None));

            Assert.Contains(cause, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
            Assert.NotNull(probePath);
            Assert.Contains(probePath, error.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(probePath));
            var facts = WorkbookAutomationTerminalFacts.Analyze(error);
            Assert.True(facts.ProcessReleaseProven);
            Assert.True(facts.DispatcherRetired);
            Assert.Equal(WorkbookAutomationDisposition.Failed, facts.Disposition);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally
        {
            RemoveOwnedProbeFixture(probePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovedLifecycleRetainsTheOwnedCopyEvenWhenCancellationWasRequested(bool processReleased)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var source = Path.Combine(temp.Path, "Book1.xlsm");
        byte[] original = [4, 3, 2, 1];
        File.WriteAllBytes(source, original);
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen);
        var cleanup = new WorkbookAutomationCleanupException("The probe did not prove its lifecycle.");
        ((IWorkbookAutomationLifecycleFailure)cleanup).LifecycleEvidence =
            new(stage, processReleased, !processReleased, true);
        var cause = new AggregateException(new WorkbookAutomationCanceledException(stage, cancellation.Token), cleanup);
        string? probePath = null;
        var probe = new WorkbookProjectIdentityProbe(new WindowsExactFileSystemObjectOwnershipFactory(),
            new FailingAutomation(cause, path => { probePath = path; cancellation.Cancel(); }));

        try
        {
            var error = await Record.ExceptionAsync(() => probe.ReadAsync(CapturedWorkbookTemplate.Capture(source), ["Visual Basic For Applications"], cancellation.Token));

            Assert.NotNull(error);
            Assert.NotNull(probePath);
            Assert.True(File.Exists(probePath));
            Assert.Equal(original, File.ReadAllBytes(probePath));
            Assert.Contains(probePath, error.Message, StringComparison.Ordinal);
            var facts = WorkbookAutomationTerminalFacts.Analyze(error, callerCancellationRequested: true);
            Assert.Equal(processReleased, facts.ProcessReleaseProven);
            Assert.Equal(!processReleased, facts.DispatcherRetired);
            Assert.Equal(WorkbookAutomationDisposition.Failed, facts.Disposition);
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally
        {
            RemoveOwnedProbeFixture(probePath);
        }
    }

    [Fact]
    public async Task ReadsOnlyTheCapturedCopyAndCleansItWithoutSavingOrImporting()
    {
        using var temp = TempDirectory.Create();
        var source = Path.Combine(temp.Path, "Book1.xlsm");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(source, original);
        var captured = CapturedWorkbookTemplate.Capture(source);
        File.Delete(source);
        var inner = new FakeWorkbookGenerationAutomation { ProjectName = "ObservedProject" };
        var reference = new WorkbookReference("Visual Basic For Applications", false, "VBA",
            "000204ef-0000-0000-c000-000000000046", 4, 2);
        inner.References.Add(reference);
        string? probePath = null;
        var automation = new ObservedAutomation(inner, path =>
        {
            probePath = path;
            Assert.NotEqual(source, path);
            Assert.Equal(original, File.ReadAllBytes(path));
        });
        var probe = new WorkbookProjectIdentityProbe(new WindowsExactFileSystemObjectOwnershipFactory(), automation);

        var identity = await probe.ReadAsync(captured, ["Visual Basic For Applications"], CancellationToken.None);

        inner.References.Clear();
        Assert.Equal("ObservedProject", identity.ProjectName);
        Assert.Equal(reference, Assert.Single(identity.References));
        Assert.NotNull(probePath);
        Assert.False(File.Exists(probePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(probePath)));
        Assert.Empty(inner.ImportedSources);
        Assert.Empty(inner.Events);
        Assert.Equal(0, inner.SaveCalls);
        Assert.Equal(0, inner.VerifyCalls);
        Assert.False(File.Exists(source));
    }

    private sealed class ObservedAutomation(IWorkbookGenerationAutomation inner, Action<string> observe)
        : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(string workbookPath, WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            observe(workbookPath);
            return inner.RunAsync(workbookPath, timeouts, operation, cancellationToken);
        }
    }

    private sealed class FailingAutomation(Exception error, Action<string> observe) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(string workbookPath, WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
        {
            observe(workbookPath);
            return Task.FromException<TResult>(error);
        }
    }

    private static void RemoveOwnedProbeFixture(string? probePath)
    {
        if (probePath is null) return;
        var directory = Path.GetFullPath(Path.GetDirectoryName(probePath)!);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(directory));
        Assert.StartsWith("vba-dev-project-identity-", Path.GetFileName(directory), StringComparison.Ordinal);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
