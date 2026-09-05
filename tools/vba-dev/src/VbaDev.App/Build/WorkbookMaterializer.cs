using System.Runtime.ExceptionServices;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Materializes closed workbook output intents through one owned staging workflow.
/// </summary>
internal sealed class WorkbookMaterializer
{
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly IWorkbookGenerationAutomation workbookGenerationAutomation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly IWorkbookOutputTransactionFactory transactionFactory;
    private readonly VbeImportSourceSetFactory importSourceSetFactory;
    private readonly WorkbookAutomationTimeouts baseTimeouts;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();
    private readonly WorkbookMaterializationOutputValidator outputValidator = new();

    /// <summary>
    /// Creates the workbook materializer.
    /// </summary>
    /// <param name="workbookGenerationAutomation">The workbook automation port used to edit VBA projects.</param>
    /// <param name="referenceNormalizer">The service that reconciles workbook references with manifest references.</param>
    internal WorkbookMaterializer(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
        : this(
            new WorkbookSourcePlanner(),
            workbookGenerationAutomation,
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(),
            new VbeImportSourceSetFactory())
    {
    }

    /// <summary>
    /// Creates the materializer with explicit collaborators.
    /// </summary>
    internal WorkbookMaterializer(
        WorkbookSourcePlanner sourcePlanner,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory)
        : this(
            sourcePlanner,
            workbookGenerationAutomation,
            referenceNormalizer,
            transactionFactory,
            new VbeImportSourceSetFactory())
    {
    }

    internal WorkbookMaterializer(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory,
        VbeImportSourceSetFactory importSourceSetFactory)
        : this(
            new WorkbookSourcePlanner(),
            workbookGenerationAutomation,
            referenceNormalizer,
            transactionFactory,
            importSourceSetFactory)
    {
    }

    internal WorkbookMaterializer(
        WorkbookSourcePlanner sourcePlanner,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory,
        VbeImportSourceSetFactory importSourceSetFactory,
        WorkbookAutomationTimeouts? baseTimeouts = null)
    {
        this.sourcePlanner = sourcePlanner;
        this.workbookGenerationAutomation = workbookGenerationAutomation;
        this.referenceNormalizer = referenceNormalizer;
        this.transactionFactory = transactionFactory;
        this.importSourceSetFactory = importSourceSetFactory;
        this.baseTimeouts = baseTimeouts ?? WorkbookAutomationTimeouts.Default;
    }

    internal Task<WorkbookMaterializationResult> MaterializeAsync(
        WorkbookMaterializationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var context = intent switch
        {
            WorkbookMaterializationIntent.ProjectBuild build => build.Context,
            WorkbookMaterializationIntent.Publish publish => publish.Context,
            WorkbookMaterializationIntent.SourceSnapshotBuild snapshot => snapshot.Context,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };
        var targetWorkbookPath = intent switch
        {
            WorkbookMaterializationIntent.ProjectBuild => context.BinDocumentPath,
            WorkbookMaterializationIntent.Publish => context.PublishDocumentPath,
            WorkbookMaterializationIntent.SourceSnapshotBuild snapshot => snapshot.TargetWorkbookPath,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };
        var configuredTimeouts = context.Manifest.CommandDefaults?.ExcelAutomation;
        var timeouts = baseTimeouts with
        {
            WorkbookOpen = configuredTimeouts?.WorkbookOpenTimeoutSeconds is int openSeconds
                ? TimeSpan.FromSeconds(openSeconds)
                : baseTimeouts.WorkbookOpen,
            WorkbookSave = configuredTimeouts?.WorkbookSaveTimeoutSeconds is int saveSeconds
                ? TimeSpan.FromSeconds(saveSeconds)
                : baseTimeouts.WorkbookSave
        };
        IAdmittedWorkbookGenerationSourceInput sourceInput = intent switch
        {
            WorkbookMaterializationIntent.ProjectBuild =>
                sourcePlanner.CaptureBuildSourceInput(context, cancellationToken),
            WorkbookMaterializationIntent.Publish =>
                sourcePlanner.CapturePublishSourceInput(context, cancellationToken),
            WorkbookMaterializationIntent.SourceSnapshotBuild snapshot =>
                snapshot.SourceCapture,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };
        return MaterializeCoreAsync(
            context.DocumentName,
            context.TemplateDocumentPath,
            targetWorkbookPath,
            context.Document.References,
            sourceInput,
            timeouts,
            cancellationToken);
    }

    private async Task<WorkbookMaterializationResult> MaterializeCoreAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IAdmittedWorkbookGenerationSourceInput sourceInput,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        var preparedSource = CreateImportSourceSetAndReleaseInput(
            sourceInput,
            cancellationToken);
        VbeImportSourceSet? importSourceSet = preparedSource.SourceSet;
        var sourcePreflight = preparedSource.Preflight;
        IWorkbookOutputTransaction? transaction = null;
        try
        {
            transaction = transactionFactory.Create(
                templateWorkbookPath,
                targetWorkbookPath);
            var sessionResult = await workbookGenerationAutomation.RunAsync(
                transaction.StagingWorkbookPath,
                timeouts,
                async (session, operationCancellationToken) =>
                {
                    var projectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var modules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var retainedModules = modules
                        .Where(component => !component.Kind.IsImportable())
                        .ToArray();
                    var activeReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var desiredReferenceNames = desiredReferences
                        .Select(reference => reference.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var referencesKnownToRemain = activeReferences
                        .Where(reference =>
                            !reference.IsRemovable ||
                            desiredReferenceNames.Contains(reference.Name))
                        .ToArray();
                    var initialLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        retainedModules,
                        projectName,
                        referencesKnownToRemain);
                    if (initialLivePreflight.HasFailures)
                    {
                        namePreflight.ThrowIfFailed(sourcePreflight, initialLivePreflight);
                    }
                    foreach (var component in modules.Where(component => component.Kind.IsImportable()))
                    {
                        await session
                            .RemoveModuleAsync(component.Name, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    var result = await referenceNormalizer.NormalizeAsync(
                            session,
                            documentName,
                            desiredReferences,
                            operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalProjectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalModules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        finalModules,
                        finalProjectName,
                        finalReferences);
                    namePreflight.ThrowIfFailed(sourcePreflight, finalLivePreflight);

                    foreach (var sourceFile in importSourceSet.SourceFiles)
                    {
                        await session
                            .ImportModuleAsync(sourceFile, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    var verificationReport = await session
                        .VerifyAsync(operationCancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Workbook generation verification returned no verification report.");
                    var committedProjectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var committedModules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var importedComponentNames = importSourceSet.SourceFiles
                        .Select(sourceFile => sourceFile.ImportVerification.ComponentName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var committedRetainedModules = committedModules
                        .Where(component => !importedComponentNames.Contains(component.Name))
                        .ToArray();
                    var committedReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var committedLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        committedRetainedModules,
                        committedProjectName,
                        committedReferences);
                    namePreflight.ThrowIfFailed(sourcePreflight, committedLivePreflight);
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    return new WorkbookGenerationSessionResult(
                        result,
                        verificationReport,
                        importSourceSet.SourceFiles.Count);
                },
                cancellationToken).ConfigureAwait(false);

            outputValidator.Validate(transaction.StagingWorkbookPath);

            var completedImportSourceSet = importSourceSet;
            importSourceSet = null;
            completedImportSourceSet.Dispose();

            ThrowIfCanceled(
                cancellationToken,
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.OutputCommit,
                    Path.GetFileName(targetWorkbookPath)));
            try
            {
                transaction.Commit();
            }
            catch (IOException ex)
            {
                throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
            }

            var completedTransaction = transaction;
            transaction = null;
            completedTransaction.Dispose();

            // Commit is the success boundary. A later cancellation cannot turn replaced output into cancellation.
            return new WorkbookMaterializationResult(
                Path.GetFullPath(targetWorkbookPath),
                sessionResult.ImportedSourceCount,
                sessionResult.OutputWarnings,
                sessionResult.VerificationReport);
        }
        catch (Exception operationError)
        {
            var failure = operationError;
            failure = DisposeAfterFailure(importSourceSet, failure);
            failure = DisposeTransactionAfterFailure(transaction, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static Exception DisposeAfterFailure(
        IDisposable? resource,
        Exception failure)
    {
        if (resource is null)
        {
            return failure;
        }

        try
        {
            resource.Dispose();
            return failure;
        }
        catch (Exception cleanupError)
        {
            return CombineFailures(failure, cleanupError);
        }
    }

    private static Exception DisposeTransactionAfterFailure(
        IWorkbookOutputTransaction? transaction,
        Exception failure)
    {
        if (transaction is null)
        {
            return failure;
        }

        try
        {
            transaction.Dispose();
            return failure;
        }
        catch (Exception cleanupError)
        {
            return new BuildCommandException(
                $"{failure.Message} {cleanupError.Message}",
                new AggregateException(failure, cleanupError));
        }
    }

    private PreparedImportSource CreateImportSourceSetAndReleaseInput(
        IAdmittedWorkbookGenerationSourceInput sourceInput,
        CancellationToken cancellationToken)
    {
        VbeImportSourceSet? importSourceSet = null;
        WorkbookMaterializationNamePreflightReport? sourcePreflight = null;
        Exception? failure = null;
        try
        {
            ThrowIfCanceled(
                cancellationToken,
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup));
            importSourceSet = importSourceSetFactory.Create(sourceInput.Admission);
            sourcePreflight = namePreflight.InspectSourcePhase(importSourceSet.SourceFiles);
            if (sourcePreflight.HasFailures)
            {
                namePreflight.ThrowIfFailed(sourcePreflight);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            sourceInput.Dispose();
        }
        catch (Exception ex)
        {
            failure = CombineFailures(failure, ex);
        }

        if (failure is not null)
        {
            if (importSourceSet is not null)
            {
                try
                {
                    importSourceSet.Dispose();
                }
                catch (Exception ex)
                {
                    failure = CombineFailures(failure, ex);
                }
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new PreparedImportSource(importSourceSet!, sourcePreflight!);
    }

    private static Exception CombineFailures(Exception? operationError, Exception cleanupError)
        => operationError is null
            ? cleanupError
            : new InvalidOperationException(
                $"{operationError.Message} {cleanupError.Message}",
                new AggregateException(operationError, cleanupError));

    private sealed record PreparedImportSource(
        VbeImportSourceSet SourceSet,
        WorkbookMaterializationNamePreflightReport Preflight);

    private sealed record WorkbookGenerationSessionResult(
        IReadOnlyList<string> OutputWarnings,
        VbeImportVerificationReport VerificationReport,
        int ImportedSourceCount);

    private static void ThrowIfCanceled(
        CancellationToken cancellationToken,
        WorkbookAutomationStage stage)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new WorkbookAutomationCanceledException(stage, cancellationToken);
        }
    }
}
