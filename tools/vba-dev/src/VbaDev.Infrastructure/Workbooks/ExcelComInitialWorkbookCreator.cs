using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record InitialWorksheetIdentity(
    string TabName,
    string DocumentModuleName);

internal sealed record InitialWorkbookBaselineSnapshot(
    int SheetCount,
    IReadOnlyList<InitialWorksheetIdentity> Worksheets,
    string WorkbookDocumentModuleName,
    string VbaProjectName,
    int ComponentCount,
    IReadOnlyList<string> DocumentModuleNames,
    IReadOnlyList<string> ReferenceNames);

internal interface IExcelComInitialWorkbookSession
{
    InitialWorkbookBaselineSnapshot EstablishAndReadBaseline();

    void Save(
        string workbookPath,
        int fileFormat);

    InitialWorkbookBaselineSnapshot ReadBaseline();
}

internal interface IExcelComInitialWorkbookLifecycle
{
    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    IExcelComInitialWorkbookSession CreateWorkbook(object host, int template);

    void DisposeHost(object host, TimeSpan cleanupGrace);

    void DisposeSession(
        IExcelComInitialWorkbookSession session,
        TimeSpan cleanupGrace);
}

/// <summary>
/// Creates initial macro-enabled workbooks through an exactly owned Excel process.
/// </summary>
public sealed class ExcelComInitialWorkbookCreator : IInitialWorkbookCreator
{
    private const int XlOpenXmlWorkbookMacroEnabled = 52;
    private const int XlWbatWorksheet = -4167;
    private static readonly TimeSpan ForcedTerminationObservationAllowance =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatcherAbandonmentObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IExcelComInitialWorkbookLifecycle lifecycle;
    private readonly WorkbookAutomationTimeouts timeouts;
    private readonly IInitialWorkbookArtifactGuard artifactGuard;

    /// <summary>
    /// Creates the production initial-workbook automation adapter.
    /// </summary>
    public ExcelComInitialWorkbookCreator()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComInitialWorkbookLifecycle(),
            WorkbookAutomationTimeouts.Default,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(WorkbookAutomationTimeouts timeouts)
        : this(
            new StaComDispatcherFactory(),
            new ExcelComInitialWorkbookLifecycle(),
            timeouts,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComInitialWorkbookLifecycle lifecycle,
        WorkbookAutomationTimeouts timeouts)
        : this(
            dispatcherFactory,
            lifecycle,
            timeouts,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComInitialWorkbookLifecycle lifecycle,
        WorkbookAutomationTimeouts timeouts,
        IInitialWorkbookArtifactGuard artifactGuard)
    {
        this.dispatcherFactory = dispatcherFactory;
        this.lifecycle = lifecycle;
        this.timeouts = timeouts;
        this.artifactGuard = artifactGuard;
    }

