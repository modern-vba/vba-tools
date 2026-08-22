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
    public async Task<WorkbookGenerationResult> GenerateAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        ThrowIfCanceled(
            cancellationToken,
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup));
        using var importSourceSet = importSourceSetFactory.Create(sourceFiles);
        var transaction = transactionFactory.Create(templateWorkbookPath, targetWorkbookPath);
        try
        {
            var warnings = await workbookGenerationAutomation.RunAsync(
                transaction.StagingWorkbookPath,
                timeouts,
                async (session, operationCancellationToken) =>
                {
                    var result = await referenceNormalizer.NormalizeAsync(
                            session,
                            documentName,
                            desiredReferences,
                            operationCancellationToken)
                        .ConfigureAwait(false);
                    var modules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    foreach (var component in modules.Where(component => component.Kind.IsImportable()))
                    {
                        await session
                            .RemoveModuleAsync(component.Name, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    foreach (var sourceFile in importSourceSet.SourceFiles)
                    {
                        await session
                            .ImportModuleAsync(sourceFile, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    await session.VerifyAsync(operationCancellationToken).ConfigureAwait(false);
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);

            importSourceSet.Dispose();

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

            transaction.Dispose();

            // Commit is the success boundary. A later cancellation cannot turn replaced output into cancellation.
            return new WorkbookGenerationResult(warnings);
        }
        catch (Exception operationError)
        {
            try
            {
                transaction.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new BuildCommandException(
                    $"{operationError.Message} {cleanupError.Message}",
                    new AggregateException(operationError, cleanupError));
            }

            throw;
        }
    }

    /// <summary>
    /// Contains non-fatal warnings emitted while generating a workbook.
    /// </summary>
    /// <param name="Warnings">The warnings that should be included in command output.</param>
    public sealed record WorkbookGenerationResult(IReadOnlyList<string> Warnings);

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
