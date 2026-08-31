using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Generates workbook outputs by copying a template, normalizing references, and importing VBA source files.
/// </summary>
public sealed class WorkbookGenerationPipeline
{
    private readonly IWorkbookGenerationAutomation workbookGenerationAutomation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly IWorkbookOutputTransactionFactory transactionFactory;
    private readonly VbeImportSourceSetFactory importSourceSetFactory;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();

    /// <summary>
    /// Creates the workbook generation pipeline.
    /// </summary>
    /// <param name="workbookBuildAutomation">The workbook automation port used to edit VBA projects.</param>
    /// <param name="referenceNormalizer">The service that reconciles workbook references with manifest references.</param>
    public WorkbookGenerationPipeline(
        IWorkbookBuildAutomation workbookBuildAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
        : this(
            new SynchronousWorkbookGenerationAutomation(workbookBuildAutomation),
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(),
            new VbeImportSourceSetFactory())
    {
    }

    /// <summary>
    /// Creates the pipeline over a strongly owned workbook generation adapter.
    /// </summary>
    public WorkbookGenerationPipeline(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
        : this(
            workbookGenerationAutomation,
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(),
            new VbeImportSourceSetFactory())
    {
    }

    /// <summary>
    /// Creates the pipeline with an explicit atomic output transaction factory.
    /// </summary>
    public WorkbookGenerationPipeline(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory)
        : this(
            workbookGenerationAutomation,
            referenceNormalizer,
            transactionFactory,
            new VbeImportSourceSetFactory())
    {
    }

    internal WorkbookGenerationPipeline(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory,
        VbeImportSourceSetFactory importSourceSetFactory)
    {
        this.workbookGenerationAutomation = workbookGenerationAutomation;
        this.referenceNormalizer = referenceNormalizer;
        this.transactionFactory = transactionFactory;
        this.importSourceSetFactory = importSourceSetFactory;
    }

    /// <summary>
    /// Generates a target workbook from a template with the supplied references and source files.
    /// </summary>
    /// <param name="documentName">The manifest document name used in warnings.</param>
    /// <param name="templateWorkbookPath">The workbook template to copy before import.</param>
    /// <param name="targetWorkbookPath">The final workbook path to replace atomically where possible.</param>
    /// <param name="desiredReferences">The manifest references that should remain in the workbook.</param>
    /// <param name="sourceFiles">The VBA source files to import after removing importable modules.</param>
    /// <returns>The generation warnings produced while preserving protected workbook references.</returns>
    public WorkbookGenerationResult Generate(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles)
        => Generate(
            documentName,
            templateWorkbookPath,
            targetWorkbookPath,
            desiredReferences,
            sourceFiles,
            CancellationToken.None);

    /// <summary>
    /// Generates a target workbook while retaining the previous completed output until atomic replacement.
    /// </summary>
    public WorkbookGenerationResult Generate(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        CancellationToken cancellationToken)
        => GenerateAsync(
                documentName,
                templateWorkbookPath,
                targetWorkbookPath,
                desiredReferences,
                sourceFiles,
                WorkbookAutomationTimeouts.Default,
                cancellationToken)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Generates and atomically commits a workbook through one bounded owned Excel process.
    /// </summary>
    public Task<WorkbookGenerationResult> GenerateAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
        => GenerateAsync(
            documentName,
            templateWorkbookPath,
            targetWorkbookPath,
            desiredReferences,
            new BorrowedWorkbookGenerationSourceInput(sourceFiles),
            timeouts,
            cancellationToken);

    internal async Task<WorkbookGenerationResult> GenerateAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IWorkbookGenerationSourceInput sourceInput,
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
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    return new WorkbookGenerationSessionResult(
                        result,
                        verificationReport);
                },
                cancellationToken).ConfigureAwait(false);

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
            return new WorkbookGenerationResult(
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
        IWorkbookGenerationSourceInput sourceInput,
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
            importSourceSet = importSourceSetFactory.Create(sourceInput.SourceFiles);
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
        VbeImportVerificationReport VerificationReport);

    /// <summary>
    /// Contains non-fatal warnings emitted while generating a workbook.
    /// </summary>
    /// <param name="Warnings">Existing warnings that should be included in command output.</param>
    /// <param name="VerificationReport">Recasing warnings that should be emitted on standard error.</param>
    public sealed record WorkbookGenerationResult(
        IReadOnlyList<string> Warnings,
        VbeImportVerificationReport VerificationReport);

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
