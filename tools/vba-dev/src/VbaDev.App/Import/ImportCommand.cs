using System.Runtime.InteropServices;
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
    /// <param name="request">The import command input containing required --from and --to paths.</param>
    /// <returns>The command result describing the import operation or validation error.</returns>
    public CommandResult Run(ImportCommandRequest request)
        => RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Replaces importable modules while observing cooperative cancellation of owned Excel.
    /// </summary>
    /// <param name="request">The import command input containing required --from and --to paths.</param>
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
        if (request.FromPath is null)
        {
            return CommandResult.UsageError("--from is required.");
        }

        if (string.IsNullOrWhiteSpace(request.FromPath))
        {
            return CommandResult.UsageError("--from requires a source directory path.");
        }

        if (request.ToPath is null)
        {
            return CommandResult.UsageError("--to is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ToPath))
        {
            return CommandResult.UsageError("--to requires a target workbook path.");
        }

        var sourceDirectory = ResolveOptionPath(request.WorkingDirectory, request.FromPath);
        var targetWorkbookPath = ResolveOptionPath(request.WorkingDirectory, request.ToPath);
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
        catch (WorkbookAutomationCanceledException ex)
        {
            var message = ex.Stage.Kind == WorkbookAutomationStageKind.OutputCommit
                ? "Workbook import was cancelled."
                : ex.Message;
            return CreateCancellationResult(ex, message);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return CreateCancellationResult(ex, "Workbook import was cancelled.");
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message.Equals(
                "Workbook generation verification returned no verification report.",
                StringComparison.Ordinal)
                ? "Workbook import verification returned no verification report."
                : ex.Message;
            return PreserveReleaseProof(ex, CommandResult.UsageError(message));
        }
        catch (BuildCommandException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
        }
        catch (IOException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (COMException ex)
        {
            return CommandResult.UsageError(CommandErrorMessages.ExcelComAutomationFailed("import", ex));
        }
    }

    private static CommandResult PreserveReleaseProof(Exception error, CommandResult result)
        => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error)
            ? result.MarkOwnedProcessReleaseUnproven()
            : result;

    private static CommandResult CreateCancellationResult(Exception error, string message)
        => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error)
            ? PreserveReleaseProof(
                error,
                CommandResult.UsageError(
                    $"{message} The owned Excel process release could not be verified."))
            : CommandResult.Cancelled(message);

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

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
