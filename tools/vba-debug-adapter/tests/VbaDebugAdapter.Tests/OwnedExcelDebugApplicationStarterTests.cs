using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class OwnedExcelDebugApplicationStarterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchRetainsBootstrapHandleEvidenceAcrossSuccessAndFailure(bool failLaunch)
    {
        using var directory = TempDirectory.Create();
        var bootstrapPath = Path.Combine(directory.Path, "proved-bootstrap.xlsx");
        var bootstrap = ExcelDebugBootstrapWorkbookFile.Create(bootstrapPath, archive =>
        {
            using var writer = new StreamWriter(archive.CreateEntry("fixture.xml").Open());
            writer.Write("<fixture />");
        }, File.Delete);
        var bootstrapEvidence = Assert.Single(bootstrap.CleanupOutcome.Evidence);
        var process = new FakeDebugOwnedProcess(134, DateTime.UnixEpoch);
        var job = new FakeDebugProcessJob(process);
        var primary = new IOException("Suspended launch failed.");
        var launcher = new WindowsExcelDebugOwnedProcessLauncher(
            () => Path.GetFullPath("excel.exe"), () => bootstrap, File.Delete,
            (_, _, _) => failLaunch ? throw primary : new(process, new PrimaryThread()));
        ExcelDebugOwnedProcessLaunch? launch = null;
        try
        {
            var failure = Record.Exception(() => launch = launcher.Start(
                new FakeDebugExcelProcessApi(process.Id, process, job), CancellationToken.None));

            var outcome = failLaunch
                ? Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome
                : Assert.IsType<DebugFailureOutcome>(launch!.LaunchCleanupOutcome);
            Assert.Contains(bootstrapEvidence, outcome.Evidence);
            if (failLaunch) { Assert.Same(primary, outcome.PrimaryFailure); }
        }
        finally
        {
            if (launch is not null)
            {
                launch.PrimaryThread.Dispose();
                await launch.ProcessOwner.DisposeAsync();
            }
        }
    }

    [Fact]
    public void CancelledLaunchWithProvedCleanupRetainsOrdinaryCancellationAndItsEvidence()
    {
        using var directory = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var bootstrap = Path.Combine(directory.Path, "cancelled-bootstrap.xlsx");
        var launcher = new WindowsExcelDebugOwnedProcessLauncher(
            () => Path.GetFullPath("excel.exe"), () =>
            {
                File.WriteAllText(bootstrap, "bootstrap fixture");
                cancellation.Cancel();
                return new(bootstrap, new DebugFailureCompletion().Complete());
            }, File.Delete, (_, _, _) => throw new InvalidOperationException("A cancelled launch must not start Excel."));
        var process = new FakeDebugOwnedProcess(133, DateTime.UnixEpoch);

        var failure = Record.Exception(() => launcher.Start(
            new FakeDebugExcelProcessApi(process.Id, process), cancellation.Token));

        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.IsAssignableFrom<OperationCanceledException>(outcome.PrimaryFailure);
        Assert.Contains(nameof(WindowsExcelDebugOwnedProcessLauncher.Start), outcome.PrimaryFailure!.StackTrace!);
        Assert.False(outcome.HasCleanupFailure);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
        Assert.False(File.Exists(bootstrap));
    }

    [Fact]
    public void BootstrapCreationRetainsItsWriteCauseAndDeletionFailureWithHandleEvidence()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "failed-bootstrap.xlsx");
        var writeFailure = new IOException("Bootstrap writing failed.");
        var deletionFailure = new IOException("Bootstrap deletion failed.");

        var failure = Record.Exception(() => ExcelDebugBootstrapWorkbookFile.Create(path,
            _ => throw writeFailure, _ => throw deletionFailure));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(writeFailure, outcome.PrimaryFailure);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, deletionFailure)
            && item.RetainedPath == path);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SuccessfulStartupRetainsEveryLocalReleaseBeforeApplicationTransfer()
    {
        using var directory = TempDirectory.Create();
        var bootstrap = Path.Combine(directory.Path, "bootstrap.xlsx");
        File.WriteAllText(bootstrap, "owned bootstrap fixture");
        var application = new BootstrapApplication(new(bootstrap, null));
        var process = new FakeDebugOwnedProcess(132, DateTime.UnixEpoch);
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        var nativeRelease = new DebugFailureCompletion();
        nativeRelease.AddEvidence(new("local COM release", "native Excel window", DebugResourceKind.Com,
            true, "The native binding scope proved its final RCW release.", process.Id));
        var launchRelease = new DebugFailureCompletion();
        launchRelease.AddEvidence(new("local launch release", "creation process handle", DebugResourceKind.Handle,
            true, "The suspended launcher proved its local process handle release.", process.Id));
        var starter = new OwnedExcelDebugApplicationStarter(
            new Launcher(new(owner, new PrimaryThread(), bootstrap)
                { LaunchCleanupOutcome = launchRelease.Complete() }),
            new ReturningBinder(application, nativeRelease.Complete()), _ => true);
        try
        {
            var started = starter.Start(new FakeDebugExcelProcessApi(process.Id, process, job), CancellationToken.None);

            Assert.Same(application, started.Application);
            var outcome = Assert.IsType<DebugFailureOutcome>(started.StartupCleanupOutcome);
            Assert.False(outcome.HasCleanupFailure);
            Assert.False(outcome.HasUnprovedRelease);
            Assert.Contains(outcome.Evidence, item => item.Resource == "native Excel window" && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Resource == "creation process handle" && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Resource == "Excel primary thread handle" && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Resource == "Excel bootstrap workbook" && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Resource == "bootstrap workbook collection" && item.Released);
            Assert.DoesNotContain(outcome.Evidence, item => item.Resource == "Excel application");
            Assert.False(File.Exists(bootstrap));
        }
        finally { await owner.DisposeAsync(); }
    }

    [Fact]
    public void NativeBindingFailureRetainsItsCauseAndEveryAcquiredComReleaseFailure()
    {
        var lookupFailure = new IOException("Application process lookup failed.");
        var applicationFailure = new IOException("Application release failed.");
        var nativeFailure = new IOException("Native object release failed.");
        var application = new object();
        var nativeObject = new NativeObject(application);
        var attempted = new List<object?>();
        var binder = new WindowsExcelDebugNativeObjectModelBinder(
            new NativeApi(nativeObject, lookupFailure), value =>
            {
                attempted.Add(value);
                if (ReferenceEquals(value, application)) { throw applicationFailure; }
                if (ReferenceEquals(value, nativeObject)) { throw nativeFailure; }
                return true;
            });

        var failure = Record.Exception(() => binder.BindApplication(131, () => false));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(lookupFailure, outcome.PrimaryFailure);
        Assert.Contains(nameof(NativeApi.GetApplicationProcessId), outcome.PrimaryFailure!.StackTrace!);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, applicationFailure));
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, nativeFailure));
        Assert.Equal(1, attempted.Count(value => ReferenceEquals(value, application)));
        Assert.Equal(1, attempted.Count(value => ReferenceEquals(value, nativeObject)));
        Assert.True(outcome.HasUnprovedRelease);
    }

    [Fact]
    public void BootstrapCloseFailureRetainsBothComReleaseFailures()
    {
        using var directory = TempDirectory.Create();
        var bootstrap = Path.Combine(directory.Path, "bootstrap.xlsx");
        File.WriteAllText(bootstrap, "owned bootstrap fixture");
        var closeFailure = new IOException("Bootstrap close failed.");
        var workbookFailure = new IOException("Workbook release failed.");
        var collectionFailure = new IOException("Workbook collection release failed.");
        var application = new BootstrapApplication(new(bootstrap, closeFailure));
        var process = new FakeDebugOwnedProcess(129, DateTime.UnixEpoch);
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        var attempted = new List<object?>();
        var starter = new OwnedExcelDebugApplicationStarter(
            new Launcher(new(owner, new PrimaryThread(), bootstrap)), new ReturningBinder(application), value =>
            {
                attempted.Add(value);
                if (ReferenceEquals(value, application.Workbooks.Workbook)) { throw workbookFailure; }
                if (ReferenceEquals(value, application.Workbooks)) { throw collectionFailure; }
                return true;
            });

        var failure = Record.Exception(() => starter.Start(
            new FakeDebugExcelProcessApi(process.Id, process, job), CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(closeFailure, outcome.PrimaryFailure);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, workbookFailure));
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, collectionFailure));
        Assert.Contains(application, attempted);
        Assert.NotNull(owner.CleanupOutcome);
        Assert.False(File.Exists(bootstrap));
    }

    [Fact]
    public void LauncherCreationFailureAttemptsEveryAcquiredResourceCleanup()
    {
        using var directory = TempDirectory.Create();
        var bootstrap = Path.Combine(directory.Path, "bootstrap.xlsx");
        File.WriteAllText(bootstrap, "owned bootstrap fixture");
        var creationFailure = new IOException("Atomic creation failed.");
        var jobFailure = new IOException("Job release failed.");
        var deletionFailure = new IOException("Bootstrap deletion failed.");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(128, DateTime.UnixEpoch);
        var job = new FakeDebugProcessJob(process, disposeAction: () =>
        {
            events.Add("job-release");
            throw jobFailure;
        });
        var launcher = new WindowsExcelDebugOwnedProcessLauncher(
            () => Path.GetFullPath("excel.exe"), () => new(bootstrap, new DebugFailureCompletion().Complete()),
            _ => { events.Add("bootstrap-delete"); throw deletionFailure; },
            (_, _, _) => { events.Add("start"); throw creationFailure; });

        var failure = Record.Exception(() => launcher.Start(
            new FakeDebugExcelProcessApi(process.Id, process, job), CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(creationFailure, outcome.PrimaryFailure);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, jobFailure)
            && item.Kind == DebugResourceKind.Handle);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, deletionFailure)
            && item.Kind == DebugResourceKind.FileSystem && item.RetainedPath == bootstrap);
        Assert.Equal(["start", "job-release", "bootstrap-delete"], events);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.False(outcome.OnlyFileDeletionFailed);
    }

    [Fact]
    public void BindingFailureRetainsItsCauseWhenPrimaryThreadReleaseAlsoFails()
    {
        using var directory = TempDirectory.Create();
        var bootstrap = Path.Combine(directory.Path, "bootstrap.xlsx");
        File.WriteAllText(bootstrap, "owned bootstrap fixture");
        var bindingFailure = new IOException("Application binding failed.");
        var threadFailure = new IOException("Primary thread release failed.");
        var process = new FakeDebugOwnedProcess(127, DateTime.UnixEpoch);
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        var thread = new PrimaryThread(threadFailure);
        var starter = new OwnedExcelDebugApplicationStarter(
            new Launcher(new(owner, thread, bootstrap)), new Binder(bindingFailure));

        var failure = Record.Exception(() => starter.Start(
            new FakeDebugExcelProcessApi(process.Id, process, job), CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(bindingFailure, outcome.PrimaryFailure);
        Assert.Contains(nameof(Binder.BindApplication), outcome.PrimaryFailure!.StackTrace!);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, threadFailure)
            && item.Kind == DebugResourceKind.Handle);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.NotNull(owner.CleanupOutcome);
        Assert.False(File.Exists(bootstrap));
        Assert.Equal(1, thread.DisposeCalls);
        Assert.Equal(1, thread.ResumeCalls);
    }

    private sealed class Launcher(ExcelDebugOwnedProcessLaunch launch) : IExcelDebugOwnedProcessLauncher
    {
        public ExcelDebugOwnedProcessLaunch Start(IDebugExcelProcessApi processApi, CancellationToken cancellationToken)
            => launch;
    }

    private sealed class Binder(Exception failure) : IExcelDebugNativeObjectModelBinder
    {
        public ExcelDebugNativeApplication BindApplication(int processId, Func<bool> hasProcessExited) => throw failure;
    }

    private sealed class ReturningBinder(object application, DebugFailureOutcome? outcome = null) : IExcelDebugNativeObjectModelBinder
    {
        public ExcelDebugNativeApplication BindApplication(int processId, Func<bool> hasProcessExited)
            => new(application, outcome ?? new DebugFailureCompletion().Complete());
    }

    public sealed class NativeObject(object application)
    {
        public object Application => application;
    }

    private sealed class NativeApi(object nativeObject, Exception lookupFailure) : IExcelDebugNativeObjectModelApi
    {
        public IReadOnlyList<nint> FindTopLevelWindows(int processId) => [new nint(1)];
        public nint FindNativeObjectWindow(nint parentWindow) => new(2);
        public void BindNativeObject(nint windowHandle, out object? value) => value = nativeObject;
        public int GetApplicationProcessId(object application) => throw lookupFailure;
    }

    public sealed class BootstrapApplication(BootstrapWorkbook workbook)
    {
        public BootstrapWorkbooks Workbooks { get; } = new(workbook);
    }

    public sealed class BootstrapWorkbooks(BootstrapWorkbook workbook)
    {
        public BootstrapWorkbook Workbook => workbook;
        public int Count => 1;
        public BootstrapWorkbook Item(int index) => workbook;
    }

    public sealed class BootstrapWorkbook(string path, Exception? closeFailure)
    {
        public string FullName => path;
        public void Close(bool save)
        {
            if (closeFailure is not null) { throw closeFailure; }
        }
    }

    private sealed class PrimaryThread(Exception? releaseFailure = null) : IDebugSuspendedPrimaryThread
    {
        public int ResumeCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool HandleReleaseVerified { get; private set; }
        public void ResumeExactlyOnce() => ResumeCalls++;
        public void Dispose()
        {
            DisposeCalls++;
            if (releaseFailure is not null) { throw releaseFailure; }
            HandleReleaseVerified = true;
        }
    }
}
