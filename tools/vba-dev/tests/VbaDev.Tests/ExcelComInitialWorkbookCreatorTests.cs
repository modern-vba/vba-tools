using VbaDev.Infrastructure.FileSystem;
using System.Runtime.InteropServices;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComInitialWorkbookCreatorTests
{
    [Fact]
    public async Task DispatcherRetirementFailureRetainsSavedWorkbookAndWithholdsFinalArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var savedWorkbook = new SavedWorkbookWriter();
        var workbookPath = Path.Combine(temp.Path, "UnretiredDispatcher.xlsm");
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            DuringSave = savedWorkbook.Save,
            DuringDispose = savedWorkbook.ReleaseWriter
        };
        var dispatcher = new RetirementBlockedStaComDispatcher();
        var creator = new ExcelComInitialWorkbookCreator(
            new FixedStaComDispatcherFactory(dispatcher),
            lifecycle,
            WorkbookAutomationTimeouts.Default with { ProcessCleanup = TimeSpan.FromMilliseconds(20) },
            new InitialWorkbookArtifactGuard());
        var creation = creator.CreateInitialWorkbookAsync(workbookPath, CancellationToken.None);
        try
        {
            await dispatcher.RetirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(lifecycle.Owner.HasExited);

            var observed = await Record.ExceptionAsync(() => creation.WaitAsync(TimeSpan.FromSeconds(5)));

            var failure = Assert.IsType<InitialWorkbookArtifactRetainedException>(observed);
            Assert.Contains(EnumerateFailures(failure), error => error is WorkbookAutomationCleanupException);
            Assert.Contains("dispatcher", failure.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(failure.TargetChanged);
            Assert.Equal(savedWorkbook.Path, failure.WorkbookPath);
            Assert.Equal(savedWorkbook.Bytes, File.ReadAllBytes(savedWorkbook.Path!));
            Assert.False(File.Exists(workbookPath));
        }
        finally
        {
            dispatcher.AllowRetirement.TrySetResult();
            _ = await Record.ExceptionAsync(() => creation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task CreationPreservesSavedBytesWhileExcelRetainsItsWriterUntilRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var workbookPath = Path.Combine(temp.Path, "SavedWithWriter.xlsm");
        var bytes = new byte[] { 0x51, 0x52, 0x53 };
        FileStream? writer = null;
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            DuringSave = path =>
            {
                writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                writer.Write(bytes);
                writer.Flush(flushToDisk: true);
            },
            DuringDispose = () => writer!.Dispose()
        };
        var creator = CreateCreator(lifecycle, artifactGuard: new InitialWorkbookArtifactGuard());
        try
        {
            var result = await creator.CreateInitialWorkbookAsync(
                workbookPath, ownership, CancellationToken.None);

            Assert.True(lifecycle.Owner.HasExited);
            Assert.Equal(bytes, File.ReadAllBytes(workbookPath));
            Assert.Equal(bytes.Length, result.ArtifactEvidence.Length);
            Assert.NotNull(result.OwnedArtifactReceipt);
            Assert.Equal(ExactFileSystemObjectOwnership.ObservationResult.Unchanged,
                ownership.Observe(result.OwnedArtifactReceipt!));
            Assert.False(Directory.Exists(Path.GetDirectoryName(lifecycle.SavedWorkbookPath)!));
        }
        finally
        {
            writer?.Dispose();
            if (lifecycle.SavedWorkbookPath is not null)
            {
                var stagingDirectory = Path.GetDirectoryName(lifecycle.SavedWorkbookPath)!;
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task ProcessExitAfterTheSavedStageWithholdsSuccessAndCleansProvedStaging()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var savedWorkbook = new SavedWorkbookWriter();
        var workbookPath = Path.Combine(temp.Path, "LostAfterSave.xlsm");
        RecordingInitialWorkbookLifecycle? lifecycle = null;
        lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            DuringSave = path =>
            {
                savedWorkbook.Save(path);
                savedWorkbook.ReleaseWriter();
                lifecycle!.Owner.ExitAfterNextObservation = true;
            }
        };
        var creator = CreateCreator(lifecycle, artifactGuard: new InitialWorkbookArtifactGuard());

        var failure = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(() =>
            creator.CreateInitialWorkbookAsync(workbookPath, CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.WorkbookSave, failure.Stage.Kind);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.DoesNotContain("dispose-session", lifecycle.Events);
        Assert.False(Directory.Exists(Path.GetDirectoryName(savedWorkbook.Path)!));
        Assert.False(File.Exists(workbookPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedPostSaveFailureOrCancellationStillCleansTheCapturedWorkbook(bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var savedWorkbook = new SavedWorkbookWriter();
        using var cancellation = new CancellationTokenSource();
        var workbookPath = Path.Combine(temp.Path, "PostSaveFailure.xlsm");
        var baseline = CreateExactBaseline("Visual Basic For Applications");
        var lifecycle = new RecordingInitialWorkbookLifecycle(baseline)
        {
            SavedSnapshot = cancel ? baseline : baseline with
            {
                ReferenceNames = ["Visual Basic For Applications", "changed reference"]
            },
            DuringSave = path =>
            {
                savedWorkbook.Save(path);
                if (cancel)
                {
                    cancellation.Cancel();
                }
            },
            DuringDispose = savedWorkbook.ReleaseWriter
        };
        var creator = CreateCreator(lifecycle, artifactGuard: new InitialWorkbookArtifactGuard());

        var error = await Record.ExceptionAsync(() =>
            creator.CreateInitialWorkbookAsync(workbookPath, cancellation.Token));

        if (cancel)
        {
            Assert.IsType<WorkbookAutomationCanceledException>(error);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("no longer matches", error!.Message, StringComparison.Ordinal);
        }

        Assert.True(lifecycle.Owner.HasExited);
        Assert.NotNull(savedWorkbook.Path);
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(savedWorkbook.Path)!));
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public async Task UnprovedReleaseRetainsPendingSavedWorkbookWithoutMintingCleanupAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var savedWorkbook = new SavedWorkbookWriter();
        var workbookPath = Path.Combine(temp.Path, "UnprovedRelease.xlsm");
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            DuringSave = savedWorkbook.Save,
            DuringDispose = savedWorkbook.ReleaseWriter,
            CompleteOwnerDuringDispose = false
        };
        lifecycle.Owner.TerminationError = new InvalidOperationException("synthetic termination failure");
        var creator = CreateCreator(
            lifecycle,
            WorkbookAutomationTimeouts.Default with { ProcessCleanup = TimeSpan.Zero },
            new InitialWorkbookArtifactGuard());

        var error = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(workbookPath, CancellationToken.None));

        Assert.False(lifecycle.Owner.HasExited);
        Assert.False(error.TargetChanged);
        Assert.Equal(savedWorkbook.Path, error.WorkbookPath);
        Assert.Contains(EnumerateFailures(error), failure => failure is WorkbookAutomationCleanupException);
        Assert.Equal(savedWorkbook.Bytes, File.ReadAllBytes(savedWorkbook.Path!));
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public async Task DispatcherConstructionFailureCleansTheAllocatedStagingArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var artifactGuard = new RecordingInitialWorkbookArtifactGuard();
        var creator = new ExcelComInitialWorkbookCreator(
            new ThrowingStaComDispatcherFactory(),
            new RecordingInitialWorkbookLifecycle(
                CreateExactBaseline("Visual Basic For Applications")),
            WorkbookAutomationTimeouts.Default,
            artifactGuard);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("DispatcherConstructionFailure.xlsm"),
                CancellationToken.None));

        Assert.Equal("synthetic dispatcher construction failure", error.Message);
        Assert.Equal(1, artifactGuard.CleanupCalls);
        Assert.Equal(0, artifactGuard.MaterializationCalls);
    }

    [Fact]
    public async Task MixedCleanupEvidenceCannotAuthorizePendingWorkbookCleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var savedWorkbook = new SavedWorkbookWriter();
        var workbookPath = Path.Combine(temp.Path, "UnprovedMixedCleanup.xlsm");
        var unprovedCleanup = new WorkbookAutomationCleanupException("synthetic unproved native cleanup");
        var releasedCleanup = new WorkbookAutomationReleasedProcessCleanupException(
            "synthetic post-release disposal failure");
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            DuringSave = savedWorkbook.Save,
            DuringDispose = savedWorkbook.ReleaseWriter,
            DisposeError = unprovedCleanup
        };
        lifecycle.Owner.DisposalError = releasedCleanup;
        var creator = CreateCreator(lifecycle, artifactGuard: new InitialWorkbookArtifactGuard());

        var failure = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(workbookPath, CancellationToken.None));

        Assert.True(lifecycle.Owner.HasExited);
        Assert.Contains(unprovedCleanup, EnumerateFailures(failure));
        Assert.Contains(releasedCleanup, EnumerateFailures(failure));
        Assert.False(failure.TargetChanged);
        Assert.Equal(savedWorkbook.Bytes, File.ReadAllBytes(savedWorkbook.Path!));
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public async Task CreationUsesTheWorksheetTemplateAndReturnsSelectableReferencesInVbeOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var snapshot = CreateExactBaseline(
            "Visual Basic For Applications",
            "Microsoft Excel 16.0 Object Library",
            "OLE Automation");
        var lifecycle = new RecordingInitialWorkbookLifecycle(snapshot);
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            DuringMaterialize = () => Assert.True(lifecycle.Owner.HasExited)
        };
        var creator = CreateCreator(lifecycle, artifactGuard: artifactGuard);

        var workbookPath = Path.Combine(temp.Path, "Sample.xlsm");
        var result = await creator.CreateInitialWorkbookAsync(
            workbookPath,
            CancellationToken.None);

        Assert.Equal(
            ["Microsoft Excel 16.0 Object Library", "OLE Automation"],
            result.ReferenceNames);
        Assert.Equal(workbookPath, result.ArtifactEvidence.WorkbookPath);
        Assert.Null(result.OwnedArtifactReceipt);
        Assert.True(File.Exists(workbookPath));
        Assert.Contains("create:-4167", lifecycle.Events);
        Assert.Contains("save:52", lifecycle.Events);
        Assert.Equal(
            artifactGuard.Staging.WorkbookPath,
            lifecycle.SavedWorkbookPath);
        Assert.Equal("dispose-session", lifecycle.Events[^1]);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
        Assert.Equal(1, artifactGuard.MaterializationCalls);
        Assert.Equal(1, artifactGuard.CleanupCalls);
    }

    [Fact]
    public async Task CreationRejectsAnInexactDocumentIdentityBeforeSaving()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var snapshot = CreateExactBaseline("Visual Basic For Applications") with
        {
            Worksheets = [new InitialWorksheetIdentity("Sheet1", "LocalizedSheet")]
        };
        var lifecycle = new RecordingInitialWorkbookLifecycle(snapshot);
        var creator = CreateCreator(lifecycle);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("Invalid.xlsm"),
                CancellationToken.None));

        Assert.Contains("document module", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lifecycle.Events, entry => entry.StartsWith("save:", StringComparison.Ordinal));
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
    }

    [Fact]
    public async Task CreationRejectsAReferenceChangeIntroducedWhileSaving()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var beforeSave = CreateExactBaseline(
            "Visual Basic For Applications",
            "Microsoft Excel 16.0 Object Library");
        var afterSave = CreateExactBaseline(
            "Visual Basic For Applications",
            "Microsoft Office 16.0 Object Library");
        var lifecycle = new RecordingInitialWorkbookLifecycle(beforeSave)
        {
            SavedSnapshot = afterSave
        };
        var creator = CreateCreator(lifecycle);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("Changed.xlsm"),
                CancellationToken.None));

        Assert.Contains("no longer matches", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(lifecycle.Owner.HasExited);
    }

    [Fact]
    public async Task CancellationAfterWorkbookCreationCleansTheOwnedProcessWithoutSaving()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            AfterEstablish = cancellation.Cancel
        };
        var creator = CreateCreator(lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("Cancelled.xlsm"),
                cancellation.Token));

        Assert.Equal(WorkbookAutomationStageKind.WorkbookOpen, error.Stage.Kind);
        Assert.DoesNotContain(lifecycle.Events, entry => entry.StartsWith("save:", StringComparison.Ordinal));
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
    }

    [Fact]
    public async Task CancellationWithComOnlyPostReleaseCleanupPreservesTheCancellation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            AfterEstablish = cancellation.Cancel,
            DisposeError = new COMException("The released Excel server rejected Close.")
        };
        var creator = CreateCreator(lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("CancelledWithReleasedCom.xlsm"),
                cancellation.Token));

        Assert.Equal(WorkbookAutomationStageKind.WorkbookOpen, error.Stage.Kind);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
    }

    [Fact]
    public async Task CancellationWithMixedPostReleaseCleanupSurfacesTheCleanupFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            AfterEstablish = cancellation.Cancel,
            DisposeError = new AggregateException(
                new COMException("The released Excel server rejected Close."),
                new InvalidOperationException("Unexpected cleanup defect."))
        };
        var creator = CreateCreator(lifecycle);

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("CancelledWithMixedCleanup.xlsm"),
                cancellation.Token));

        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .Flatten()
            .InnerExceptions;
        Assert.Contains(failures, failure => failure is WorkbookAutomationCanceledException);
        Assert.Contains(failures, failure => failure is COMException);
        Assert.Contains(failures, failure => failure is InvalidOperationException);
        Assert.True(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
    }

    [Fact]
    public async Task UnprovedOwnedProcessReleaseIsSurfacedAsCleanupFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"))
        {
            CompleteOwnerDuringDispose = false
        };
        lifecycle.Owner.TerminationError = new InvalidOperationException(
            "synthetic termination failure");
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard();
        var creator = CreateCreator(
            lifecycle,
            WorkbookAutomationTimeouts.Default with
            {
                ProcessCleanup = TimeSpan.Zero
            },
            artifactGuard);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("CleanupFailure.xlsm"),
                CancellationToken.None));

        Assert.Contains("could not prove release", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.InnerException);
        Assert.False(lifecycle.Owner.HasExited);
        Assert.Equal(1, lifecycle.Owner.DisposeCalls);
        Assert.Equal(1, artifactGuard.CleanupCalls);
    }

    [Fact]
    public async Task ChangedSavedArtifactIsPreservedAndReportedWithTrustedEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var beforeSave = CreateExactBaseline("Visual Basic For Applications");
        var afterSave = beforeSave with
        {
            ReferenceNames =
            [
                "Visual Basic For Applications",
                "Microsoft Excel 16.0 Object Library"
            ]
        };
        var lifecycle = new RecordingInitialWorkbookLifecycle(beforeSave)
        {
            SavedSnapshot = afterSave
        };
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            StagingCleanupResult = InitialWorkbookArtifactCleanupResult.Changed()
        };
        var creator = CreateCreator(lifecycle, artifactGuard: artifactGuard);

        var error = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(
                Path.GetFullPath("Replaced.xlsm"),
                CancellationToken.None));

        Assert.Equal(artifactGuard.Staging.WorkbookPath, error.WorkbookPath);
        Assert.Equal(artifactGuard.CapturedEvidence, error.ExpectedArtifact);
        Assert.True(error.TargetChanged);
        Assert.Equal(1, artifactGuard.CleanupCalls);
    }

    [Fact]
    public async Task FinalDestinationRaceCleansTheExactStagingWorkbookAndIsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"));
        var finalPath = Path.GetFullPath("Raced.xlsm");
        var race = new InitialWorkbookArtifactRetainedException(
            finalPath,
            expectedArtifact: null,
            targetChanged: true,
            new IOException("synthetic destination race"));
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            MaterializationError = race
        };
        var creator = CreateCreator(lifecycle, artifactGuard: artifactGuard);

        var error = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(finalPath, CancellationToken.None));

        Assert.Same(race, error);
        Assert.Equal(finalPath, error.WorkbookPath);
        Assert.True(error.TargetChanged);
        Assert.Equal(1, artifactGuard.CleanupCalls);
        Assert.Equal(0, artifactGuard.FinalCleanupCalls);
    }

    [Fact]
    public async Task StagingCleanupFailureAfterMaterializationRemovesTheExactFinalWorkbook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "StagingCleanupFailure.xlsm");
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"));
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            StagingCleanupResult = InitialWorkbookArtifactCleanupResult.Failed(
                new IOException("synthetic staging cleanup failure"))
        };
        var creator = CreateCreator(lifecycle, artifactGuard: artifactGuard);

        var error = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(
                workbookPath,
                CancellationToken.None));

        Assert.Equal(artifactGuard.Staging.WorkbookPath, error.WorkbookPath);
        Assert.False(error.TargetChanged);
        Assert.Equal(1, artifactGuard.MaterializationCalls);
        Assert.Equal(1, artifactGuard.CleanupCalls);
        Assert.Equal(1, artifactGuard.FinalCleanupCalls);
        Assert.False(File.Exists(workbookPath));
    }

    [Fact]
    public async Task FailedStagingAndFinalCleanupRetainBothStructuredPathsAndIndependentClassifications()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "BothRetained.xlsm");
        var finalFailure = new IOException("synthetic final cleanup failure");
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            StagingCleanupResult = InitialWorkbookArtifactCleanupResult.Changed(),
            FinalCleanupResult = InitialWorkbookArtifactCleanupResult.Failed(finalFailure)
        };
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"));
        var creator = CreateCreator(lifecycle, artifactGuard: artifactGuard);

        var error = await Assert.ThrowsAsync<InitialWorkbookArtifactRetainedException>(() =>
            creator.CreateInitialWorkbookAsync(workbookPath, CancellationToken.None));

        var failures = EnumerateFailures(error).ToArray();
        var retained = failures.OfType<InitialWorkbookArtifactRetainedException>().ToArray();
        Assert.Equal(2, retained.Length);
        Assert.Contains(retained, item => item.WorkbookPath == workbookPath && !item.TargetChanged);
        Assert.Contains(retained, item => item.WorkbookPath == artifactGuard.Staging.WorkbookPath && item.TargetChanged);
        Assert.All(retained, item => Assert.True(Path.IsPathFullyQualified(item.WorkbookPath)));
        Assert.Contains(finalFailure, failures);
        Assert.True(File.Exists(workbookPath));
    }

    private static IEnumerable<Exception> EnumerateFailures(Exception failure)
    {
        yield return failure;
        IEnumerable<Exception> innerFailures = failure is AggregateException aggregate
            ? aggregate.InnerExceptions
            : failure.InnerException is null ? [] : new[] { failure.InnerException };
        foreach (var innerFailure in innerFailures)
        {
            foreach (var nested in EnumerateFailures(innerFailure))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public async Task ReceiptCreationReturnsTheExactCallerSessionArtifactAfterExcelRelease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var workbookPath = Path.Combine(temp.Path, "Owned.xlsm");
        var lifecycle = new RecordingInitialWorkbookLifecycle(
            CreateExactBaseline("Visual Basic For Applications"));
        var artifactGuard = new RecordingInitialWorkbookArtifactGuard
        {
            DuringMaterialize = () => Assert.True(lifecycle.Owner.HasExited)
        };
        IReceiptInitialWorkbookCreator creator = CreateCreator(
            lifecycle,
            artifactGuard: artifactGuard);

        var result = await creator.CreateInitialWorkbookAsync(
            workbookPath,
            ownership,
            CancellationToken.None);

        var receipt = Assert.IsType<ExactFileSystemObjectOwnership.FileReceipt>(
            result.OwnedArtifactReceipt);
        Assert.Same(ownership, artifactGuard.MaterializationOwnership);
        Assert.Same(artifactGuard.MaterializedReceipt, receipt);
        Assert.Equal(workbookPath, receipt.Route);
        Assert.Equal(
            ExactFileSystemObjectOwnership.ObservationResult.Unchanged,
            ownership.Observe(receipt));
        Assert.True(ownership.TryDelete(receipt).Removed);
        Assert.False(File.Exists(workbookPath));
    }

    private static ExcelComInitialWorkbookCreator CreateCreator(
        RecordingInitialWorkbookLifecycle lifecycle,
        WorkbookAutomationTimeouts? timeouts = null,
        IInitialWorkbookArtifactGuard? artifactGuard = null)
        => new(
            new ImmediateStaComDispatcherFactory(),
            lifecycle,
            timeouts ?? WorkbookAutomationTimeouts.Default,
            artifactGuard ?? new RecordingInitialWorkbookArtifactGuard());

    private static InitialWorkbookBaselineSnapshot CreateExactBaseline(
        params string[] references)
        => new(
            SheetCount: 1,
            Worksheets: [new InitialWorksheetIdentity("Sheet1", "Sheet1")],
            WorkbookDocumentModuleName: "ThisWorkbook",
            VbaProjectName: "VBAProject",
            ComponentCount: 2,
            DocumentModuleNames: ["Sheet1", "ThisWorkbook"],
            ReferenceNames: references);

    private sealed class SavedWorkbookWriter : IDisposable
    {
        private FileStream? writer;

        public byte[] Bytes { get; } = [0x51, 0x52, 0x53];

        public string? Path { get; private set; }

        public void Save(string path)
        {
            Path = path;
            writer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            writer.Write(Bytes);
            writer.Flush(flushToDisk: true);
        }

        public void ReleaseWriter() => writer?.Dispose();

        public void Dispose()
        {
            ReleaseWriter();
            if (Path is not null)
            {
                var directory = System.IO.Path.GetDirectoryName(Path)!;
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    private sealed class ImmediateStaComDispatcherFactory : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => new ImmediateStaComDispatcher();
    }

    private sealed class FixedStaComDispatcherFactory(IStaComDispatcher dispatcher) : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => dispatcher;
    }

    private sealed class RetirementBlockedStaComDispatcher : IStaComDispatcher
    {
        internal TaskCompletionSource RetirementStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowRetirement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync()
        {
            RetirementStarted.TrySetResult();
            return new ValueTask(AllowRetirement.Task);
        }
    }

    private sealed class ThrowingStaComDispatcherFactory : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create()
            => throw new InvalidOperationException(
                "synthetic dispatcher construction failure");
    }

    private sealed class ImmediateStaComDispatcher : IStaComDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(operation());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingInitialWorkbookLifecycle(
        InitialWorkbookBaselineSnapshot snapshot) : IExcelComInitialWorkbookLifecycle
    {
        public List<string> Events { get; } = [];

        public RecordingOwnedExcelProcess Owner { get; } = new();

        public InitialWorkbookBaselineSnapshot? SavedSnapshot { get; init; }

        public Action? AfterEstablish { get; init; }

        public Action<string>? DuringSave { get; init; }

        public Action? DuringDispose { get; init; }

        public bool CompleteOwnerDuringDispose { get; init; } = true;

        public Exception? DisposeError { get; init; }

        public string? SavedWorkbookPath { get; private set; }

        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("start");
            terminationController.Attach(Owner);
            return new object();
        }

        public IExcelComInitialWorkbookSession CreateWorkbook(object host, int template)
        {
            Events.Add($"create:{template}");
            return new RecordingInitialWorkbookSession(
                Events,
                snapshot,
                SavedSnapshot ?? snapshot,
                AfterEstablish,
                path =>
                {
                    SavedWorkbookPath = path;
                    DuringSave?.Invoke(path);
                });
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            Events.Add("dispose-host");
            if (CompleteOwnerDuringDispose)
            {
                Owner.Complete();
            }

            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }

        public void DisposeSession(
            IExcelComInitialWorkbookSession session,
            TimeSpan cleanupGrace)
        {
            Events.Add("dispose-session");
            DuringDispose?.Invoke();
            if (CompleteOwnerDuringDispose)
            {
                Owner.Complete();
            }

            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }
    }

    private sealed class RecordingInitialWorkbookSession(
        List<string> events,
        InitialWorkbookBaselineSnapshot snapshot,
        InitialWorkbookBaselineSnapshot savedSnapshot,
        Action? afterEstablish,
        Action<string> recordSavedWorkbookPath) : IExcelComInitialWorkbookSession
    {
        public InitialWorkbookBaselineSnapshot EstablishAndReadBaseline()
        {
            events.Add("establish");
            afterEstablish?.Invoke();
            return snapshot;
        }

        public void Save(
            string workbookPath,
            int fileFormat)
        {
            events.Add($"save:{fileFormat}");
            recordSavedWorkbookPath(workbookPath);
        }

        public InitialWorkbookBaselineSnapshot ReadBaseline()
            => savedSnapshot;
    }

    private sealed class RecordingOwnedExcelProcess : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited
        {
            get
            {
                var hasExited = completion.Task.IsCompletedSuccessfully;
                if (ExitAfterNextObservation)
                {
                    ExitAfterNextObservation = false;
                    Complete();
                }

                return hasExited;
            }
        }

        public bool ExitAfterNextObservation { get; set; }

        public Task Completion => completion.Task;

        public int DisposeCalls { get; private set; }

        public Exception? TerminationError { get; set; }

        public Exception? DisposalError { get; set; }

        public void Complete() => completion.TrySetResult();

        public Task TerminateAsync()
        {
            if (TerminationError is not null)
            {
                return Task.FromException(TerminationError);
            }

            Complete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return DisposalError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposalError);
        }
    }

    private sealed class RecordingInitialWorkbookArtifactGuard
        : IInitialWorkbookArtifactGuard
    {
        public InitialWorkbookStagingArtifact Staging { get; } = new(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vba-dev-test-staging")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vba-dev-test-staging", "initial.xlsm")));

        public InitialWorkbookArtifactEvidence Evidence { get; } = new(
            Path.GetFullPath("created.xlsm"),
            new FileSystemObjectIdentity(7, 11),
            Length: 128,
            Sha256: new string('a', 64));

        public InitialWorkbookArtifactCleanupResult StagingCleanupResult { get; init; } =
            InitialWorkbookArtifactCleanupResult.Removed();

        public InitialWorkbookArtifactCleanupResult FinalCleanupResult { get; init; } =
            InitialWorkbookArtifactCleanupResult.Removed();

        public int CleanupCalls { get; private set; }

        public int MaterializationCalls { get; private set; }

        public int FinalCleanupCalls { get; private set; }

        public Action? DuringMaterialize { get; init; }

        public Exception? MaterializationError { get; init; }

        public InitialWorkbookArtifactEvidence? CapturedEvidence { get; private set; }

        public ExactFileSystemObjectOwnership? MaterializationOwnership { get; private set; }

        public ExactFileSystemObjectOwnership.FileReceipt? MaterializedReceipt { get; private set; }

        public InitialWorkbookStagingArtifact CreateStagingArtifact() => Staging;

        public InitialWorkbookArtifactEvidence Capture(InitialWorkbookStagingArtifact staging)
            => CapturedEvidence = Evidence with { WorkbookPath = staging.WorkbookPath };

        public void CompleteCapture(InitialWorkbookStagingArtifact staging)
        {
        }

        public InitialWorkbookMaterializedArtifact MaterializeCreateOnly(
            InitialWorkbookStagingArtifact staging,
            string workbookPath,
            ExactFileSystemObjectOwnership ownership,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaterializationCalls++;
            DuringMaterialize?.Invoke();
            if (MaterializationError is not null)
            {
                throw MaterializationError;
            }

            MaterializationOwnership = ownership;
            MaterializedReceipt = ownership.CreateOnlyFile(
                Path.GetDirectoryName(workbookPath)!,
                Path.GetFileName(workbookPath),
                new byte[128]);
            return new InitialWorkbookMaterializedArtifact(
                Evidence with { WorkbookPath = workbookPath },
                MaterializedReceipt);
        }

        public InitialWorkbookArtifactCleanupResult TryDeleteStaging(
            InitialWorkbookStagingArtifact staging)
        {
            CleanupCalls++;
            return StagingCleanupResult;
        }

        public InitialWorkbookArtifactCleanupResult TryDeleteFinalArtifact(
            ExactFileSystemObjectOwnership ownership,
            ExactFileSystemObjectOwnership.FileReceipt receipt)
        {
            FinalCleanupCalls++;
            if (FinalCleanupResult.RemovedOrAbsent)
            {
                Assert.True(ownership.TryDelete(receipt).Removed);
            }

            return FinalCleanupResult;
        }
    }
}
