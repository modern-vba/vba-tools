using System.Runtime.InteropServices;
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
    /// <param name="workbookBuildAutomation">The workbook automation port used to modify the target workbook.</param>
    public ImportCommand(IWorkbookBuildAutomation workbookBuildAutomation)
        : this(workbookBuildAutomation, new VbeImportSourceSetFactory())
    {
    }

    internal ImportCommand(
        IWorkbookBuildAutomation workbookBuildAutomation,
        VbeImportSourceSetFactory importSourceSetFactory)
    {
        workbookGenerationAutomation = workbookBuildAutomation is IWorkbookGenerationAutomation nativeAutomation
            ? nativeAutomation
            : new SynchronousWorkbookGenerationAutomation(workbookBuildAutomation);
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

    private async Task<CommandResult> RunCoreAsync(
        ImportCommandRequest request,
        CancellationToken cancellationToken)
    {
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
            var sourceFiles = ResolveSourceFiles(sourceDirectory);
            ValidateTargetWorkbook(targetWorkbookPath);
            using var importSourceSet = importSourceSetFactory.Create(sourceFiles);
            var sourcePreflight = namePreflight.InspectSourcePhase(importSourceSet.SourceFiles);
            if (sourcePreflight.HasFailures)
            {
                namePreflight.ThrowIfFailed(sourcePreflight);
            }

            await workbookGenerationAutomation.RunAsync(
                targetWorkbookPath,
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
                    operationCancellationToken.ThrowIfCancellationRequested();
                    await session.VerifyAsync(operationCancellationToken).ConfigureAwait(false);
                    operationCancellationToken.ThrowIfCancellationRequested();
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            var label = sourceFiles.Count == 1 ? "source file" : "source files";
            return CommandResult.Success($"Imported {sourceFiles.Count} {label} from {sourceDirectory} to {targetWorkbookPath}{Environment.NewLine}");
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
            return CommandResult.UsageError(ex.Message);
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

    private static IReadOnlyList<VbaSourceFile> ResolveSourceFiles(string sourceDirectory)
    {
        if (File.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Import source path is not a directory: {sourceDirectory}");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Import source directory was not found: {sourceDirectory}");
        }

        var sourceFiles = DocumentSourceSetLayout
            .EnumerateVbaSourceFiles(sourceDirectory)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {sourceDirectory}");
        }

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(sourceDirectory, sourceFiles);

        return sourceFiles
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
