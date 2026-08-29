using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComInitialWorkbookCreatorTests
{
    [Fact]
    public async Task DispatcherConstructionFailureCleansTheAllocatedStagingArtifact()
    {
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
    public async Task CreationUsesTheWorksheetTemplateAndReturnsSelectableReferencesInVbeOrder()
    {
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

        var workbookPath = Path.GetFullPath("Sample.xlsm");
        var result = await creator.CreateInitialWorkbookAsync(
            workbookPath,
            CancellationToken.None);

        Assert.Equal(
            ["Microsoft Excel 16.0 Object Library", "OLE Automation"],
            result.ReferenceNames);
        Assert.Equal(workbookPath, result.ArtifactEvidence.WorkbookPath);
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
    public async Task UnprovedOwnedProcessReleaseIsSurfacedAsCleanupFailure()
    {
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
                Path.GetFullPath("StagingCleanupFailure.xlsm"),
                CancellationToken.None));

        Assert.Equal(artifactGuard.Staging.WorkbookPath, error.WorkbookPath);
        Assert.False(error.TargetChanged);
        Assert.Equal(1, artifactGuard.MaterializationCalls);
        Assert.Equal(1, artifactGuard.CleanupCalls);
        Assert.Equal(1, artifactGuard.FinalCleanupCalls);
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

    private sealed class ImmediateStaComDispatcherFactory : IStaComDispatcherFactory
    {
        public IStaComDispatcher Create() => new ImmediateStaComDispatcher();
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

        public bool CompleteOwnerDuringDispose { get; init; } = true;

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
                path => SavedWorkbookPath = path);
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
        {
            Events.Add("dispose-host");
            if (CompleteOwnerDuringDispose)
            {
                Owner.Complete();
            }
        }

        public void DisposeSession(
            IExcelComInitialWorkbookSession session,
            TimeSpan cleanupGrace)
        {
            Events.Add("dispose-session");
            if (CompleteOwnerDuringDispose)
            {
                Owner.Complete();
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

        public bool HasExited => completion.Task.IsCompletedSuccessfully;

        public Task Completion => completion.Task;

        public int DisposeCalls { get; private set; }

        public Exception? TerminationError { get; set; }

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
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingInitialWorkbookArtifactGuard
        : IInitialWorkbookArtifactGuard
    {
        public InitialWorkbookStagingArtifact Staging { get; } = new(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vba-dev-test-staging")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vba-dev-test-staging", "initial.xlsm")),
            new FileSystemObjectIdentity(7, 10));

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

        public InitialWorkbookStagingArtifact CreateStagingArtifact() => Staging;

        public InitialWorkbookArtifactEvidence Capture(string workbookPath)
            => CapturedEvidence = Evidence with { WorkbookPath = workbookPath };

        public InitialWorkbookArtifactEvidence MaterializeCreateOnly(
            InitialWorkbookArtifactEvidence stagingArtifact,
            string workbookPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaterializationCalls++;
            DuringMaterialize?.Invoke();
            if (MaterializationError is not null)
            {
                throw MaterializationError;
            }

            return Evidence with { WorkbookPath = workbookPath };
        }

        public InitialWorkbookArtifactCleanupResult TryDeleteStaging(
            InitialWorkbookStagingArtifact staging,
            InitialWorkbookArtifactEvidence? expectedArtifact)
        {
            CleanupCalls++;
            return StagingCleanupResult;
        }

        public InitialWorkbookArtifactCleanupResult TryDeleteIfUnchanged(
            string workbookPath,
            InitialWorkbookArtifactEvidence? expectedArtifact)
        {
            FinalCleanupCalls++;
            return FinalCleanupResult;
        }
    }
}
