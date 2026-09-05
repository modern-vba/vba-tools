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
        var plan = CreatePlan(intent, cancellationToken);
        return MaterializeCoreAsync(
            plan.DocumentName,
            plan.TemplateWorkbookPath,
            plan.TargetWorkbookPath,
            plan.DesiredReferences,
            plan.SourceInput,
            plan.Timeouts,
            plan.NormalizeReferences,
            plan.GuardExistingTarget,
            cancellationToken);
    }

    private WorkbookMaterializationPlan CreatePlan(
        WorkbookMaterializationIntent intent,
        CancellationToken cancellationToken)
        => intent switch
        {
            WorkbookMaterializationIntent.ProjectBuild build => CreateProjectPlan(
                build.Context,
                build.Context.BinDocumentPath,
                ResolveTimeouts(build.Context),
                sourcePlanner.CaptureBuildSourceInput(build.Context, cancellationToken)),
            WorkbookMaterializationIntent.Publish publish => CreateProjectPlan(
                publish.Context,
                publish.Context.PublishDocumentPath,
                ResolveTimeouts(publish.Context),
                sourcePlanner.CapturePublishSourceInput(publish.Context, cancellationToken)),
            WorkbookMaterializationIntent.SourceSnapshotBuild snapshot => CreateProjectPlan(
                snapshot.Context,
                snapshot.TargetWorkbookPath,
                ResolveTimeouts(snapshot.Context),
                snapshot.SourceCapture),
            WorkbookMaterializationIntent.ExplicitImport import => new WorkbookMaterializationPlan(
                Path.GetFileNameWithoutExtension(import.TargetWorkbookPath),
                import.TargetWorkbookPath,
                import.TargetWorkbookPath,
                [],
                new AdmittedWorkbookGenerationSourceInput(import.Admission),
                baseTimeouts,
                NormalizeReferences: false,
                GuardExistingTarget: true),
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };

    private WorkbookMaterializationPlan CreateProjectPlan(
        ResolvedProjectContext context,
        string targetWorkbookPath,
        WorkbookAutomationTimeouts timeouts,
        IAdmittedWorkbookGenerationSourceInput sourceInput)
        => new(
            context.DocumentName,
            context.TemplateDocumentPath,
            targetWorkbookPath,
            context.Document.References,
            sourceInput,
            timeouts,
            NormalizeReferences: true,
            GuardExistingTarget: false);

    private WorkbookAutomationTimeouts ResolveTimeouts(ResolvedProjectContext context)
    {
        var configuredTimeouts = context.Manifest.CommandDefaults?.ExcelAutomation;
        return baseTimeouts with
        {
            WorkbookOpen = configuredTimeouts?.WorkbookOpenTimeoutSeconds is int openSeconds
                ? TimeSpan.FromSeconds(openSeconds)
                : baseTimeouts.WorkbookOpen,
            WorkbookSave = configuredTimeouts?.WorkbookSaveTimeoutSeconds is int saveSeconds
                ? TimeSpan.FromSeconds(saveSeconds)
                : baseTimeouts.WorkbookSave
        };
    }

    private async Task<WorkbookMaterializationResult> MaterializeCoreAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IAdmittedWorkbookGenerationSourceInput sourceInput,
        WorkbookAutomationTimeouts timeouts,
        bool normalizeReferences,
        bool guardExistingTarget,
        CancellationToken cancellationToken)
    {
        var sourceAdmission = sourceInput.Admission;
        var preparedSource = CreateImportSourceSetAndReleaseInput(
            sourceInput,
            cancellationToken);
        VbeImportSourceSet? importSourceSet = preparedSource.SourceSet;
        var sourcePreflight = preparedSource.Preflight;
        IWorkbookOutputTransaction? transaction = null;
        FileStream? targetGuard = null;
        try
        {
            if (guardExistingTarget)
            {
                targetGuard = new FileStream(
                    targetWorkbookPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }

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
                    var referencesKnownToRemain = normalizeReferences
                        ? activeReferences
                            .Where(reference =>
                                !reference.IsRemovable ||
                                desiredReferenceNames.Contains(reference.Name))
                            .ToArray()
                        : activeReferences;
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

                    var result = normalizeReferences
                        ? await referenceNormalizer.NormalizeAsync(
                                session,
                                documentName,
                                desiredReferences,
                                operationCancellationToken)
                            .ConfigureAwait(false)
                        : [];
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
            targetGuard?.Dispose();
            targetGuard = null;
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
                sessionResult.VerificationReport,
                sourceAdmission);
        }
        catch (Exception operationError)
        {
            var failure = operationError;
            failure = DisposeAfterFailure(importSourceSet, failure);
            failure = DisposeTransactionAfterFailure(transaction, failure);
            failure = DisposeAfterFailure(targetGuard, failure);
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

    private sealed record WorkbookMaterializationPlan(
        string DocumentName,
        string TemplateWorkbookPath,
        string TargetWorkbookPath,
        IReadOnlyList<VbaProjectReference> DesiredReferences,
        IAdmittedWorkbookGenerationSourceInput SourceInput,
        WorkbookAutomationTimeouts Timeouts,
        bool NormalizeReferences,
        bool GuardExistingTarget);

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