    /// <inheritdoc />
    public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        => CreateInitialWorkbookAsync(workbookPath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    public async Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        cancellationToken.ThrowIfCancellationRequested();

        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteWorkbookPath)!);
        var staging = artifactGuard.CreateStagingArtifact();

        OwnedExcelTerminationController? candidateTerminationController = null;
        IStaComDispatcher? candidateDispatcher = null;
        WorkbookAutomationStageExecutor? candidateStageExecutor = null;
        try
        {
            candidateTerminationController = new OwnedExcelTerminationController();
            candidateDispatcher = dispatcherFactory.Create();
            candidateStageExecutor = new WorkbookAutomationStageExecutor(
                () => candidateTerminationController.HasAttachedProcessExited,
                candidateTerminationController.RequestForcedTermination,
                getOwnedProcessCompletion: () =>
                    candidateTerminationController.AttachedProcessCompletion);
        }
        catch (Exception setupError)
        {
            var setupDispatcherError = candidateDispatcher is null
                ? null
                : await DisposeDispatcherAsync(
                        candidateDispatcher,
                        allowAbandonment: false)
                    .ConfigureAwait(false);
            candidateTerminationController?.Dispose();
            var artifactCleanup = TryDeleteStaging(staging, stagingEvidence: null);
            if (!artifactCleanup.RemovedOrAbsent)
            {
                var setupArtifactError = artifactCleanup.Failure
                    ?? new InvalidOperationException(
                        "The staging artifact changed during initial workbook setup.");
                throw new InitialWorkbookArtifactRetainedException(
                    staging.WorkbookPath,
                    expectedArtifact: null,
                    artifactCleanup.TargetChanged,
                    setupDispatcherError is null
                        ? new AggregateException(setupError, setupArtifactError)
                        : new AggregateException(
                            setupError,
                            setupDispatcherError,
                            setupArtifactError));
            }

            if (setupDispatcherError is not null)
            {
                throw new AggregateException(setupError, setupDispatcherError);
            }

            ExceptionDispatchInfo.Capture(setupError).Throw();
        }

        using var terminationController = candidateTerminationController
            ?? throw new InvalidOperationException(
                "Initial workbook automation ownership was not initialized.");
        var dispatcher = candidateDispatcher
            ?? throw new InvalidOperationException(
                "Initial workbook automation dispatcher was not initialized.");
        var stageExecutor = candidateStageExecutor
            ?? throw new InvalidOperationException(
                "Initial workbook automation stage executor was not initialized.");
        object? host = null;
        IExcelComInitialWorkbookSession? session = null;
        IReadOnlyList<string>? selectableReferences = null;
        InitialWorkbookArtifactEvidence? stagingEvidence = null;
        var stagingWorkbookMayHaveBeenCreated = false;
        Exception? operationError = null;

        try
        {
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup),
                timeouts.ExcelStartup,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        host = lifecycle.Start(
                            terminationController,
                            stageCancellation);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            InitialWorkbookBaselineSnapshot? beforeSave = null;
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookOpen,
                    Path.GetFileName(absoluteWorkbookPath)),
                timeouts.WorkbookOpen,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        session = lifecycle.CreateWorkbook(host!, XlWbatWorksheet);
                        host = null;
                        beforeSave = session.EstablishAndReadBaseline();
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            selectableReferences = InitialWorkbookBaselineContract.ValidateExact(beforeSave!);

            InitialWorkbookBaselineSnapshot? afterSave = null;
            await stageExecutor.ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookSave,
                    Path.GetFileName(absoluteWorkbookPath)),
                timeouts.WorkbookSave,
                timeouts.ProcessCleanup,
                cancellationToken,
                    stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        stagingWorkbookMayHaveBeenCreated = true;
                        session!.Save(
                            staging.WorkbookPath,
                            XlOpenXmlWorkbookMacroEnabled);
                        stagingEvidence = artifactGuard.Capture(staging.WorkbookPath);
                        afterSave = session.ReadBaseline();
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            InitialWorkbookBaselineContract.ValidateExact(afterSave!);
            InitialWorkbookBaselineContract.ValidateUnchanged(beforeSave!, afterSave!);
        }
        catch (Exception exception)
        {
            operationError = exception;
        }

        var cleanupError = await CleanupAsync(
            dispatcher,
            terminationController,
            host,
            session,
            stageExecutor).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            stageExecutor.HasAbandonedOperation).ConfigureAwait(false);
        if (dispatcherError is not null)
        {
            cleanupError = cleanupError is null
                ? dispatcherError
                : new AggregateException(cleanupError, dispatcherError);
        }

        if (operationError is not null || cleanupError is not null)
        {
            var artifactCleanup = TryDeleteStaging(staging, stagingEvidence);

            if (!artifactCleanup.RemovedOrAbsent)
            {
                var terminalError = CombineErrors(operationError, cleanupError)
                    ?? new InvalidOperationException(
                        stagingWorkbookMayHaveBeenCreated
                            ? "Initial workbook creation failed after the staging workbook save began."
                            : "Initial workbook creation failed after staging was allocated.");
                var artifactError = artifactCleanup.Failure
                    ?? new InvalidOperationException(
                        "The staging workbook no longer names the exact object and bytes created by Excel.");
                throw new InitialWorkbookArtifactRetainedException(
                    staging.WorkbookPath,
                    stagingEvidence,
                    artifactCleanup.TargetChanged,
                    new AggregateException(terminalError, artifactError));
            }
        }

        if (cleanupError is not null)
        {
            throw new WorkbookAutomationCleanupException(
                "The initial workbook could not prove release of its exactly owned Excel process.",
                operationError is null
                    ? cleanupError
                    : new AggregateException(operationError, cleanupError));
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        InitialWorkbookArtifactEvidence? finalEvidence = null;
        Exception? materializationError = null;
        try
        {
            finalEvidence = artifactGuard.MaterializeCreateOnly(
                stagingEvidence!,
                absoluteWorkbookPath,
                cancellationToken);
        }
        catch (Exception exception)
        {
            materializationError = exception;
        }

        var stagingCleanup = TryDeleteStaging(staging, stagingEvidence);
        if (!stagingCleanup.RemovedOrAbsent)
        {
            var stagingError = stagingCleanup.Failure
                ?? new InvalidOperationException(
                    "The staging workbook path changed before it could be removed.");
            if (finalEvidence is not null)
            {
                var finalCleanup = TryDeleteArtifact(
                    absoluteWorkbookPath,
                    finalEvidence);
                if (!finalCleanup.RemovedOrAbsent)
                {
                    throw new InitialWorkbookArtifactRetainedException(
                        absoluteWorkbookPath,
                        finalEvidence,
                        finalCleanup.TargetChanged,
                        new AggregateException(
                            materializationError ?? stagingError,
                            stagingError,
                            finalCleanup.Failure ?? new InvalidOperationException(
                                "The final workbook no longer names the exact materialized object and bytes.")));
                }
            }

            throw new InitialWorkbookArtifactRetainedException(
                staging.WorkbookPath,
                stagingEvidence,
                stagingCleanup.TargetChanged,
                materializationError is null
                    ? stagingError
                    : new AggregateException(materializationError, stagingError));
        }

        if (materializationError is not null)
        {
            ExceptionDispatchInfo.Capture(materializationError).Throw();
        }

        return new InitialWorkbookCreationResult(
            selectableReferences!,
            finalEvidence!);
    }

    private InitialWorkbookArtifactCleanupResult TryDeleteStaging(
        InitialWorkbookStagingArtifact staging,
        InitialWorkbookArtifactEvidence? stagingEvidence)
    {
        try
        {
            return artifactGuard.TryDeleteStaging(staging, stagingEvidence);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    private InitialWorkbookArtifactCleanupResult TryDeleteArtifact(
        string workbookPath,
        InitialWorkbookArtifactEvidence? evidence)
    {
        try
        {
            return artifactGuard.TryDeleteIfUnchanged(workbookPath, evidence);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    private static Exception? CombineErrors(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : new AggregateException(first, second);
    }

    private async Task<Exception?> CleanupAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        object? host,
        IExcelComInitialWorkbookSession? session,
        WorkbookAutomationStageExecutor stageExecutor)
    {
        if (stageExecutor.HasAbandonedOperation ||
            terminationController.HasAttachedProcessExited ||
            (host is null && session is null))
        {
            return await CleanupOwnedProcessOnlyAsync(
                terminationController,
                timeouts.ProcessCleanup).ConfigureAwait(false);
        }

        terminationController.RequestForcedTermination(timeouts.ProcessCleanup);
        Exception? cooperativeCleanupError = null;
        try
        {
            var cleanupTask = dispatcher.InvokeAsync(
                () =>
                {
                    if (session is not null)
                    {
                        lifecycle.DisposeSession(session, timeouts.ProcessCleanup);
                    }
                    else
                    {
                        lifecycle.DisposeHost(host!, timeouts.ProcessCleanup);
                    }

                    return true;
                },
                CancellationToken.None);
            var completed = await Task.WhenAny(
                cleanupTask,
                Task.Delay(
                    timeouts.ProcessCleanup +
                    ForcedTerminationObservationAllowance)).ConfigureAwait(false);
            if (completed != cleanupTask)
            {
                stageExecutor.MarkOperationAbandoned();
                WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
                cooperativeCleanupError = new WorkbookAutomationTimeoutException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.ProcessCleanup),
                    timeouts.ProcessCleanup);
            }
            else
            {
                await cleanupTask.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cooperativeCleanupError = exception;
        }

        var ownershipError = await CleanupOwnedProcessOnlyAsync(
            terminationController,
            timeouts.ProcessCleanup).ConfigureAwait(false);
        if (cooperativeCleanupError is null)
        {
            return ownershipError;
        }

        return ownershipError is null
            ? cooperativeCleanupError
            : new AggregateException(cooperativeCleanupError, ownershipError);
    }

    private static async Task<Exception?> CleanupOwnedProcessOnlyAsync(
        OwnedExcelTerminationController terminationController,
        TimeSpan cleanupGrace)
    {
        try
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            await terminationController.ObserveCleanupWithinAsync(
                ForcedTerminationObservationAllowance).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> DisposeDispatcherAsync(
        IStaComDispatcher dispatcher,
        bool allowAbandonment)
    {
        try
        {
            var disposalTask = dispatcher.DisposeAsync().AsTask();
            if (!allowAbandonment)
            {
                await disposalTask.ConfigureAwait(false);
                return null;
            }

            var completed = await Task.WhenAny(
                disposalTask,
                Task.Delay(DispatcherAbandonmentObservation)).ConfigureAwait(false);
            if (completed == disposalTask)
            {
                await disposalTask.ConfigureAwait(false);
            }
            else
            {
                WorkbookAutomationStageExecutor.ObserveFault(disposalTask);
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class ExcelComInitialWorkbookLifecycle
        : IExcelComInitialWorkbookLifecycle
    {
        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);

        public IExcelComInitialWorkbookSession CreateWorkbook(object host, int template)
            => new ExcelComInitialWorkbookSession(
                ExcelComWorkbookSession.CreateOwnedForGeneration(
                    (ExcelComWorkbookSession.ExcelComHostObjects)host,
                    template));

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);

        public void DisposeSession(
            IExcelComInitialWorkbookSession session,
            TimeSpan cleanupGrace)
            => ((ExcelComInitialWorkbookSession)session)
                .DisposeOwnedGeneration(cleanupGrace);
    }
}

internal static class InitialWorkbookBaselineContract
{
    public static IReadOnlyList<string> ValidateExact(
        InitialWorkbookBaselineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SheetCount != 1 || snapshot.Worksheets.Count != 1)
        {
            throw new InvalidOperationException(
                "The initial workbook must contain exactly one worksheet and no other sheets.");
        }

        var worksheet = snapshot.Worksheets[0];
        if (!string.Equals(worksheet.TabName, "Sheet1", StringComparison.Ordinal) ||
            !string.Equals(
                worksheet.DocumentModuleName,
                "Sheet1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook worksheet tab and document module must both be named 'Sheet1'.");
        }

        if (!string.Equals(
                snapshot.WorkbookDocumentModuleName,
                "ThisWorkbook",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook document module must be named 'ThisWorkbook'.");
        }

        if (!string.Equals(
                snapshot.VbaProjectName,
                "VBAProject",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook VBProject must be named 'VBAProject'.");
        }

        if (snapshot.ComponentCount != 2 ||
            snapshot.DocumentModuleNames.Count != 2 ||
            !snapshot.DocumentModuleNames.Contains("Sheet1", StringComparer.Ordinal) ||
            !snapshot.DocumentModuleNames.Contains("ThisWorkbook", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook must contain only the 'Sheet1' and 'ThisWorkbook' document modules.");
        }

        if (snapshot.ReferenceNames.Any(string.IsNullOrWhiteSpace) ||
            snapshot.ReferenceNames.Distinct(VbaProjectReferenceName.Comparer).Count() !=
            snapshot.ReferenceNames.Count ||
            snapshot.ReferenceNames.Count(VbaProjectReferenceName.IsStandardLibrary) != 1)
        {
            throw new InvalidOperationException(
                "The initial workbook reference baseline is incomplete or ambiguous.");
        }

        return snapshot.ReferenceNames
            .Where(referenceName =>
                !VbaProjectReferenceName.IsStandardLibrary(referenceName))
            .ToArray();
    }

    public static void ValidateUnchanged(
        InitialWorkbookBaselineSnapshot beforeSave,
        InitialWorkbookBaselineSnapshot afterSave)
    {
        ArgumentNullException.ThrowIfNull(beforeSave);
        ArgumentNullException.ThrowIfNull(afterSave);
        var unchanged = beforeSave.SheetCount == afterSave.SheetCount &&
            beforeSave.Worksheets.SequenceEqual(afterSave.Worksheets) &&
            string.Equals(
                beforeSave.WorkbookDocumentModuleName,
                afterSave.WorkbookDocumentModuleName,
                StringComparison.Ordinal) &&
            string.Equals(
                beforeSave.VbaProjectName,
                afterSave.VbaProjectName,
                StringComparison.Ordinal) &&
            beforeSave.ComponentCount == afterSave.ComponentCount &&
            beforeSave.DocumentModuleNames.ToHashSet(StringComparer.Ordinal)
                .SetEquals(afterSave.DocumentModuleNames) &&
            beforeSave.ReferenceNames.SequenceEqual(
                afterSave.ReferenceNames,
                StringComparer.Ordinal);
        if (!unchanged)
        {
            throw new InvalidOperationException(
                "The saved initial workbook no longer matches its verified Excel baseline.");
        }
    }
}

internal sealed class ExcelComInitialWorkbookSession(
    ExcelComWorkbookSession session) : IExcelComInitialWorkbookSession
{
    private const int VbextCtDocument = 100;

    public InitialWorkbookBaselineSnapshot EstablishAndReadBaseline()
    {
        EstablishExactIdentities();
        return ReadBaselineCore();
    }

    public void Save(
        string workbookPath,
        int fileFormat)
    {
        dynamic workbook = session.WorkbookObject;
        workbook.SaveAs(workbookPath, fileFormat);
    }

    public InitialWorkbookBaselineSnapshot ReadBaseline()
        => ReadBaselineCore();

    public void DisposeOwnedGeneration(TimeSpan cleanupGrace)
        => session.DisposeOwnedGeneration(cleanupGrace);

    private void EstablishExactIdentities()
    {
        object? sheetsObject = null;
        object? worksheetsObject = null;
        object? worksheetObject = null;
        object? vbProjectObject = null;
        object? componentsObject = null;
        try
        {
            dynamic workbook = session.WorkbookObject;
            sheetsObject = workbook.Sheets;
            worksheetsObject = workbook.Worksheets;
            dynamic sheets = sheetsObject;
            dynamic worksheets = worksheetsObject;
            if (Convert.ToInt32(sheets.Count) != 1 ||
                Convert.ToInt32(worksheets.Count) != 1)
            {
                throw new InvalidOperationException(
                    "Excel did not create the requested one-worksheet workbook baseline.");
            }

            worksheetObject = worksheets.Item(1);
            dynamic worksheet = worksheetObject;
            worksheet.Name = "Sheet1";

            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            vbProject.Name = "VBAProject";
            componentsObject = vbProject.VBComponents;

            RenameDocumentComponent(
                componentsObject,
                Convert.ToString(worksheet.CodeName),
                "Sheet1");
            RenameDocumentComponent(
                componentsObject,
                Convert.ToString(workbook.CodeName),
                "ThisWorkbook");
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(vbProjectObject);
            ComObjectReleaser.Release(worksheetObject);
            ComObjectReleaser.Release(worksheetsObject);
            ComObjectReleaser.Release(sheetsObject);
        }
    }

    private InitialWorkbookBaselineSnapshot ReadBaselineCore()
    {
        object? sheetsObject = null;
        object? worksheetsObject = null;
        object? worksheetObject = null;
        object? vbProjectObject = null;
        object? componentsObject = null;
        object? referencesObject = null;
        try
        {
            dynamic workbook = session.WorkbookObject;
            sheetsObject = workbook.Sheets;
            worksheetsObject = workbook.Worksheets;
            dynamic sheets = sheetsObject;
            dynamic worksheets = worksheetsObject;
            var sheetCount = Convert.ToInt32(sheets.Count);
            var worksheetCount = Convert.ToInt32(worksheets.Count);
            var worksheetIdentities = new List<InitialWorksheetIdentity>(worksheetCount);
            for (var index = 1; index <= worksheetCount; index++)
            {
                worksheetObject = worksheets.Item(index);
                try
                {
                    dynamic worksheet = worksheetObject;
                    worksheetIdentities.Add(new InitialWorksheetIdentity(
                        Convert.ToString(worksheet.Name) ?? string.Empty,
                        Convert.ToString(worksheet.CodeName) ?? string.Empty));
                }
                finally
                {
                    ComObjectReleaser.Release(worksheetObject);
                    worksheetObject = null;
                }
            }

            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            var projectName = Convert.ToString(vbProject.Name) ?? string.Empty;
            componentsObject = vbProject.VBComponents;
            dynamic components = componentsObject;
            var componentCount = Convert.ToInt32(components.Count);
            var documentModuleNames = new List<string>();
            for (var index = 1; index <= componentCount; index++)
            {
                object? componentObject = null;
                try
                {
                    componentObject = components.Item(index);
                    dynamic component = componentObject;
                    if (Convert.ToInt32(component.Type) == VbextCtDocument)
                    {
                        documentModuleNames.Add(
                            Convert.ToString(component.Name) ?? string.Empty);
                    }
                }
                finally
                {
                    ComObjectReleaser.Release(componentObject);
                }
            }

            referencesObject = vbProject.References;
            dynamic references = referencesObject;
            var referenceCount = Convert.ToInt32(references.Count);
            var referenceNames = new List<string>(referenceCount);
            for (var index = 1; index <= referenceCount; index++)
            {
                object? referenceObject = null;
                try
                {
                    referenceObject = references.Item(index);
                    dynamic reference = referenceObject;
                    referenceNames.Add(
                        Convert.ToString(reference.Description) ?? string.Empty);
                }
                finally
                {
                    ComObjectReleaser.Release(referenceObject);
                }
            }

            return new InitialWorkbookBaselineSnapshot(
                sheetCount,
                worksheetIdentities,
                Convert.ToString(workbook.CodeName) ?? string.Empty,
                projectName,
                componentCount,
                documentModuleNames,
                referenceNames);
        }
        finally
        {
            ComObjectReleaser.Release(referencesObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(vbProjectObject);
            ComObjectReleaser.Release(worksheetObject);
            ComObjectReleaser.Release(worksheetsObject);
            ComObjectReleaser.Release(sheetsObject);
        }
    }

    private static void RenameDocumentComponent(
        object componentsObject,
        string? currentName,
        string requiredName)
    {
        if (string.IsNullOrEmpty(currentName))
        {
            throw new InvalidOperationException(
                $"Excel did not expose the document module required for '{requiredName}'.");
        }

        if (string.Equals(currentName, requiredName, StringComparison.Ordinal))
        {
            return;
        }

        object? componentObject = null;
        try
        {
            dynamic components = componentsObject;
            componentObject = components.Item(currentName);
            dynamic component = componentObject;
            component.Name = requiredName;
        }
        finally
        {
            ComObjectReleaser.Release(componentObject);
        }
    }
}
