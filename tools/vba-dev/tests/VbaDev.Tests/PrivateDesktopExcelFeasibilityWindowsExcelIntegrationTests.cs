using System.Diagnostics;
using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;
using Xunit.Abstractions;

namespace VbaDev.Tests;

[Collection(PrivateDesktopExcelFeasibilityCollection.Name)]
public sealed class PrivateDesktopExcelFeasibilityWindowsExcelIntegrationTests(
    ITestOutputHelper output)
{
    [Fact]
    public async Task MacroEntryObservationRetriesAWriterLockAndRequiresExactContent()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-marker-observer-{Guid.NewGuid():N}.txt");
        const string expected = "exact-entry-identity";
        FileStream? writer = null;
        try
        {
            writer = new FileStream(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            var observation = WaitForMacroEnteredAsync(
                markerPath,
                expected,
                TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(expected));
            await writer.FlushAsync();
            writer.Dispose();
            writer = null;

            await observation;
        }
        finally
        {
            writer?.Dispose();
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task MacroEntryObservationRejectsPersistentWrongContent()
    {
        var markerPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-marker-observer-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(markerPath, "wrong-entry-identity");

            await Assert.ThrowsAsync<TimeoutException>(() =>
                WaitForMacroEnteredAsync(
                    markerPath,
                    "exact-entry-identity",
                    TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [PrivateDesktopExcelFeasibilityFact]
    [Trait("Category", PrivateDesktopExcelFeasibilityFactAttribute.Category)]
    public async Task PrivateDesktopSupportsExactPidNativeObjectModelBinding()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialProofArtifacts = CaptureProofArtifacts();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease privateDesktop = null!;
        DesktopWindowObservationScope privateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        var workbookPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-proof-{Guid.NewGuid():N}.xlsm");
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        try
        {
            privateDesktop = WindowsPrivateDesktopLease.Create();
            privateScope = new DesktopWindowObservationScope(
                privateDesktop.Handle,
                privateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(privateDesktop.QualifiedName, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                privateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);

            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            var binding = dispatcher.InvokeAsync(
                () =>
                {
                    launch.PrimaryThread.ResumeExactlyOnce();
                    application = new WindowsExcelNativeObjectModelBinder()
                        .BindApplicationOnDesktop(
                        owner.ProcessId,
                        privateDesktop.Handle,
                        () => owner.HasExited);
                    dynamic excel = application;
                    excel.Visible = false;
                    excel.DisplayAlerts = false;
                    workbooks = excel.Workbooks;
                    CloseBootstrapWorkbook(workbooks, bootstrapPath);
                    ExcelBootstrapWorkbookFile.Delete(bootstrapPath);
                    return CaptureNativeBindingEvidence(application, owner);
                },
                CancellationToken.None);
            var bindingResult = await AwaitBoundedBindingAsync(binding, owner);
            Assert.False(string.IsNullOrWhiteSpace(Environment.OSVersion.VersionString));
            Assert.False(string.IsNullOrWhiteSpace(bindingResult.ExcelVersion));
            Assert.False(string.IsNullOrWhiteSpace(bindingResult.ExcelFileVersion));
            Assert.False(string.IsNullOrWhiteSpace(bindingResult.ExcelProductVersion));
            Assert.Contains(
                bindingResult.ProcessArchitecture,
                new[] { "X86", "X64", "Arm64" });

            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            var evidenceBeforeExit = observer.Evidence;
            Assert.False(evidenceBeforeExit.HasCallerDesktopExposure);
            Assert.Contains(evidenceBeforeExit.Observations, observation =>
                observation.Location == DesktopWindowLocation.Private &&
                observation.ProcessId == owner.ProcessId &&
                observation.WindowHandle == bindingResult.ApplicationWindow);

            var workflow = await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () => ExerciseRepresentativeAutomation(
                        application,
                        workbooks,
                        owner,
                        observer,
                        workbookPath),
                    CancellationToken.None),
                owner,
                "representative workbook, VBE, reference, and test execution",
                TimeSpan.FromMinutes(2));
            Assert.Equal("UserForm", workflow.IntrinsicEventSourceName);
            Assert.Contains("Initialize", workflow.EventNames);
            Assert.Contains("QueryClose", workflow.EventNames);
            Assert.Equal(
                Guid.Parse("420b2830-e718-11cf-893d-00a0c9054228"),
                Guid.Parse(workflow.ReferenceGuid));
            Assert.Equal("private-desktop-executed", workflow.WorkbookOwnedEvidence);
            Assert.True(workflow.ModulePersistedAfterReopen);
            Assert.Equal([3, 1, 3], workflow.AutomationSecurityTransitions);
            Assert.True(File.Exists(workbookPath));
            File.Delete(workbookPath);

            observer.Capture(DesktopWindowLifecyclePhase.Shutdown);
            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        dynamic excel = application!;
                        excel.Quit();
                        ComObjectReleaser.Release(workbooks);
                        workbooks = null;
                        ComObjectReleaser.Release(application);
                        application = null;
                        ComObjectReleaser.CollectReleasedComObjects();
                        return true;
                    },
                    CancellationToken.None),
                owner,
                "successful Excel shutdown",
                TimeSpan.FromSeconds(20));
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            await AwaitJobEmptyAsync(owner, TimeSpan.FromSeconds(5));
            var finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None);
            observer = null;

            Assert.False(finalEvidence.HasCallerDesktopExposure);
            Assert.All(finalEvidence.Observations, observation =>
                Assert.Equal(owner.ProcessId, observation.ProcessId));
            output.WriteLine(
                "Supported native bind: Windows={0}; Excel={1}; fileVersion={2}; " +
                "productVersion={3}; processArchitecture={4}; PID={5}; caller={6}; " +
                "private={7}; observations={8}.",
                Environment.OSVersion.VersionString,
                bindingResult.ExcelVersion,
                bindingResult.ExcelFileVersion,
                bindingResult.ExcelProductVersion,
                bindingResult.ProcessArchitecture,
                owner.ProcessId,
                callerDesktop.QualifiedName,
                privateDesktop.QualifiedName,
                finalEvidence.Observations.Count);
            output.WriteLine(
                "Representative automation: events={0}; reference={1}; " +
                "modulePersisted={2}; macroEvidence={3}; security={4}.",
                workflow.EventNames.Count,
                workflow.ReferenceGuid,
                workflow.ModulePersistedAfterReopen,
                workflow.WorkbookOwnedEvidence,
                string.Join("->", workflow.AutomationSecurityTransitions));
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "success-path exact process-tree termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                privateDesktop,
                privateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "success-path bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            AttemptCleanup(
                cleanupFailures,
                "success-path proof artifact deletion",
                () => File.Delete(workbookPath));
            AttemptCleanup(
                cleanupFailures,
                "success-path exact artifact absence verification",
                () =>
                {
                    Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath));
                    Assert.False(File.Exists(workbookPath));
                });
            ThrowIfCleanupFailed(
                cleanupFailures,
                "The success path did not complete every cleanup boundary.");
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.True(initialProofArtifacts.SetEquals(CaptureProofArtifacts()));
    }

    [PrivateDesktopExcelFeasibilityFact]
    [Trait("Category", PrivateDesktopExcelFeasibilityFactAttribute.Category)]
    public async Task PrivateDesktopDetectsAndBoundsInteractiveUiWithoutCallerExposure()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialProofArtifacts = CaptureProofArtifacts();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease privateDesktop = null!;
        DesktopWindowObservationScope privateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        var workbookPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-proof-{Guid.NewGuid():N}.xlsm");
        var uiTitle = $"vba-dev private UI {Guid.NewGuid():N}";
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        object? workbook = null;
        Task<bool>? macro = null;
        var cleanup = new Stopwatch();
        string? actionableFailure = null;
        DesktopWindowExposureEvidence? finalEvidence = null;
        try
        {
            privateDesktop = WindowsPrivateDesktopLease.Create();
            privateScope = new DesktopWindowObservationScope(
                privateDesktop.Handle,
                privateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(privateDesktop.QualifiedName, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                privateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);
            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            _ = await AwaitBoundedBindingAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        launch.PrimaryThread.ResumeExactlyOnce();
                        application = new WindowsExcelNativeObjectModelBinder()
                            .BindApplicationOnDesktop(
                                owner.ProcessId,
                                privateDesktop.Handle,
                                () => owner.HasExited);
                        dynamic excel = application;
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        excel.AutomationSecurity = 3;
                        workbooks = excel.Workbooks;
                        CloseBootstrapWorkbook(workbooks, bootstrapPath);
                        ExcelBootstrapWorkbookFile.Delete(bootstrapPath);
                        return new NativeBindingEvidence(
                            new nint(Convert.ToInt64(excel.Hwnd)),
                            Convert.ToString(excel.Version) ?? string.Empty);
                    },
                    CancellationToken.None),
                owner);
            observer.Capture(DesktopWindowLifecyclePhase.VbeAutomation);
            var workbookName = await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        workbook = CreateBlockingUiWorkbook(
                            application,
                            workbooks,
                            workbookPath,
                            uiTitle,
                            observer);
                        dynamic openedWorkbook = workbook;
                        return Convert.ToString(openedWorkbook.Name) ?? string.Empty;
                    },
                    CancellationToken.None),
                owner,
                "interactive-UI probe preparation",
                TimeSpan.FromSeconds(30));

            observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
            macro = dispatcher.InvokeAsync(
                () =>
                {
                    dynamic excel = application!;
                    try
                    {
                        excel.Run($"'{workbookName}'!ShowBlockingUi");
                        return true;
                    }
                    finally
                    {
                        if (!owner.HasExited)
                        {
                            excel.AutomationSecurity = 3;
                        }
                    }
                },
                CancellationToken.None);

            var detectionDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            DesktopWindowObservation? detectedUi = null;
            while (DateTime.UtcNow < detectionDeadline && !macro.IsCompleted)
            {
                observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
                detectedUi = observer.Evidence.Observations.LastOrDefault(observation =>
                    observation.ProcessId == owner.ProcessId &&
                    observation.Location == DesktopWindowLocation.Private &&
                    observation.IsVisible &&
                    observation.Title.Equals(uiTitle, StringComparison.Ordinal));
                if (detectedUi is not null)
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.NotNull(detectedUi);
            Assert.False(observer.Evidence.HasCallerDesktopExposure);
            Assert.Equal(DesktopWindowLifecyclePhase.TestExecution, detectedUi.LifecyclePhase);
            actionableFailure =
                $"Interactive UI blocked private-desktop automation: " +
                $"PID={detectedUi.ProcessId}; HWND=0x{detectedUi.WindowHandle.ToInt64():X}; " +
                $"desktop={detectedUi.Desktop}; class={detectedUi.WindowClass}; " +
                $"title={detectedUi.Title}; phase={detectedUi.LifecyclePhase}.";

            cleanup.Start();
            await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(5));
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await AwaitJobEmptyAsync(owner, TimeSpan.FromSeconds(5));
            try
            {
                _ = await macro.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is not Xunit.Sdk.XunitException)
            {
                // Terminating the exact Job interrupts the blocked COM call.
            }

            finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None);
            observer = null;
            Assert.False(finalEvidence.HasCallerDesktopExposure);
            Assert.Contains($"PID={owner.ProcessId}", actionableFailure, StringComparison.Ordinal);
            Assert.Contains($"title={uiTitle}", actionableFailure, StringComparison.Ordinal);
            Assert.Contains("phase=TestExecution", actionableFailure, StringComparison.Ordinal);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "interactive-UI exact process-tree termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            if (macro is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "interactive-UI COM operation unwind",
                    () => AwaitCompletedTaskAsync(macro, TimeSpan.FromSeconds(5)));
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                privateDesktop,
                privateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "interactive-UI bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            AttemptCleanup(
                cleanupFailures,
                "interactive-UI proof artifact deletion",
                () => File.Delete(workbookPath));
            AttemptCleanup(
                cleanupFailures,
                "interactive-UI exact artifact absence verification",
                () =>
                {
                    Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath));
                    Assert.False(File.Exists(workbookPath));
                });
            cleanup.Stop();
            ThrowIfCleanupFailed(
                cleanupFailures,
                "The interactive-UI path did not complete every cleanup boundary.");
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.True(initialProofArtifacts.SetEquals(CaptureProofArtifacts()));
        Assert.True(
            cleanup.Elapsed < TimeSpan.FromSeconds(10),
            $"Interactive UI cleanup exceeded its end-to-end bound: {cleanup.Elapsed}.");
        Assert.NotNull(actionableFailure);
        Assert.NotNull(finalEvidence);
        output.WriteLine(actionableFailure);
        output.WriteLine(
            "Bounded UI cleanup: {0} ms; caller exposure={1}; observations={2}.",
            cleanup.ElapsedMilliseconds,
            finalEvidence.HasCallerDesktopExposure,
            finalEvidence.Observations.Count);
    }

    [PrivateDesktopExcelFeasibilityFact]
    [Trait("Category", PrivateDesktopExcelFeasibilityFactAttribute.Category)]
    public async Task PrivateDesktopTerminalModesReleaseExactOwnershipAndArtifacts()
    {
        foreach (var mode in new[]
                 {
                     PrivateDesktopTerminalMode.Timeout,
                     PrivateDesktopTerminalMode.CommandFailure,
                     PrivateDesktopTerminalMode.CooperativeCancellation,
                     PrivateDesktopTerminalMode.UnexpectedProcessLoss
                 })
        {
            await AssertTerminalModeAsync(mode);
        }
    }

    [PrivateDesktopExcelFeasibilityFact]
    [Trait("Category", PrivateDesktopExcelFeasibilityFactAttribute.Category)]
    public async Task UnisolatedControlObserverReproducesBootstrapAndTargetLeaks()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialStagingDirectories = CaptureInitialWorkbookStagingDirectories();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease unusedPrivateDesktop = null!;
        DesktopWindowObservationScope unusedPrivateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        var artifactGuard = new InitialWorkbookArtifactGuard();
        InitialWorkbookStagingArtifact staging = null!;
        var targetIdentity = $"vba-dev initial target {Guid.NewGuid():N}";
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        object? workbook = null;
        Task<bool>? targetSave = null;
        try
        {
            unusedPrivateDesktop = WindowsPrivateDesktopLease.Create();
            unusedPrivateScope = new DesktopWindowObservationScope(
                unusedPrivateDesktop.Handle,
                unusedPrivateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            staging = artifactGuard.CreateStagingArtifact();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(desktopName: null, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                unusedPrivateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);
            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            _ = await AwaitBoundedBindingAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        launch.PrimaryThread.ResumeExactlyOnce();
                        application = new WindowsExcelNativeObjectModelBinder()
                            .BindApplicationOnCallerDesktopForUnisolatedControl(
                                owner.ProcessId,
                                () => owner.HasExited);
                        dynamic excel = application;
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        excel.AutomationSecurity = 3;
                        workbooks = excel.Workbooks;
                        return new NativeBindingEvidence(
                            new nint(Convert.ToInt64(excel.Hwnd)),
                            Convert.ToString(excel.Version) ?? string.Empty);
                    },
                    CancellationToken.None),
                owner);

            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var bootstrapLeak = observer.Evidence.Observations.FirstOrDefault(
                observation => IsCallerDesktopExposure(observation) &&
                    observation.Title.Contains(
                        Path.GetFileNameWithoutExtension(bootstrapPath),
                        StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(bootstrapLeak);
            Assert.Equal(owner.ProcessId, bootstrapLeak.ProcessId);

            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        CloseBootstrapWorkbook(workbooks!, bootstrapPath);
                        ExcelBootstrapWorkbookFile.Delete(bootstrapPath);
                        return true;
                    },
                    CancellationToken.None),
                owner,
                "unisolated control bootstrap close",
                TimeSpan.FromSeconds(10));

            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            var targetObservationCutoff = LastObservationSequence(observer.Evidence);
            targetSave = dispatcher.InvokeAsync(
                () =>
                {
                    dynamic excel = application!;
                    dynamic workbookCollection = workbooks!;
                    workbook = workbookCollection.Add(-4167);
                    dynamic target = workbook;
                    object? worksheets = null;
                    object? worksheet = null;
                    object? cell = null;
                    try
                    {
                        worksheets = target.Worksheets;
                        dynamic sheets = worksheets;
                        worksheet = sheets.Item(1);
                        dynamic sheet = worksheet;
                        cell = sheet.Cells.Item(1, 1);
                        dynamic marker = cell;
                        marker.Value2 = targetIdentity;
                        excel.Caption = targetIdentity;
                        target.SaveAs(staging.WorkbookPath, 52);
                        return true;
                    }
                    finally
                    {
                        ComObjectReleaser.Release(cell);
                        ComObjectReleaser.Release(worksheet);
                        ComObjectReleaser.Release(worksheets);
                    }
                },
                CancellationToken.None);

            var targetLeakDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            DesktopWindowObservation? targetCaptionLeak = null;
            var targetSaveWasBlocked = false;
            while (DateTime.UtcNow < targetLeakDeadline)
            {
                observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
                targetCaptionLeak = observer.Evidence.Observations.LastOrDefault(
                    observation => observation.Sequence > targetObservationCutoff &&
                        IsCallerDesktopExposure(observation) &&
                        observation.Title.Contains(targetIdentity, StringComparison.Ordinal));
                if (targetCaptionLeak is not null &&
                    !targetSave.IsCompleted)
                {
                    targetSaveWasBlocked = true;
                    break;
                }

                if (targetSave.IsCompleted)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }

            Assert.NotNull(targetCaptionLeak);
            Assert.True(
                targetSaveWasBlocked,
                "Target SaveAs completed or faulted before the run-unique exact-PID target " +
                "workbook exposure could be accepted.");
            Assert.Equal(owner.ProcessId, targetCaptionLeak.ProcessId);
            Assert.NotEqual(nint.Zero, targetCaptionLeak.WindowHandle);
            Assert.Equal(
                DesktopWindowLifecyclePhase.WorkbookAutomation,
                targetCaptionLeak.LifecyclePhase);
            observer.Capture(DesktopWindowLifecyclePhase.Shutdown);
            await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(5));
            await AwaitCompletedTaskAsync(targetSave, TimeSpan.FromSeconds(5));
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await AwaitJobEmptyAsync(owner, TimeSpan.FromSeconds(5));
            var finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None);
            observer = null;
            Assert.True(finalEvidence.HasCallerDesktopExposure);
            Assert.All(finalEvidence.Observations, observation =>
                Assert.Equal(owner.ProcessId, observation.ProcessId));
            output.WriteLine(
                "Unisolated control leak: PID={0}; bootstrapHWND=0x{1:X}; " +
                "targetHWND=0x{2:X}; targetClass={3}; targetTitle={4}; " +
                "targetIdentity={5}; targetSaveWasBlocked={6}; observations={7}.",
                owner.ProcessId,
                bootstrapLeak.WindowHandle.ToInt64(),
                targetCaptionLeak.WindowHandle.ToInt64(),
                targetCaptionLeak.WindowClass,
                targetCaptionLeak.Title,
                targetIdentity,
                targetSaveWasBlocked,
                finalEvidence.Observations.Count);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "unisolated-control exact process-tree termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            if (targetSave is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "unisolated-control target SaveAs unwind",
                    () => AwaitCompletedTaskAsync(targetSave, TimeSpan.FromSeconds(5)));
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                unusedPrivateDesktop,
                unusedPrivateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "unisolated-control bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            if (staging is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "unisolated-control initial-workbook staging deletion",
                    () => DeleteInitialWorkbookStagingForProof(artifactGuard, staging));
            }
            AttemptCleanup(
                cleanupFailures,
                "unisolated-control exact artifact absence verification",
                () =>
                {
                    Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath));
                    Assert.True(staging is null || !File.Exists(staging.WorkbookPath));
                    Assert.True(staging is null || !Directory.Exists(staging.DirectoryPath));
                });
            ThrowIfCleanupFailed(
                cleanupFailures,
                "The unisolated control did not complete every cleanup boundary.");
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.True(initialStagingDirectories.SetEquals(CaptureInitialWorkbookStagingDirectories()));
    }

    [PrivateDesktopExcelFeasibilityFact]
    [Trait("Category", PrivateDesktopExcelFeasibilityFactAttribute.Category)]
    public async Task PrivateDesktopProbePreservesInteractiveExcelContinuously()
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialControlArtifacts = CaptureControlArtifacts();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease unusedPrivateDesktop = null!;
        DesktopWindowObservationScope unusedPrivateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        var targetPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-control-{Guid.NewGuid():N}.xlsm");
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        object? workbook = null;
        DesktopWindowExposureEvidence? finalEvidence = null;
        var controlSamples = new List<InteractiveExcelControlSample>();
        try
        {
            unusedPrivateDesktop = WindowsPrivateDesktopLease.Create();
            unusedPrivateScope = new DesktopWindowObservationScope(
                unusedPrivateDesktop.Handle,
                unusedPrivateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(desktopName: null, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                unusedPrivateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);
            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            _ = await AwaitBoundedBindingAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        launch.PrimaryThread.ResumeExactlyOnce();
                        application = new WindowsExcelNativeObjectModelBinder()
                            .BindApplicationOnCallerDesktopForUnisolatedControl(
                                owner.ProcessId,
                                () => owner.HasExited);
                        dynamic excel = application;
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        excel.AutomationSecurity = 3;
                        workbooks = excel.Workbooks;
                        return new NativeBindingEvidence(
                            new nint(Convert.ToInt64(excel.Hwnd)),
                            Convert.ToString(excel.Version) ?? string.Empty);
                    },
                    CancellationToken.None),
                owner);

            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        CloseBootstrapWorkbook(workbooks!, bootstrapPath);
                        ExcelBootstrapWorkbookFile.Delete(bootstrapPath);
                        return true;
                    },
                    CancellationToken.None),
                owner,
                "interactive baseline bootstrap close",
                TimeSpan.FromSeconds(10));

            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        dynamic workbookCollection = workbooks!;
                        workbook = workbookCollection.Add(-4167);
                        dynamic target = workbook;
                        object? worksheets = null;
                        object? worksheet = null;
                        object? cell = null;
                        object? selection = null;
                        try
                        {
                            worksheets = target.Worksheets;
                            dynamic sheets = worksheets;
                            worksheet = sheets.Item(1);
                            dynamic sheet = worksheet;
                            cell = sheet.Cells.Item(1, 1);
                            dynamic marker = cell;
                            marker.Value2 = "interactive-control-sentinel";
                            target.SaveAs(targetPath, 52);
                        }
                        finally
                        {
                            ComObjectReleaser.Release(selection);
                            ComObjectReleaser.Release(cell);
                            ComObjectReleaser.Release(worksheet);
                            ComObjectReleaser.Release(worksheets);
                        }

                        return true;
                    },
                    CancellationToken.None),
                owner,
                "interactive control workbook creation",
                TimeSpan.FromSeconds(30));

            var controlBefore = await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        dynamic excel = application!;
                        dynamic target = workbook!;
                        object? activeWindow = null;
                        object? worksheets = null;
                        object? worksheet = null;
                        object? selection = null;
                        try
                        {
                            excel.Visible = true;
                            target.Activate();
                            activeWindow = excel.ActiveWindow;
                            dynamic window = activeWindow;
                            window.Activate();
                            worksheets = target.Worksheets;
                            dynamic sheets = worksheets;
                            worksheet = sheets.Item(1);
                            dynamic sheet = worksheet;
                            selection = sheet.Range["B2"];
                            dynamic selectedCell = selection;
                            selectedCell.Select();
                            return CaptureInteractiveControlState(application, workbooks, workbook);
                        }
                        finally
                        {
                            ComObjectReleaser.Release(selection);
                            ComObjectReleaser.Release(worksheet);
                            ComObjectReleaser.Release(worksheets);
                            ComObjectReleaser.Release(activeWindow);
                        }
                    },
                    CancellationToken.None),
                owner,
                "interactive control fixture activation",
                TimeSpan.FromSeconds(10));
            Assert.True(controlBefore.Visible);
            Assert.True(SetForegroundWindow(controlBefore.ApplicationWindow));
            await WaitForForegroundWindowAsync(
                controlBefore.ApplicationWindow,
                TimeSpan.FromSeconds(2));
            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            var controlEventCutoff = LastObservationSequence(observer.Evidence);
            var baselineWindowsBefore = nativeObserver.EnumerateTopLevelWindows(callerDesktop)
                .Where(window => window.ProcessId == owner.ProcessId && window.IsVisible)
                .OrderBy(window => window.WindowHandle.ToInt64())
                .ToArray();
            Assert.Contains(
                controlBefore.ApplicationWindow,
                baselineWindowsBefore.Select(window => window.WindowHandle));
            var foregroundBefore = GetForegroundWindow();
            Assert.Equal(controlBefore.ApplicationWindow, foregroundBefore);

            var privateProbe = RunPrivateControlProbeAsync(
                initialProcesses.Append(owner.ProcessId).ToHashSet());
            try
            {
                while (!privateProbe.IsCompleted)
                {
                    controlSamples.Add(await CaptureInteractiveControlSampleAsync(
                        dispatcher,
                        application,
                        workbooks,
                        workbook,
                        owner,
                        nativeObserver,
                        callerDesktop));
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
            }
            finally
            {
                await privateProbe.WaitAsync(TimeSpan.FromMinutes(1));
            }

            controlSamples.Add(await CaptureInteractiveControlSampleAsync(
                dispatcher,
                application,
                workbooks,
                workbook,
                owner,
                nativeObserver,
                callerDesktop));

            var foregroundAfter = GetForegroundWindow();
            var baselineWindowsAfter = nativeObserver.EnumerateTopLevelWindows(callerDesktop)
                .Where(window => window.ProcessId == owner.ProcessId && window.IsVisible)
                .OrderBy(window => window.WindowHandle.ToInt64())
                .ToArray();
            var controlAfter = await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () => CaptureInteractiveControlState(application, workbooks, workbook),
                    CancellationToken.None),
                owner,
                "interactive control post-probe state capture",
                TimeSpan.FromSeconds(5));
            Assert.Equal(controlBefore, controlAfter);
            Assert.Equal(foregroundBefore, foregroundAfter);
            Assert.Equal(baselineWindowsBefore, baselineWindowsAfter);
            Assert.False(owner.HasExited);
            Assert.NotEmpty(controlSamples);
            Assert.All(controlSamples, sample =>
            {
                Assert.False(sample.OwnerExited);
                Assert.Equal(controlBefore, sample.ControlState);
                Assert.Equal(foregroundBefore, sample.ForegroundWindow);
                Assert.Equal(baselineWindowsBefore, sample.CallerWindows);
            });
            Assert.DoesNotContain(
                observer.Evidence.Observations,
                observation => observation.Sequence > controlEventCutoff &&
                    IsCallerDesktopExposure(observation));

            var evidenceBeforeExit = observer.Evidence;
            Assert.True(evidenceBeforeExit.HasCallerDesktopExposure);

            observer.Capture(DesktopWindowLifecyclePhase.Shutdown);
            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        dynamic target = workbook!;
                        target.Close(false);
                        ComObjectReleaser.Release(workbook);
                        workbook = null;
                        dynamic excel = application!;
                        excel.Quit();
                        ComObjectReleaser.Release(workbooks);
                        workbooks = null;
                        ComObjectReleaser.Release(application);
                        application = null;
                        ComObjectReleaser.CollectReleasedComObjects();
                        return true;
                    },
                    CancellationToken.None),
                owner,
                "interactive control shutdown",
                TimeSpan.FromSeconds(20));
            await owner.TerminateProcessTreeAsync(
                TimeSpan.FromSeconds(5));
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await AwaitJobEmptyAsync(owner, TimeSpan.FromSeconds(5));
            finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None);
            observer = null;
            output.WriteLine(
                "Interactive control: PID={0}; workbook={1}; foreground=0x{2:X}; " +
                "continuousSamples={3}; observations={4}.",
                owner.ProcessId,
                Path.GetFileName(targetPath),
                foregroundBefore.ToInt64(),
                controlSamples.Count,
                finalEvidence.Observations.Count);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "interactive-control exact process-tree termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                unusedPrivateDesktop,
                unusedPrivateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "interactive-control bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            AttemptCleanup(
                cleanupFailures,
                "interactive-control workbook deletion",
                () => File.Delete(targetPath));
            AttemptCleanup(
                cleanupFailures,
                "baseline exact artifact absence verification",
                () =>
                {
                    Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath));
                    Assert.False(File.Exists(targetPath));
                });
            ThrowIfCleanupFailed(
                cleanupFailures,
                "The interactive control did not complete every cleanup boundary.");
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.True(initialControlArtifacts.SetEquals(CaptureControlArtifacts()));
    }

    private async Task RunPrivateControlProbeAsync(IReadOnlySet<int> expectedProcesses)
    {
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease privateDesktop = null!;
        DesktopWindowObservationScope privateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        try
        {
            privateDesktop = WindowsPrivateDesktopLease.Create();
            privateScope = new DesktopWindowObservationScope(
                privateDesktop.Handle,
                privateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(privateDesktop.QualifiedName, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                privateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);
            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            _ = await AwaitBoundedBindingAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        launch.PrimaryThread.ResumeExactlyOnce();
                        application = new WindowsExcelNativeObjectModelBinder()
                            .BindApplicationOnDesktop(
                                owner.ProcessId,
                                privateDesktop.Handle,
                                () => owner.HasExited);
                        dynamic excel = application;
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        workbooks = excel.Workbooks;
                        CloseBootstrapWorkbook(workbooks, bootstrapPath);
                        ExcelBootstrapWorkbookFile.Delete(bootstrapPath);
                        return new NativeBindingEvidence(
                            new nint(Convert.ToInt64(excel.Hwnd)),
                            Convert.ToString(excel.Version) ?? string.Empty);
                    },
                    CancellationToken.None),
                owner);
            observer.Capture(DesktopWindowLifecyclePhase.Shutdown);
            await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        dynamic excel = application!;
                        excel.Quit();
                        ComObjectReleaser.Release(workbooks);
                        workbooks = null;
                        ComObjectReleaser.Release(application);
                        application = null;
                        ComObjectReleaser.CollectReleasedComObjects();
                        return true;
                    },
                    CancellationToken.None),
                owner,
                "private control-probe shutdown",
                TimeSpan.FromSeconds(20));
            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(20));
            await AwaitJobEmptyAsync(owner, TimeSpan.FromSeconds(5));
            var finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None);
            observer = null;
            Assert.False(finalEvidence.HasCallerDesktopExposure);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "private control-probe exact process-tree termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                privateDesktop,
                privateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "private control-probe bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            AttemptCleanup(
                cleanupFailures,
                "private control-probe exact artifact absence verification",
                () => Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath)));
            ThrowIfCleanupFailed(
                cleanupFailures,
                "The private control probe did not complete every cleanup boundary.");
        }

        await WaitForProcessSetAsync(expectedProcesses, TimeSpan.FromSeconds(20));
        Assert.True(expectedProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
    }

    private static InteractiveExcelControlState CaptureInteractiveControlState(
        object? application,
        object? workbooks,
        object? workbook)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(workbooks);
        ArgumentNullException.ThrowIfNull(workbook);
        object? activeWindow = null;
        object? activeWorkbook = null;
        object? selection = null;
        object? worksheets = null;
        object? worksheet = null;
        object? cell = null;
        try
        {
            dynamic excel = application;
            dynamic workbookCollection = workbooks;
            dynamic target = workbook;
            activeWindow = excel.ActiveWindow;
            activeWorkbook = excel.ActiveWorkbook;
            selection = excel.Selection;
            dynamic window = activeWindow;
            dynamic selectedWorkbook = activeWorkbook;
            dynamic selectedCell = selection;
            worksheets = target.Worksheets;
            dynamic sheets = worksheets;
            worksheet = sheets.Item(1);
            dynamic sheet = worksheet;
            cell = sheet.Cells.Item(1, 1);
            dynamic marker = cell;
            return new InteractiveExcelControlState(
                new nint(Convert.ToInt64(excel.Hwnd)),
                (bool)excel.Visible,
                (int)workbookCollection.Count,
                Convert.ToString(target.FullName) ?? string.Empty,
                (bool)target.Saved,
                Convert.ToString(marker.Value2) ?? string.Empty,
                new nint(Convert.ToInt64(window.Hwnd)),
                Convert.ToString(selectedWorkbook.Name) ?? string.Empty,
                Convert.ToString(selectedCell.Address) ?? string.Empty);
        }
        finally
        {
            ComObjectReleaser.Release(cell);
            ComObjectReleaser.Release(worksheet);
            ComObjectReleaser.Release(worksheets);
            ComObjectReleaser.Release(selection);
            if (!ReferenceEquals(activeWorkbook, workbook))
            {
                ComObjectReleaser.Release(activeWorkbook);
            }
            ComObjectReleaser.Release(activeWindow);
        }
    }

    private static async Task<InteractiveExcelControlSample> CaptureInteractiveControlSampleAsync(
        StaComDispatcher dispatcher,
        object? application,
        object? workbooks,
        object? workbook,
        DebugExcelProcessOwner owner,
        IDesktopWindowObservationNativeApi nativeObserver,
        DesktopWindowObservationScope callerDesktop)
    {
        var ownerExitedBefore = owner.HasExited;
        var state = await dispatcher.InvokeAsync(
                () => CaptureInteractiveControlState(application, workbooks, workbook),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var callerWindows = nativeObserver.EnumerateTopLevelWindows(callerDesktop)
            .Where(window => window.ProcessId == owner.ProcessId && window.IsVisible)
            .OrderBy(window => window.WindowHandle.ToInt64())
            .ToArray();
        return new InteractiveExcelControlSample(
            ownerExitedBefore || owner.HasExited,
            state,
            GetForegroundWindow(),
            callerWindows);
    }

    private static bool IsCallerDesktopExposure(DesktopWindowObservation observation)
        => observation.Location == DesktopWindowLocation.CallerInteractive &&
           (observation.Cause is DesktopWindowObservationCause.WinEventShow or
                   DesktopWindowObservationCause.WinEventForeground ||
               (observation.IsVisible &&
                   observation.Cause is not DesktopWindowObservationCause.WinEventHide and
                       not DesktopWindowObservationCause.WinEventDestroy));

    private static long LastObservationSequence(DesktopWindowExposureEvidence evidence)
        => evidence.Observations.Count == 0
            ? 0
            : evidence.Observations.Max(observation => observation.Sequence);

    private static async Task WaitForForegroundWindowAsync(nint expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetForegroundWindow() == expected)
            {
                return;
            }

            _ = SetForegroundWindow(expected);
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Equal(expected, GetForegroundWindow());
    }

    private static async Task AwaitJobEmptyAsync(
        DebugExcelProcessOwner owner,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (owner.ActiveJobProcessCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Equal(0u, owner.ActiveJobProcessCount);
    }

    private static async Task ReleaseComReferencesAsync(
        ICollection<Exception> failures,
        StaComDispatcher? dispatcher,
        Action release)
    {
        if (dispatcher is null)
        {
            AttemptCleanup(failures, "COM reference release", release);
            return;
        }

        await AttemptCleanupAsync(
            failures,
            "COM reference release",
            async () =>
            {
                var releaseTask = dispatcher.InvokeAsync(
                    () =>
                    {
                        release();
                        ComObjectReleaser.CollectReleasedComObjects();
                        return true;
                    },
                    CancellationToken.None);
                _ = await releaseTask.WaitAsync(TimeSpan.FromSeconds(5));
            });
    }

    private static async Task CleanupProofInfrastructureAsync(
        ICollection<Exception> failures,
        DebugExcelProcessOwner? owner,
        WindowsDebugProcessJob? unownedJob,
        DebugSuspendedProcessLaunch? launch,
        OwnedDesktopWindowExposureObserver? observer,
        StaComDispatcher? dispatcher,
        WindowsPrivateDesktopLease? privateDesktop,
        DesktopWindowObservationScope? privateScope)
    {
        if (observer is not null && owner is not null)
        {
            await AttemptCleanupAsync(
                failures,
                "window observation through exact process exit",
                async () =>
                {
                    _ = await observer.CompleteAfterExitAsync(
                            owner.Completion,
                            CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5));
                });
        }
        if (observer is not null)
        {
            await AttemptCleanupAsync(
                failures,
                "window observer disposal",
                () => observer.DisposeAsync().AsTask());
        }

        if (owner is not null)
        {
            await AttemptCleanupAsync(
                failures,
                "owned process disposal",
                () => owner.DisposeAsync().AsTask());
        }
        else
        {
            AttemptCleanup(failures, "unowned Job disposal", () => unownedJob?.Dispose());
            AttemptCleanup(failures, "unowned process disposal", () => launch?.Process.Dispose());
        }

        AttemptCleanup(
            failures,
            "suspended primary-thread handle disposal",
            () => launch?.PrimaryThread.Dispose());
        if (dispatcher is not null)
        {
            await AttemptCleanupAsync(
                failures,
                "STA dispatcher disposal",
                () => dispatcher.DisposeAsync().AsTask());
        }

        if (privateScope is not null)
        {
            AttemptCleanup(
                failures,
                "private desktop emptiness verification",
                () => Assert.Empty(
                    WindowsDesktopWindowObservationNativeApi.Instance
                        .EnumerateTopLevelWindows(privateScope)));
        }

        if (privateDesktop is not null)
        {
            AttemptCleanup(failures, "private desktop disposal", privateDesktop.Dispose);
            AttemptCleanup(
                failures,
                "private desktop handle release verification",
                () => Assert.Throws<ObjectDisposedException>(() => _ = privateDesktop.Handle));
        }
    }

    private static void ThrowIfCleanupFailed(
        IReadOnlyCollection<Exception> failures,
        string message)
    {
        if (failures.Count > 0)
        {
            throw new AggregateException(message, failures);
        }
    }

    private static void DeleteInitialWorkbookStagingForProof(
        InitialWorkbookArtifactGuard artifactGuard,
        InitialWorkbookStagingArtifact staging)
    {
        InitialWorkbookArtifactEvidence? evidence = null;
        if (File.Exists(staging.WorkbookPath))
        {
            evidence = artifactGuard.Capture(staging.WorkbookPath);
        }

        var guardedCleanup = artifactGuard.TryDeleteStaging(staging, evidence);
        if (!guardedCleanup.RemovedOrAbsent && Directory.Exists(staging.DirectoryPath))
        {
            var stagingPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(staging.DirectoryPath));
            var tempPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            Assert.Equal(
                tempPath,
                Directory.GetParent(stagingPath)?.FullName,
                StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith(
                "vba-dev-new-",
                Path.GetFileName(stagingPath),
                StringComparison.Ordinal);
            Assert.False(
                File.GetAttributes(stagingPath).HasFlag(FileAttributes.ReparsePoint),
                $"The proof-owned staging directory became a reparse point: '{stagingPath}'.");
            Directory.Delete(stagingPath, recursive: true);
        }

        Assert.False(File.Exists(staging.WorkbookPath));
        Assert.False(Directory.Exists(staging.DirectoryPath));
    }

    private async Task AssertTerminalModeAsync(PrivateDesktopTerminalMode mode)
    {
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapArtifacts = CaptureBootstrapArtifacts();
        var initialProofArtifacts = CaptureProofArtifacts();
        var nativeObserver = WindowsDesktopWindowObservationNativeApi.Instance;
        var callerDesktop = nativeObserver.CaptureCurrentThreadDesktop();
        WindowsPrivateDesktopLease privateDesktop = null!;
        DesktopWindowObservationScope privateScope = null!;
        StaComDispatcher dispatcher = null!;
        string bootstrapPath = null!;
        var workbookPath = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-private-desktop-proof-{Guid.NewGuid():N}.xlsm");
        var macroEnteredPath = workbookPath + ".entered";
        var macroEnteredIdentity = $"private-desktop-macro-entered-{Guid.NewGuid():N}";
        WindowsDebugProcessJob? unownedJob = null;
        DebugSuspendedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        OwnedDesktopWindowExposureObserver? observer = null;
        object? application = null;
        object? workbooks = null;
        object? workbook = null;
        Task<bool>? blockedOperation = null;
        Task? forcedCleanup = null;
        var cleanupGate = new object();
        var terminalCleanup = new Stopwatch();
        DesktopWindowExposureEvidence? finalEvidence = null;

        void RequestExactTermination(TimeSpan grace)
        {
            lock (cleanupGate)
            {
                forcedCleanup ??= TerminateOwnedProcessAfterGraceAsync(owner!, grace);
                WorkbookAutomationStageExecutor.ObserveFault(forcedCleanup);
            }
        }

        try
        {
            privateDesktop = WindowsPrivateDesktopLease.Create();
            privateScope = new DesktopWindowObservationScope(
                privateDesktop.Handle,
                privateDesktop.QualifiedName,
                DesktopWindowLocation.Private);
            dispatcher = new StaComDispatcher();
            bootstrapPath = ExcelBootstrapWorkbookFile.Create();
            (launch, owner) = await dispatcher.InvokeAsync(
                () => LaunchSuspended(privateDesktop.QualifiedName, bootstrapPath, ref unownedJob),
                CancellationToken.None);
            observer = await OwnedDesktopWindowExposureObserver.StartAsync(
                nativeObserver,
                owner.ProcessId,
                callerDesktop,
                privateScope,
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                CancellationToken.None);
            observer.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            _ = await AwaitBoundedBindingAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        launch.PrimaryThread.ResumeExactlyOnce();
                        application = new WindowsExcelNativeObjectModelBinder()
                            .BindApplicationOnDesktop(
                                owner.ProcessId,
                                privateDesktop.Handle,
                                () => owner.HasExited);
                        dynamic excel = application;
                        excel.Visible = false;
                        excel.DisplayAlerts = false;
                        excel.AutomationSecurity = 3;
                        workbooks = excel.Workbooks;
                        CloseBootstrapWorkbook(workbooks, bootstrapPath);
                        return new NativeBindingEvidence(
                            new nint(Convert.ToInt64(excel.Hwnd)),
                            Convert.ToString(excel.Version) ?? string.Empty);
                    },
                    CancellationToken.None),
                owner);

            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            var workbookName = await AwaitBoundedOperationAsync(
                dispatcher.InvokeAsync(
                    () =>
                    {
                        workbook = CreateNonReturningMacroWorkbook(
                            application,
                            workbooks,
                            workbookPath,
                            macroEnteredPath,
                            macroEnteredIdentity,
                            observer);
                        dynamic openedWorkbook = workbook;
                        return Convert.ToString(openedWorkbook.Name) ?? string.Empty;
                    },
                    CancellationToken.None),
                owner,
                "terminal-mode workbook preparation",
                TimeSpan.FromSeconds(30));
            Assert.True(File.Exists(bootstrapPath));
            Assert.True(File.Exists(workbookPath));
            Assert.True(owner.ActiveJobProcessCount > 0);

            var executor = new WorkbookAutomationStageExecutor(
                () => owner.HasExited,
                RequestExactTermination,
                forcedTerminationObservationAllowance: TimeSpan.FromSeconds(5),
                getOwnedProcessCompletion: () => owner.Completion);
            var stage = new WorkbookAutomationStage(
                WorkbookAutomationStageKind.TestExecution,
                workbookName);
            switch (mode)
            {
                case PrivateDesktopTerminalMode.Timeout:
                    observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
                    terminalCleanup.Start();
                    var timeoutExecution = executor.ExecuteAsync(
                            stage,
                            TimeSpan.FromSeconds(5),
                            TimeSpan.Zero,
                            CancellationToken.None,
                            () => blockedOperation = RunNonReturningMacroAsync(
                                dispatcher,
                                application,
                                workbookName,
                                owner));
                    await WaitForMacroEnteredAsync(
                        macroEnteredPath,
                        macroEnteredIdentity,
                        TimeSpan.FromSeconds(3));
                    Assert.False(timeoutExecution.IsCompleted);
                    var timeoutFailure = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(
                        () => timeoutExecution);
                    Assert.Equal(stage, timeoutFailure.Stage);
                    Assert.NotNull(forcedCleanup);
                    break;

                case PrivateDesktopTerminalMode.CommandFailure:
                    observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
                    terminalCleanup.Start();
                    var commandFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => executor.ExecuteAsync(
                            stage,
                            TimeSpan.FromSeconds(5),
                            TimeSpan.Zero,
                            CancellationToken.None,
                            () => dispatcher.InvokeAsync<bool>(
                                () =>
                                {
                                    dynamic excel = application!;
                                    _ = Convert.ToString(excel.Version);
                                    throw new InvalidOperationException(
                                        "Injected private-desktop command failure.");
                                },
                                CancellationToken.None)));
                    Assert.Contains("command failure", commandFailure.Message, StringComparison.Ordinal);
                    Assert.Null(forcedCleanup);
                    break;

                case PrivateDesktopTerminalMode.CooperativeCancellation:
                    using (var cancellation = new CancellationTokenSource())
                    {
                        observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
                        var cancellationExecution = executor.ExecuteAsync(
                            stage,
                            TimeSpan.FromSeconds(30),
                            TimeSpan.Zero,
                            cancellation.Token,
                            () => blockedOperation = RunNonReturningMacroAsync(
                                dispatcher,
                                application,
                                workbookName,
                                owner));
                        await WaitForMacroEnteredAsync(
                            macroEnteredPath,
                            macroEnteredIdentity,
                            TimeSpan.FromSeconds(5));
                        Assert.NotNull(blockedOperation);
                        Assert.False(blockedOperation.IsCompleted);
                        terminalCleanup.Start();
                        cancellation.Cancel();
                        var cancellationFailure =
                            await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(
                                () => cancellationExecution);
                        Assert.Equal(stage, cancellationFailure.Stage);
                        Assert.NotNull(forcedCleanup);
                    }

                    break;

                case PrivateDesktopTerminalMode.UnexpectedProcessLoss:
                    observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
                    var processLossExecution = executor.ExecuteAsync(
                        stage,
                        TimeSpan.FromSeconds(30),
                        TimeSpan.Zero,
                        CancellationToken.None,
                        () => blockedOperation = RunNonReturningMacroAsync(
                            dispatcher,
                            application,
                            workbookName,
                            owner));
                    await WaitForMacroEnteredAsync(
                        macroEnteredPath,
                        macroEnteredIdentity,
                        TimeSpan.FromSeconds(5));
                    Assert.NotNull(blockedOperation);
                    Assert.False(blockedOperation.IsCompleted);
                    terminalCleanup.Start();
                    using (var unexpectedProcess = Process.GetProcessById(owner.ProcessId))
                    {
                        unexpectedProcess.Kill(entireProcessTree: false);
                        await unexpectedProcess.WaitForExitAsync()
                            .WaitAsync(TimeSpan.FromSeconds(5));
                    }

                    await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    var processLossFailure =
                        await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(
                            () => processLossExecution);
                    Assert.Equal(stage, processLossFailure.Stage);
                    Assert.Null(forcedCleanup);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }

            observer.Capture(DesktopWindowLifecyclePhase.Shutdown);
            RequestExactTermination(TimeSpan.Zero);

            if (forcedCleanup is not null)
            {
                await forcedCleanup.WaitAsync(TimeSpan.FromSeconds(6));
            }

            await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0u, owner.ActiveJobProcessCount);
            finalEvidence = await observer.CompleteAfterExitAsync(
                owner.Completion,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            observer = null;
            Assert.False(finalEvidence.HasCallerDesktopExposure);
            Assert.All(finalEvidence.Observations, observation =>
                Assert.Equal(owner.ProcessId, observation.ProcessId));
            if (mode == PrivateDesktopTerminalMode.CommandFailure)
            {
                Assert.False(File.Exists(macroEnteredPath));
            }
            else
            {
                Assert.Equal(macroEnteredIdentity, File.ReadAllText(macroEnteredPath));
            }
            output.WriteLine(
                "Terminal mode {0}: PID={1}; private={2}; caller exposure={3}; observations={4}.",
                mode,
                owner.ProcessId,
                privateDesktop.QualifiedName,
                finalEvidence.HasCallerDesktopExposure,
                finalEvidence.Observations.Count);
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "exact Job termination",
                    () => owner.TerminateProcessTreeAsync(
                        TimeSpan.FromSeconds(5)).AsTask());
            }

            if (blockedOperation is not null)
            {
                await AttemptCleanupAsync(
                    cleanupFailures,
                    "blocked COM operation unwind",
                    () => AwaitCompletedTaskAsync(blockedOperation, TimeSpan.FromSeconds(5)));
            }

            await ReleaseComReferencesAsync(
                cleanupFailures,
                dispatcher,
                () =>
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                    ComObjectReleaser.Release(workbooks);
                    workbooks = null;
                    ComObjectReleaser.Release(application);
                    application = null;
                });
            await CleanupProofInfrastructureAsync(
                cleanupFailures,
                owner,
                unownedJob,
                launch,
                observer,
                dispatcher,
                privateDesktop,
                privateScope);
            if (bootstrapPath is not null)
            {
                AttemptCleanup(
                    cleanupFailures,
                    "bootstrap artifact deletion",
                    () => ExcelBootstrapWorkbookFile.Delete(bootstrapPath));
            }
            AttemptCleanup(
                cleanupFailures,
                "proof artifact deletion",
                () => File.Delete(workbookPath));
            AttemptCleanup(
                cleanupFailures,
                "macro-entry marker deletion",
                () => File.Delete(macroEnteredPath));
            AttemptCleanup(
                cleanupFailures,
                "exact artifact absence verification",
                () =>
                {
                    Assert.True(bootstrapPath is null || !File.Exists(bootstrapPath));
                    Assert.False(File.Exists(workbookPath));
                    Assert.False(File.Exists(macroEnteredPath));
                });
            terminalCleanup.Stop();

            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    $"Terminal mode {mode} did not complete every cleanup boundary.",
                    cleanupFailures);
            }
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.True(initialProcesses.SetEquals(CaptureExcelProcessIds()));
        Assert.True(initialBootstrapArtifacts.SetEquals(CaptureBootstrapArtifacts()));
        Assert.True(initialProofArtifacts.SetEquals(CaptureProofArtifacts()));
        Assert.True(
            terminalCleanup.Elapsed < TimeSpan.FromSeconds(10),
            $"Terminal mode {mode} cleanup exceeded its end-to-end bound: " +
            $"{terminalCleanup.Elapsed}.");
    }

    private static object CreateNonReturningMacroWorkbook(
        object? application,
        object? workbooks,
        string workbookPath,
        string macroEnteredPath,
        string macroEnteredIdentity,
        OwnedDesktopWindowExposureObserver observer)
    {
        const int standardModuleType = 1;
        const int macroEnabledWorkbookFormat = 52;
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(workbooks);
        dynamic excel = application;
        object? workbook = null;
        object? project = null;
        object? components = null;
        object? module = null;
        object? codeModule = null;
        try
        {
            dynamic workbookCollection = workbooks;
            workbook = workbookCollection.Add(-4167);
            dynamic generatedWorkbook = workbook;
            observer.Capture(DesktopWindowLifecyclePhase.VbeAutomation);
            project = generatedWorkbook.VBProject;
            dynamic vbProject = project;
            components = vbProject.VBComponents;
            dynamic componentCollection = components;
            module = componentCollection.Add(standardModuleType);
            dynamic standardModule = module;
            standardModule.Name = "PrivateDesktopTerminalProof";
            codeModule = standardModule.CodeModule;
            dynamic source = codeModule;
            var escapedMarkerPath = macroEnteredPath.Replace("\"", "\"\"", StringComparison.Ordinal);
            var escapedMarkerIdentity = macroEnteredIdentity.Replace(
                "\"",
                "\"\"",
                StringComparison.Ordinal);
            source.AddFromString(
                $"Public Sub NeverReturns(){Environment.NewLine}" +
                $"    Dim markerChannel As Integer{Environment.NewLine}" +
                $"    markerChannel = FreeFile{Environment.NewLine}" +
                $"    Open \"{escapedMarkerPath}\" For Output As #markerChannel{Environment.NewLine}" +
                $"    Print #markerChannel, \"{escapedMarkerIdentity}\";{Environment.NewLine}" +
                $"    Close #markerChannel{Environment.NewLine}" +
                $"    Do{Environment.NewLine}" +
                $"        DoEvents{Environment.NewLine}" +
                $"    Loop{Environment.NewLine}" +
                $"End Sub{Environment.NewLine}");
            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            generatedWorkbook.SaveAs(workbookPath, macroEnabledWorkbookFormat);
        }
        finally
        {
            ComObjectReleaser.Release(codeModule);
            ComObjectReleaser.Release(module);
            ComObjectReleaser.Release(components);
            ComObjectReleaser.Release(project);
            if (workbook is not null)
            {
                try
                {
                    dynamic generatedWorkbook = workbook;
                    generatedWorkbook.Close(false);
                }
                finally
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                }
            }
        }

        observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
        excel.AutomationSecurity = 1;
        dynamic collection = workbooks;
        return collection.Open(workbookPath, 0, false);
    }

    private static Task<bool> RunNonReturningMacroAsync(
        StaComDispatcher dispatcher,
        object? application,
        string workbookName,
        DebugExcelProcessOwner owner)
        => dispatcher.InvokeAsync(
            () =>
            {
                dynamic excel = application!;
                try
                {
                    excel.Run($"'{workbookName}'!NeverReturns");
                    return true;
                }
                finally
                {
                    if (!owner.HasExited)
                    {
                        excel.AutomationSecurity = 3;
                    }
                }
            },
            CancellationToken.None);

    private static async Task WaitForMacroEnteredAsync(
        string markerPath,
        string expectedIdentity,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(markerPath) &&
                    File.ReadAllText(markerPath).Equals(
                        expectedIdentity,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The VBA publisher creates the path before it closes the exact marker content.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException(
            $"The proof macro did not publish the exact entry marker within {timeout}.");
    }

    private static async Task TerminateOwnedProcessAfterGraceAsync(
        DebugExcelProcessOwner owner,
        TimeSpan grace)
    {
        if (grace > TimeSpan.Zero)
        {
            await Task.Delay(grace);
        }

        await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AwaitCompletedTaskAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            WorkbookAutomationStageExecutor.ObserveFault(task);
            throw new TimeoutException(
                $"A terminated private-desktop COM operation did not unwind within {timeout}.");
        }

        try
        {
            await task;
        }
        catch
        {
            // Process termination is expected to fault the in-flight COM operation.
        }
    }

    private static async Task AttemptCleanupAsync(
        ICollection<Exception> failures,
        string stage,
        Func<Task> cleanup)
    {
        try
        {
            await cleanup().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"Private-desktop cleanup failed during {stage}.",
                exception));
        }
    }

    private static void AttemptCleanup(
        ICollection<Exception> failures,
        string stage,
        Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"Private-desktop cleanup failed during {stage}.",
                exception));
        }
    }

    private static object CreateBlockingUiWorkbook(
        object? application,
        object? workbooks,
        string workbookPath,
        string uiTitle,
        OwnedDesktopWindowExposureObserver observer)
    {
        const int standardModuleType = 1;
        const int macroEnabledWorkbookFormat = 52;
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(workbooks);
        dynamic excel = application;
        object? workbook = null;
        object? project = null;
        object? components = null;
        object? module = null;
        object? codeModule = null;
        try
        {
            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            dynamic workbookCollection = workbooks;
            workbook = workbookCollection.Add(-4167);
            dynamic generatedWorkbook = workbook;
            observer.Capture(DesktopWindowLifecyclePhase.VbeAutomation);
            project = generatedWorkbook.VBProject;
            dynamic vbProject = project;
            components = vbProject.VBComponents;
            dynamic componentCollection = components;
            module = componentCollection.Add(standardModuleType);
            dynamic standardModule = module;
            standardModule.Name = "PrivateDesktopUiProof";
            codeModule = standardModule.CodeModule;
            dynamic source = codeModule;
            source.AddFromString(
                $"Public Sub ShowBlockingUi(){Environment.NewLine}" +
                $"    MsgBox \"private desktop only\", vbOKOnly, \"{uiTitle}\"{Environment.NewLine}" +
                $"End Sub{Environment.NewLine}");
            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            generatedWorkbook.SaveAs(workbookPath, macroEnabledWorkbookFormat);
        }
        finally
        {
            ComObjectReleaser.Release(codeModule);
            ComObjectReleaser.Release(module);
            ComObjectReleaser.Release(components);
            ComObjectReleaser.Release(project);
            if (workbook is not null)
            {
                try
                {
                    dynamic generatedWorkbook = workbook;
                    generatedWorkbook.Close(false);
                }
                finally
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                }
            }
        }

        observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
        excel.AutomationSecurity = 1;
        dynamic collection = workbooks;
        return collection.Open(workbookPath, 0, false);
    }

    private static RepresentativeAutomationEvidence ExerciseRepresentativeAutomation(
        object? application,
        object? workbooks,
        DebugExcelProcessOwner owner,
        OwnedDesktopWindowExposureObserver observer,
        string workbookPath)
    {
        const int automationSecurityLow = 1;
        const int automationSecurityForceDisable = 3;
        const int standardModuleType = 1;
        const int macroEnabledWorkbookFormat = 52;
        const string moduleName = "PrivateDesktopProof";
        const string scriptingGuid = "420b2830-e718-11cf-893d-00a0c9054228";
        const string marker = "private-desktop-executed";

        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(workbooks);
        dynamic excel = application;
        var host = new ExcelComWorkbookSession.ExcelComHostObjects(
            application,
            workbooks,
            ExcelProcess: null,
            StrongExcelProcess: owner,
            TerminationController: null,
            CancellationRegistration: default);
        var hostEvents = new ExcelComHostEventCatalogAutomation
            .ExcelComHostEventCatalogLifecycle();
        var references = new ExcelComVbaProjectReferenceProbeAutomation
            .ExcelComVbaProjectReferenceProbeLifecycle();
        var securityTransitions = new List<int>();
        object? workbook = null;
        object? project = null;
        object? components = null;
        object? module = null;
        object? codeModule = null;
        object? reference = null;
        string? referenceGuid = null;
        string? intrinsicEventSourceName = null;
        string[] eventNames = [];
        try
        {
            hostEvents.ForceDisableAutomationSecurity(host);
            hostEvents.DisableExcelEvents(host);
            securityTransitions.Add((int)excel.AutomationSecurity);
            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            workbook = hostEvents.CreateUnsavedBlankWorkbook(host);
            observer.Capture(DesktopWindowLifecyclePhase.VbeAutomation);
            var descriptor = hostEvents.AddEmptyUserForm(workbook);
            try
            {
                var catalog = hostEvents.InspectEmptyUserForm(host, workbook, descriptor);
                intrinsicEventSourceName = catalog.IntrinsicEventSourceName;
                eventNames = catalog.Events
                    .Select(hostEvent => hostEvent.Identity.Name)
                    .ToArray();
            }
            finally
            {
                hostEvents.RemoveUserForm(workbook, descriptor);
            }

            dynamic generatedWorkbook = workbook;
            project = generatedWorkbook.VBProject;
            dynamic vbProject = project;
            components = vbProject.VBComponents;
            dynamic componentCollection = components;
            module = componentCollection.Add(standardModuleType);
            dynamic standardModule = module;
            standardModule.Name = moduleName;
            codeModule = standardModule.CodeModule;
            dynamic source = codeModule;
            source.AddFromString(
                $"Public Sub UnitTestMain(){Environment.NewLine}" +
                $"    ThisWorkbook.Worksheets(1).Range(\"A1\").Value2 = \"{marker}\"{Environment.NewLine}" +
                $"End Sub{Environment.NewLine}");

            reference = references.FindReference(
                workbook,
                "Microsoft Scripting Runtime") ?? references.AddReference(
                workbook,
                new ResolvedVbaProjectReference(
                    "Microsoft Scripting Runtime",
                    scriptingGuid,
                    1,
                    0));
            referenceGuid = references.ReadIdentity(
                reference,
                "Microsoft Scripting Runtime").Guid;
            references.ReleaseReference(reference);
            reference = null;

            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            generatedWorkbook.SaveAs(workbookPath, macroEnabledWorkbookFormat);
        }
        finally
        {
            references.ReleaseReference(reference);
            ComObjectReleaser.Release(codeModule);
            ComObjectReleaser.Release(module);
            ComObjectReleaser.Release(components);
            ComObjectReleaser.Release(project);
            if (workbook is not null)
            {
                try
                {
                    dynamic generatedWorkbook = workbook;
                    generatedWorkbook.Close(false);
                }
                finally
                {
                    ComObjectReleaser.Release(workbook);
                    workbook = null;
                }
            }
        }

        object? reopenedWorkbook = null;
        object? reopenedProject = null;
        object? reopenedComponents = null;
        object? reopenedModule = null;
        object? reopenedCodeModule = null;
        object? worksheets = null;
        object? worksheet = null;
        object? cell = null;
        var modulePersisted = false;
        string? workbookOwnedEvidence = null;
        try
        {
            observer.Capture(DesktopWindowLifecyclePhase.TestExecution);
            excel.AutomationSecurity = automationSecurityLow;
            securityTransitions.Add((int)excel.AutomationSecurity);
            dynamic workbookCollection = workbooks;
            reopenedWorkbook = workbookCollection.Open(workbookPath, 0, false);
            dynamic openedWorkbook = reopenedWorkbook;
            excel.Run($"'{openedWorkbook.Name}'!UnitTestMain");
            excel.AutomationSecurity = automationSecurityForceDisable;
            securityTransitions.Add((int)excel.AutomationSecurity);

            observer.Capture(DesktopWindowLifecyclePhase.WorkbookAutomation);
            reopenedProject = openedWorkbook.VBProject;
            dynamic vbProject = reopenedProject;
            reopenedComponents = vbProject.VBComponents;
            dynamic componentCollection = reopenedComponents;
            reopenedModule = componentCollection.Item(moduleName);
            dynamic standardModule = reopenedModule;
            reopenedCodeModule = standardModule.CodeModule;
            dynamic source = reopenedCodeModule;
            modulePersisted = ((string)source.Lines(1, (int)source.CountOfLines))
                .Contains("UnitTestMain", StringComparison.Ordinal);

            worksheets = openedWorkbook.Worksheets;
            dynamic worksheetCollection = worksheets;
            worksheet = worksheetCollection.Item(1);
            dynamic sheet = worksheet;
            cell = sheet.Cells.Item(1, 1);
            dynamic evidenceCell = cell;
            workbookOwnedEvidence = Convert.ToString(evidenceCell.Value2);
        }
        finally
        {
            if ((int)excel.AutomationSecurity != automationSecurityForceDisable)
            {
                excel.AutomationSecurity = automationSecurityForceDisable;
                securityTransitions.Add((int)excel.AutomationSecurity);
            }

            ComObjectReleaser.Release(cell);
            ComObjectReleaser.Release(worksheet);
            ComObjectReleaser.Release(worksheets);
            ComObjectReleaser.Release(reopenedCodeModule);
            ComObjectReleaser.Release(reopenedModule);
            ComObjectReleaser.Release(reopenedComponents);
            ComObjectReleaser.Release(reopenedProject);
            if (reopenedWorkbook is not null)
            {
                try
                {
                    dynamic openedWorkbook = reopenedWorkbook;
                    openedWorkbook.Close(false);
                }
                finally
                {
                    ComObjectReleaser.Release(reopenedWorkbook);
                }
            }
        }

        return new RepresentativeAutomationEvidence(
            intrinsicEventSourceName ?? string.Empty,
            eventNames,
            referenceGuid ?? string.Empty,
            modulePersisted,
            workbookOwnedEvidence ?? string.Empty,
            securityTransitions);
    }

    private static (DebugSuspendedProcessLaunch Launch, DebugExcelProcessOwner Owner)
        LaunchSuspended(
            string? desktopName,
            string bootstrapPath,
            ref WindowsDebugProcessJob? unownedJob)
    {
        unownedJob = WindowsDebugProcessJob.Create();
        var launch = unownedJob.StartSuspended(
            ExcelExecutablePathResolver.Resolve(),
            ["/x", bootstrapPath],
            desktopName);
        try
        {
            var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(
                launch.Process,
                unownedJob);
            unownedJob = null;
            return (launch, owner);
        }
        catch
        {
            launch.PrimaryThread.Dispose();
            launch.Process.Dispose();
            throw;
        }
    }

    private static async Task<NativeBindingEvidence> AwaitBoundedBindingAsync(
        Task<NativeBindingEvidence> binding,
        DebugExcelProcessOwner owner)
    {
        var completed = await Task.WhenAny(binding, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed == binding)
        {
            return await binding;
        }

        await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(5))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(6));
        await AwaitCompletedTaskAsync(binding, TimeSpan.FromSeconds(5));

        throw new TimeoutException(
            "Excel on the private desktop did not expose its native object model within 30 seconds.");
    }

    private static NativeBindingEvidence CaptureNativeBindingEvidence(
        object? application,
        DebugExcelProcessOwner owner)
    {
        ArgumentNullException.ThrowIfNull(application);
        dynamic excel = application;
        using var process = Process.GetProcessById(owner.ProcessId);
        var fileVersion = process.MainModule?.FileVersionInfo;
        return new NativeBindingEvidence(
            new nint(Convert.ToInt64(excel.Hwnd)),
            Convert.ToString(excel.Version) ?? string.Empty,
            fileVersion?.FileVersion ?? string.Empty,
            fileVersion?.ProductVersion ?? string.Empty,
            owner.ProcessArchitecture.ToString());
    }

    private static async Task<T> AwaitBoundedOperationAsync<T>(
        Task<T> operation,
        DebugExcelProcessOwner owner,
        string stage,
        TimeSpan timeout)
    {
        var completed = await Task.WhenAny(operation, Task.Delay(timeout));
        if (completed == operation)
        {
            return await operation;
        }

        await owner.TerminateProcessTreeAsync(TimeSpan.FromSeconds(5))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(6));
        await AwaitCompletedTaskAsync(operation, TimeSpan.FromSeconds(5));

        throw new TimeoutException(
            $"Private-desktop Excel did not complete {stage} within {timeout}.");
    }

    private static void CloseBootstrapWorkbook(object workbooksObject, string bootstrapPath)
    {
        dynamic workbooks = workbooksObject;
        for (var index = (int)workbooks.Count; index >= 1; index--)
        {
            object? workbookObject = null;
            try
            {
                workbookObject = workbooks.Item(index);
                dynamic workbook = workbookObject;
                if (!Path.GetFullPath((string)workbook.FullName).Equals(
                        Path.GetFullPath(bootstrapPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                workbook.Close(false);
                return;
            }
            finally
            {
                ComObjectReleaser.Release(workbookObject);
            }
        }

        throw new InvalidOperationException(
            "The private-desktop Excel process did not open the bootstrap workbook.");
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try
                {
                    processIds.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return processIds;
    }

    private static IReadOnlySet<string> CaptureBootstrapArtifacts()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-excel-bootstrap-*.xlsx",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> CaptureProofArtifacts()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-private-desktop-proof-*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> CaptureControlArtifacts()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-private-desktop-control-*.xlsm",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> CaptureInitialWorkbookStagingDirectories()
        => Directory
            .EnumerateDirectories(
                Path.GetTempPath(),
                "vba-dev-new-*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task WaitForProcessSetAsync(
        IReadOnlySet<int> expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CaptureExcelProcessIds().SetEquals(expected))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(expected.Order().ToArray(), CaptureExcelProcessIds().Order().ToArray());
    }

    private sealed record NativeBindingEvidence(
        nint ApplicationWindow,
        string ExcelVersion,
        string ExcelFileVersion = "",
        string ExcelProductVersion = "",
        string ProcessArchitecture = "");

    private sealed record RepresentativeAutomationEvidence(
        string IntrinsicEventSourceName,
        IReadOnlyList<string> EventNames,
        string ReferenceGuid,
        bool ModulePersistedAfterReopen,
        string WorkbookOwnedEvidence,
        IReadOnlyList<int> AutomationSecurityTransitions);

    private sealed record InteractiveExcelControlState(
        nint ApplicationWindow,
        bool Visible,
        int WorkbookCount,
        string WorkbookPath,
        bool Saved,
        string Marker,
        nint ActiveWindow,
        string ActiveWorkbook,
        string SelectionAddress);

    private sealed record InteractiveExcelControlSample(
        bool OwnerExited,
        InteractiveExcelControlState ControlState,
        nint ForegroundWindow,
        IReadOnlyList<DesktopWindowSnapshot> CallerWindows);

    private enum PrivateDesktopTerminalMode
    {
        Timeout,
        CommandFailure,
        CooperativeCancellation,
        UnexpectedProcessLoss
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
