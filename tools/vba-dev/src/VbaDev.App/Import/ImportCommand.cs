using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Import;

/// <summary>
/// Imports exported VBA source files into an existing workbook without using vba-project.json.
/// </summary>
public sealed class ImportCommand
{
    private readonly WorkbookMaterializer materializer;
    private readonly VbaSourceAdmission sourceAdmission;

    internal ImportCommand(
        WorkbookMaterializer materializer,
        VbaSourceAdmission sourceAdmission)
    {
        this.materializer = materializer;
        this.sourceAdmission = sourceAdmission;
    }

    /// <summary>
    /// Replaces importable modules in the target workbook with source files from a directory.
    /// </summary>
    /// <param name="request">The import command input containing required source and target paths.</param>
    /// <returns>The command result describing the import operation or validation error.</returns>
    public CommandResult Run(ImportCommandRequest request)
        => RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Replaces importable modules while observing cooperative cancellation of owned Excel.
    /// </summary>
    /// <param name="request">The import command input containing required source and target paths.</param>
    /// <param name="cancellationToken">Cancels the owned workbook automation session.</param>
    /// <returns>The command result describing the import operation or validation error.</returns>
    public Task<CommandResult> RunAsync(
        ImportCommandRequest request,
        CancellationToken cancellationToken)
        => RunCoreAsync(request, cancellationToken);

    private async Task<CommandResult> RunImportAsync(
        ImportCommandRequest request,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = ResolvePath(request.WorkingDirectory, request.SourceDirectory);
        var targetWorkbookPath = ResolvePath(request.WorkingDirectory, request.TargetWorkbook);
        ValidateTargetWorkbook(targetWorkbookPath);
        var admission = sourceAdmission.Admit(
            sourceDirectory,
            VbaSourceAdmissionIntent.ExplicitImport,
            cancellationToken);
        var materialization = await materializer.MaterializeAsync(
                new WorkbookMaterializationIntent.ExplicitImport(
                    admission,
                    targetWorkbookPath),
                cancellationToken)
            .ConfigureAwait(false);
        var label = materialization.ImportedSourceCount == 1
            ? "source file"
            : "source files";
        return new CommandResult(
            0,
            $"Imported {materialization.ImportedSourceCount} {label} from {sourceDirectory} to {targetWorkbookPath}{Environment.NewLine}",
            VbeImportWarningRenderer.Render(materialization.VerificationReport));
    }

    private async Task<CommandResult> RunCoreAsync(
        ImportCommandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunImportAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (WorkbookAutomationFailureClassifier.TryClassify(
            ex, out var facts, cancellationToken.IsCancellationRequested))
        {
            if (!facts.ProcessReleaseProven)
            {
                var message = facts.CancellationObserved
                    ? $"{ex.Message} The owned Excel process release could not be verified."
                    : ex.Message;
                return CommandResult.UsageError(message).MarkOwnedProcessReleaseUnproven();
            }

            if (!facts.DispatcherRetired)
            {
                return CommandResult.UsageError(ex.Message);
            }

            if (facts.PrimaryFailure!.Category != WorkbookAutomationFailureCategory.Cancellation)
            {
                return CommandResult.UsageError(
                    facts.PrimaryFailure.Category == WorkbookAutomationFailureCategory.ComFailure
                        ? CommandErrorMessages.ExcelComAutomationFailed("import", ex)
                        : ex.Message);
            }

            var cancellation = facts.Failures.FirstOrDefault(failure =>
                failure.Error is WorkbookAutomationCanceledException) ?? facts.PrimaryFailure;
            if (cancellation.Error is not WorkbookAutomationCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return CommandResult.Cancelled(
                cancellation.Stage is null || cancellation.Stage.Kind == WorkbookAutomationStageKind.OutputCommit
                    ? "Workbook import was cancelled."
                    : ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or BuildCommandException
            or IOException or UnauthorizedAccessException)
        {
            var message = ex is WorkbookVerificationReportMissingException
                ? "Workbook import verification returned no verification report."
                : ex.Message;
            WorkbookAutomationFailureClassifier.TryClassify(ex, out var facts);
            var result = CommandResult.UsageError(message);
            return facts.ProcessReleaseProven ? result : result.MarkOwnedProcessReleaseUnproven();
        }
    }

    private static void ValidateTargetWorkbook(string targetWorkbookPath)
    {
        if (Directory.Exists(targetWorkbookPath))
        {
            throw new InvalidOperationException($"Import target workbook is not a file: {targetWorkbookPath}");
        }

        if (!File.Exists(targetWorkbookPath))
        {
            throw new InvalidOperationException($"Import target workbook was not found: {targetWorkbookPath}");
        }
    }

    private static string ResolvePath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
