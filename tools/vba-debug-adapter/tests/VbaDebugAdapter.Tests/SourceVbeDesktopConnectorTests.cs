using VbaDebugAdapter.Infrastructure;
using VbaDebugAdapter.Debugging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class SourceVbeDesktopConnectorTests
{
    [Fact]
    public void BorrowSelectsTheExactPhysicalWorkbookAcrossExcelProcesses()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = Path.Combine(temp.Path, "source");
        var otherDirectory = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(otherDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "Same.xlsm");
        var otherPath = Path.Combine(otherDirectory, "Same.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        File.WriteAllText(otherPath, "other fixture");
        var otherWorkbook = new FakeWorkbook(otherPath);
        var sourceWorkbook = new FakeWorkbook(sourcePath);
        var otherProcess = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var sourceProcess = new SourceVbeProcessIdentity(12002, 638000000000000002);
        var otherApplication = new FakeApplication(otherWorkbook);
        var sourceApplication = new FakeApplication(sourceWorkbook);
        var api = new FakeDesktopApi(
            (otherProcess, otherApplication),
            (sourceProcess, sourceApplication));
        var identities = new FakeIdentityReader(
            (sourcePath, new SourceVbePhysicalFileIdentity(42, 100)),
            (otherPath, new SourceVbePhysicalFileIdentity(42, 200)));
        var connector = new WindowsSourceVbeDesktopConnector(api, identities);

        var binding = connector.AttachOrOpen(sourcePath);

        Assert.Same(sourceWorkbook, binding.Workbook);
        Assert.Same(sourceApplication, binding.Application);
        Assert.Equal(sourceProcess.ProcessId, binding.ProcessId);
        Assert.Equal(sourceProcess.StartUtcTicks, binding.ProcessStartUtcTicks);
        Assert.True(binding.WasAlreadyOpen);
        Assert.True(binding.IsExactWorkbook());
        Assert.Equal(0, api.CreatedApplications);
        Assert.Equal(0, sourceApplication.Workbooks.OpenCount);
        Assert.Equal(0, otherApplication.Workbooks.OpenCount);
        binding.Release();
        Assert.Equal(0, sourceApplication.QuitCount);
        Assert.Equal(0, otherApplication.QuitCount);
    }

    [Fact]
    public void ClosedSourceDoesNotReuseAHiddenExcelApplication()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var hiddenApplication = new FakeApplication { Visible = false };
        var createdApplication = new FakeApplication { Visible = false };
        var hiddenProcess = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((hiddenProcess, hiddenApplication))
        {
            CreatedApplication = createdApplication
        };
        var identities = new FakeIdentityReader(
            (sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var connector = new WindowsSourceVbeDesktopConnector(api, identities);

        var binding = connector.AttachOrOpen(sourcePath);

        Assert.Same(createdApplication, binding.Application);
        Assert.False(binding.WasAlreadyOpen);
        Assert.False(hiddenApplication.Visible);
        Assert.True(createdApplication.Visible);
        Assert.Equal(0, hiddenApplication.Workbooks.OpenCount);
        Assert.Equal(1, createdApplication.Workbooks.OpenCount);
        binding.Release();
        Assert.True(binding.ReleaseVerified);
        Assert.Equal(0, hiddenApplication.QuitCount);
        Assert.Equal(0, createdApplication.QuitCount);
    }

    [Fact]
    public void MonitorReleasesEachAcquiredWorkbookReferenceEvenWhenItMatchesBinding()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader(
            (sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var released = new List<object?>();
        var connector = new WindowsSourceVbeDesktopConnector(api, identities, value =>
        {
            if (value is not null) released.Add(value);
            return true;
        });
        var binding = connector.AttachOrOpen(sourcePath);
        released.Clear();

        Assert.Null(binding.ProbeCompletion());

        Assert.Single(released, value => ReferenceEquals(value, workbook));
        Assert.Single(released, value => ReferenceEquals(value, application.Workbooks));
        Assert.DoesNotContain(released, value => ReferenceEquals(value, application));
        binding.Release();
        binding.Release();
        Assert.Equal(2, released.Count(value => ReferenceEquals(value, workbook)));
        Assert.Single(released, value => ReferenceEquals(value, application));
        Assert.True(binding.ReleaseVerified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DuplicatePhysicalSourceRefusesAttachmentAndPreservesReleasedReferenceEvidence(int releaseFailureMode)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        var aliasPath = Path.Combine(temp.Path, "Alias.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        File.WriteAllText(aliasPath, "alias fixture");
        var sourceWorkbook = new FakeWorkbook(sourcePath);
        var aliasWorkbook = new FakeWorkbook(aliasPath);
        var sourceApplication = new FakeApplication(sourceWorkbook);
        var aliasApplication = new FakeApplication(aliasWorkbook);
        var sourceProcess = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var aliasProcess = new SourceVbeProcessIdentity(12002, 638000000000000002);
        var api = new FakeDesktopApi((sourceProcess, sourceApplication), (aliasProcess, aliasApplication));
        var physicalIdentity = new SourceVbePhysicalFileIdentity(42, 100);
        var identities = new FakeIdentityReader((sourcePath, physicalIdentity), (aliasPath, physicalIdentity));
        var released = new List<object>();
        var releaseFailure = new InvalidOperationException("candidate reference release failed");
        var connector = new WindowsSourceVbeDesktopConnector(api, identities, value =>
        {
            if (value is null) return true;
            released.Add(value);
            if (!ReferenceEquals(value, aliasWorkbook)) return true;
            if (releaseFailureMode == 2) throw releaseFailure;
            return releaseFailureMode != 1;
        });

        var failure = Assert.ThrowsAny<Exception>(() => connector.AttachOrOpen(sourcePath));

        var evidence = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        var primary = Assert.IsType<DebugSetupException>(evidence.PrimaryFailure);
        Assert.Contains("open more than once", primary.Message, StringComparison.Ordinal);
        Assert.Equal(releaseFailureMode != 0, evidence.HasCleanupFailure);
        Assert.Equal(releaseFailureMode != 0, evidence.HasUnprovedRelease);
        if (releaseFailureMode == 2)
            Assert.Contains(evidence.CleanupFailures, item => ReferenceEquals(item.Exception, releaseFailure));
        foreach (var acquired in new object[]
        {
            sourceWorkbook, aliasWorkbook, sourceApplication.Workbooks, aliasApplication.Workbooks,
            sourceApplication, aliasApplication
        })
            Assert.Single(released, value => ReferenceEquals(value, acquired));
        Assert.Contains(evidence.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released
            && item.Reason.Contains("lifetime ownership", StringComparison.Ordinal));
        Assert.Contains(evidence.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
        Assert.Equal(0, api.CreatedApplications);
        Assert.Equal(0, sourceApplication.Workbooks.OpenCount);
        Assert.Equal(0, aliasApplication.Workbooks.OpenCount);
        Assert.Equal(0, sourceApplication.QuitCount);
        Assert.Equal(0, aliasApplication.QuitCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ClosedSourceAlwaysCreatesANewVisibleUserControlledSession(int existingApplicationCount)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var existing = Enumerable.Range(1, existingApplicationCount)
            .Select(index => (new SourceVbeProcessIdentity(12000 + index, 638000000000000000 + index),
                new FakeApplication())).ToArray();
        var created = new FakeApplication { Visible = false, UserControl = false };
        created.Workbooks.BeforeOpen = () =>
        {
            Assert.True(created.Visible);
            Assert.True(created.UserControl);
        };
        var api = new FakeDesktopApi(existing) { CreatedApplication = created };
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));

        var binding = new WindowsSourceVbeDesktopConnector(api, identities).AttachOrOpen(sourcePath);

        Assert.Same(created, binding.Application);
        Assert.False(binding.WasAlreadyOpen);
        Assert.Equal(1, api.CreatedApplications);
        Assert.Equal(1, created.Workbooks.OpenCount);
        Assert.True(created.Visible);
        Assert.True(created.UserControl);
        foreach (var (_, application) in existing)
        {
            Assert.Equal(0, application.Workbooks.OpenCount);
            Assert.True(application.Visible);
            Assert.False(application.UserControl);
            Assert.Equal(0, application.QuitCount);
        }
        binding.Release();
        Assert.True(created.Visible);
        Assert.True(created.UserControl);
        Assert.Equal(0, created.QuitCount);
    }

    [Fact]
    public void UnprovedUserControlHandoffRetainsApplicationReferenceAndReportsReleaseUnproved()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var handoffFailure = new InvalidOperationException("UserControl handoff failed");
        var created = new FakeApplication { Visible = false, UserControlWriteFailure = handoffFailure };
        var api = new FakeDesktopApi { CreatedApplication = created };
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var released = new List<object?>();
        var connector = new WindowsSourceVbeDesktopConnector(api, identities, value =>
        {
            released.Add(value);
            return true;
        });

        var failure = Assert.ThrowsAny<Exception>(() => connector.AttachOrOpen(sourcePath));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(handoffFailure, outcome.PrimaryFailure);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Com && !item.Released
            && item.Reason.Contains("user control", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(released, value => ReferenceEquals(value, created));
        Assert.Equal(0, created.Workbooks.OpenCount);
        Assert.Equal(0, created.QuitCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SourceOpenSuppressesEventsWithLowSecurityAndRestoresExactSettings(bool originalEvents, bool openFails)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var created = new FakeApplication
        {
            Visible = false, EnableEvents = originalEvents, AutomationSecurity = 2
        };
        var openFailure = new InvalidOperationException("source Open failed");
        bool? eventsAtOpen = null;
        int? securityAtOpen = null;
        created.Workbooks.BeforeOpen = () =>
        {
            eventsAtOpen = created.EnableEvents;
            securityAtOpen = created.AutomationSecurity;
            if (openFails) throw openFailure;
        };
        var api = new FakeDesktopApi { CreatedApplication = created };
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var connector = new WindowsSourceVbeDesktopConnector(api, identities);
        SourceVbeDesktopBinding? binding = null;
        if (openFails)
        {
            var failure = Assert.ThrowsAny<Exception>(() => connector.AttachOrOpen(sourcePath));
            Assert.Same(openFailure, Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome.PrimaryFailure);
        }
        else
        {
            binding = connector.AttachOrOpen(sourcePath);
        }

        Assert.False(eventsAtOpen);
        Assert.Equal(1, securityAtOpen);
        Assert.Equal(originalEvents, created.EnableEvents);
        Assert.Equal(2, created.AutomationSecurity);
        Assert.True(created.Visible);
        Assert.True(created.UserControl);
        Assert.Equal(0, created.QuitCount);
        binding?.Release();
    }

    [Fact]
    public void ProcessExitDuringComIdentityReadIsConfirmedWithoutFabricatingWorkbookClosure()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var binding = new WindowsSourceVbeDesktopConnector(api, identities).AttachOrOpen(sourcePath);
        var processProbes = 0;
        api.CompletionOverride = observed =>
        {
            Assert.Equal(process, observed);
            return ++processProbes == 1 ? null
                : new SourceVbeDebugSessionCompletion(SourceVbeDebugSessionEndReason.ProcessExited, null);
        };
        api.IdentityReadFailure = new COMException("The bound Excel exited during the COM read.", unchecked((int)0x80010108));

        var completion = binding.ProbeCompletion();

        Assert.NotNull(completion);
        Assert.Equal(SourceVbeDebugSessionEndReason.ProcessExited, completion.Reason);
        Assert.Null(completion.ProcessExitCode);
        Assert.Equal(2, processProbes);
        Assert.Equal(0, application.Workbooks.OpenCount);
        Assert.Equal(0, application.QuitCount);
        binding.Release();
        Assert.True(binding.ReleaseVerified);
    }

    [Fact]
    public void ProcessExitDuringWorkbookInventoryReleasesItsAcquiredReferencesBeforeConfirmation()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var released = new List<object?>();
        var binding = new WindowsSourceVbeDesktopConnector(api, identities, value =>
        {
            released.Add(value);
            return true;
        }).AttachOrOpen(sourcePath);
        released.Clear();
        var processProbes = 0;
        api.CompletionOverride = _ => ++processProbes == 1 ? null
            : new SourceVbeDebugSessionCompletion(SourceVbeDebugSessionEndReason.ProcessExited, null);
        application.Workbooks.BeforeCount = () => throw new COMException("Excel exited during workbook inventory.");

        var completion = binding.ProbeCompletion();

        Assert.Equal(SourceVbeDebugSessionEndReason.ProcessExited, completion!.Reason);
        Assert.Equal(2, processProbes);
        Assert.Single(released, value => ReferenceEquals(value, application.Workbooks));
        Assert.DoesNotContain(released, value => ReferenceEquals(value, workbook));
        Assert.Equal(0, application.QuitCount);
        binding.Release();
    }

    [Fact]
    public void ConfirmedProcessExitDoesNotHideUnprovedWorkbookInventoryReferenceRelease()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var rejectRelease = false;
        var binding = new WindowsSourceVbeDesktopConnector(api, identities,
            value => !rejectRelease || !ReferenceEquals(value, application.Workbooks)).AttachOrOpen(sourcePath);
        rejectRelease = true;
        var processProbes = 0;
        api.CompletionOverride = _ => ++processProbes == 1 ? null
            : new SourceVbeDebugSessionCompletion(SourceVbeDebugSessionEndReason.ProcessExited, null);
        var readFailure = new COMException("Excel exited during workbook inventory.");
        application.Workbooks.BeforeCount = () => throw readFailure;

        var failure = Assert.ThrowsAny<Exception>(() => binding.ProbeCompletion());

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(readFailure, outcome.PrimaryFailure);
        Assert.True(outcome.HasUnprovedRelease);
        Assert.Equal(1, processProbes);
        Assert.Equal(0, application.QuitCount);
        rejectRelease = false;
        binding.Release();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfirmedProcessExitPreservesTheOriginalObservationFailure(bool confirmationFails)
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var binding = new WindowsSourceVbeDesktopConnector(api, identities).AttachOrOpen(sourcePath);
        var readFailure = new COMException("The source binding is unavailable, but exit is unproved.");
        api.IdentityReadFailure = readFailure;
        var confirmationFailure = new Win32Exception("Exact process exit could not be observed.");
        var processProbes = 0;
        api.CompletionOverride = _ => ++processProbes == 1 || !confirmationFails ? null : throw confirmationFailure;

        var failure = Assert.ThrowsAny<Exception>(() => binding.ProbeCompletion());

        if (confirmationFails)
        {
            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.Same(readFailure, outcome.PrimaryFailure);
            Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, confirmationFailure));
        }
        else Assert.Same(readFailure, failure);
        Assert.Equal(2, processProbes);
        Assert.Equal(0, application.QuitCount);
        binding.Release();
    }

    [Fact]
    public void ProcessIdentityMismatchFaultsObservationInsteadOfClaimingSuccessfulDetach()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var workbook = new FakeWorkbook(sourcePath);
        var application = new FakeApplication(workbook);
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, application));
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var binding = new WindowsSourceVbeDesktopConnector(api, identities).AttachOrOpen(sourcePath);
        api.ProcessIdentityOverride = process with { StartUtcTicks = process.StartUtcTicks + 1 };

        var failure = Assert.Throws<DebugSetupException>(() => binding.ProbeCompletion());

        Assert.Contains("identity", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, application.Workbooks.OpenCount);
        Assert.Equal(0, application.QuitCount);
        binding.Release();
    }

    [Fact]
    public void UnreadableRootedSavedWorkbookPreventsAssumingTheSourceIsClosed()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        var unavailableSavedPath = Path.Combine(temp.Path, "Unavailable.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var unreadableWorkbook = new FakeWorkbook(unavailableSavedPath);
        var existingApplication = new FakeApplication(unreadableWorkbook);
        var createdApplication = new FakeApplication { Visible = false };
        var process = new SourceVbeProcessIdentity(12001, 638000000000000001);
        var api = new FakeDesktopApi((process, existingApplication)) { CreatedApplication = createdApplication };
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));
        var released = new List<object?>();
        var connector = new WindowsSourceVbeDesktopConnector(api, identities, value =>
        {
            if (value is not null) released.Add(value);
            return true;
        });

        var failure = Assert.ThrowsAny<Exception>(() => connector.AttachOrOpen(sourcePath));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.IsType<KeyNotFoundException>(outcome.PrimaryFailure);
        Assert.False(outcome.HasUnprovedRelease);
        foreach (var acquired in new object[] { unreadableWorkbook, existingApplication.Workbooks, existingApplication })
            Assert.Single(released, value => ReferenceEquals(value, acquired));
        Assert.Equal(0, api.CreatedApplications);
        Assert.Equal(0, existingApplication.Workbooks.OpenCount);
        Assert.Equal(0, createdApplication.Workbooks.OpenCount);
        Assert.Equal(0, existingApplication.QuitCount);
    }

    [Fact]
    public void CallerDesktopEnumerationFailureIsNotEvidenceOfAnEmptySourceInventory()
    {
        using var temp = TempDirectory.Create();
        var sourcePath = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(sourcePath, "source fixture");
        var windows = new WindowsSourceVbeCallerDesktopWindows(_ => false, _ =>
            throw new InvalidOperationException("Failed enumeration supplied no window to inspect."));
        var createdApplication = new FakeApplication { Visible = false };
        var api = new FakeDesktopApi
        {
            CreatedApplication = createdApplication,
            CaptureOverride = () =>
            {
                windows.Find(0);
                return [];
            }
        };
        var identities = new FakeIdentityReader((sourcePath, new SourceVbePhysicalFileIdentity(42, 100)));

        var failure = Assert.ThrowsAny<Exception>(() =>
            new WindowsSourceVbeDesktopConnector(api, identities).AttachOrOpen(sourcePath));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        var primary = Assert.IsType<Win32Exception>(outcome.PrimaryFailure);
        Assert.Contains("caller desktop", primary.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Equal(0, api.CreatedApplications);
        Assert.Equal(0, createdApplication.Workbooks.OpenCount);
    }

    private sealed class FakeDesktopApi : ISourceVbeDesktopApi
    {
        private readonly (SourceVbeProcessIdentity Process, FakeApplication Application)[] applications;

        public FakeDesktopApi(params (SourceVbeProcessIdentity Process, FakeApplication Application)[] applications)
            => this.applications = applications;

        public int CreatedApplications { get; private set; }
        public FakeApplication? CreatedApplication { get; init; }
        public SourceVbeProcessIdentity? ProcessIdentityOverride { get; set; }
        public Exception? IdentityReadFailure { get; set; }
        public Func<SourceVbeProcessIdentity, SourceVbeDebugSessionCompletion?>? CompletionOverride { get; set; }
        public Func<IReadOnlyList<SourceVbeProcessIdentity>>? CaptureOverride { get; init; }
        private static readonly SourceVbeProcessIdentity CreatedProcess = new(12003, 638000000000000003);

        public IReadOnlyList<SourceVbeProcessIdentity> CaptureExcelProcesses()
            => CaptureOverride?.Invoke() ?? applications.Select(item => item.Process).ToArray();

        public object? TryBindApplication(SourceVbeProcessIdentity process)
            => applications.SingleOrDefault(item => item.Process == process).Application;

        public object CreateApplication()
        {
            CreatedApplications++;
            return CreatedApplication
                ?? throw new InvalidOperationException("A borrowed source workbook must not start Excel.");
        }

        public SourceVbeProcessIdentity ReadProcessIdentity(object application)
        {
            if (IdentityReadFailure is { } failure) throw failure;
            return ProcessIdentityOverride ?? (ReferenceEquals(application, CreatedApplication) ? CreatedProcess
                : applications.Single(item => ReferenceEquals(item.Application, application)).Process);
        }

        public SourceVbeDebugSessionCompletion? ReadProcessCompletion(SourceVbeProcessIdentity process)
            => CompletionOverride?.Invoke(process);
    }

    private sealed class FakeIdentityReader : ISourceVbePhysicalIdentityReader
    {
        private readonly Dictionary<string, SourceVbePhysicalFileIdentity> identities;

        public FakeIdentityReader(params (string Path, SourceVbePhysicalFileIdentity Identity)[] entries)
            => identities = entries.ToDictionary(item => item.Path, item => item.Identity,
                StringComparer.OrdinalIgnoreCase);

        public SourceVbePhysicalFileIdentity Read(string path) => identities[Path.GetFullPath(path)];
    }

    public sealed class FakeApplication(params FakeWorkbook[] workbooks)
    {
        public FakeWorkbooks Workbooks { get; } = new(workbooks);
        public bool Visible { get; set; } = true;
        private bool userControl;
        public Exception? UserControlWriteFailure { get; init; }
        public bool UserControl
        {
            get => userControl;
            set
            {
                if (UserControlWriteFailure is { } failure) throw failure;
                userControl = value;
            }
        }
        public bool EnableEvents { get; set; } = true;
        public int AutomationSecurity { get; set; } = 1;
        public int QuitCount { get; private set; }
        public void Quit() => QuitCount++;
    }

    public sealed class FakeWorkbooks(params FakeWorkbook[] workbooks)
    {
        private readonly List<FakeWorkbook> items = [.. workbooks];
        public Action? BeforeCount { get; set; }
        public int Count
        {
            get { BeforeCount?.Invoke(); return items.Count; }
        }
        public int OpenCount { get; private set; }
        public Action? BeforeOpen { get; set; }
        public FakeWorkbook Item(int index) => items[index - 1];
        public FakeWorkbook Open(string path)
        {
            OpenCount++;
            BeforeOpen?.Invoke();
            var workbook = new FakeWorkbook(path);
            items.Add(workbook);
            return workbook;
        }
    }

    public sealed class FakeWorkbook(string fullName)
    {
        public string FullName { get; } = fullName;
    }
}
