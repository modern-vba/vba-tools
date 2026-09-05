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
    private readonly IWorkbookGenerationAutomation workbookGenerationAutomation;
    private readonly VbeImportSourceSetFactory importSourceSetFactory;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();

    /// <summary>
    /// Creates the import command.
    /// </summary>
    /// <param name="workbookGenerationAutomation">The workbook automation port used to modify the target workbook.</param>
    public ImportCommand(IWorkbookGenerationAutomation workbookGenerationAutomation)
        : this(workbookGenerationAutomation, new VbeImportSourceSetFactory())
    {
    }

    internal ImportCommand(
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        VbeImportSourceSetFactory importSourceSetFactory)
    {
        this.workbookGenerationAutomation = workbookGenerationAutomation;
        this.importSourceSetFactory = importSourceSetFactory;
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
        VbeImportSourceSet? ownedSourceSet = null;
        WorkbookOutputTransaction? outputTransaction = null;
        FileStream? protectedTarget = null;
        Exception? operationFailure = null;
        try
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
            var importSourceSet = importSourceSetFactory.CreateExplicitImport(sourceDirectory);
            ownedSourceSet = importSourceSet;
            var sourcePreflight = namePreflight.InspectSourcePhase(importSourceSet.SourceFiles);
            if (sourcePreflight.HasFailures)
            {
                namePreflight.ThrowIfFailed(sourcePreflight);
            }

            protectedTarget = new FileStream(targetWorkbookPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            outputTransaction = WorkbookOutputTransaction.Create(targetWorkbookPath, targetWorkbookPath);
            var verificationReport = await workbookGenerationAutomation.RunAsync(
                outputTransaction.StagingWorkbookPath,
                WorkbookAutomationTimeouts.Default,
                async (session, operationCancellationToken) =>
                {
                    var projectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var modules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var references = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var livePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        modules.Where(component => !component.Kind.IsImportable()).ToArray(),
                        projectName,
                        references);
                    namePreflight.ThrowIfFailed(sourcePreflight, livePreflight);
                    foreach (var component in modules.Where(component => component.Kind.IsImportable()))
                    {
                        operationCancellationToken.ThrowIfCancellationRequested();
                        await session
                            .RemoveModuleAsync(component.Name, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    foreach (var sourceFile in importSourceSet.SourceFiles)
                    {
                        operationCancellationToken.ThrowIfCancellationRequested();
                        await session
                            .ImportModuleAsync(sourceFile, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    importSourceSet.Dispose();
                    ownedSourceSet = null;
                    operationCancellationToken.ThrowIfCancellationRequested();
                    var report = await session
                        .VerifyAsync(operationCancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Workbook import verification returned no verification report.");
                    operationCancellationToken.ThrowIfCancellationRequested();
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    return report;
                },
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            // Release write/delete exclusion only for the synchronous atomic replacement.
            protectedTarget.Dispose();
            protectedTarget = null;
            outputTransaction.Commit();
            var label = importSourceSet.SourceFiles.Count == 1 ? "source file" : "source files";
            return new CommandResult(
                0,
                $"Imported {importSourceSet.SourceFiles.Count} {label} from {sourceDirectory} to {targetWorkbookPath}{Environment.NewLine}",
                VbeImportWarningRenderer.Render(verificationReport));
        }
        catch (Exception error)
        {
            operationFailure = error;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var artifact in new IDisposable?[] { ownedSourceSet, outputTransaction, protectedTarget })
            {
                try
                {
                    artifact?.Dispose();
                }
                catch (Exception cleanupError)
                {
                    cleanupFailures.Add(cleanupError);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                if (operationFailure is not null)
                {
                    cleanupFailures.Insert(0, operationFailure);
                }

                throw new InvalidOperationException(
                    string.Join(" ", cleanupFailures.Select(error => error.Message)),
                    new AggregateException(cleanupFailures));
            }
        }
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
            return CreateCancellationResult(ex, ex.Message);
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
            return PreserveReleaseProof(ex, CommandResult.UsageError(ex.Message));
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
