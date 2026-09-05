using VbaDev.App.Build;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.Tests;

internal static class WorkbookMaterializerTestExtensions
{
    internal static WorkbookMaterializationResult Generate(
        this WorkbookMaterializer materializer,
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        CancellationToken cancellationToken)
        => materializer.MaterializeCapturedSnapshotCompatibilityAsync(
                documentName,
                templateWorkbookPath,
                targetWorkbookPath,
                desiredReferences,
                new BorrowedWorkbookGenerationSourceInput(sourceFiles),
                WorkbookAutomationTimeouts.Default,
                cancellationToken)
            .GetAwaiter()
            .GetResult();

    internal static Task<WorkbookMaterializationResult> GenerateAsync(
        this WorkbookMaterializer materializer,
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
        => materializer.MaterializeCapturedSnapshotCompatibilityAsync(
            documentName,
            templateWorkbookPath,
            targetWorkbookPath,
            desiredReferences,
            new BorrowedWorkbookGenerationSourceInput(sourceFiles),
            timeouts,
            cancellationToken);

    internal static Task<WorkbookMaterializationResult> GenerateAsync(
        this WorkbookMaterializer materializer,
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IWorkbookGenerationSourceInput sourceInput,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
        => materializer.MaterializeCapturedSnapshotCompatibilityAsync(
            documentName,
            templateWorkbookPath,
            targetWorkbookPath,
            desiredReferences,
            sourceInput,
            timeouts,
            cancellationToken);
}
