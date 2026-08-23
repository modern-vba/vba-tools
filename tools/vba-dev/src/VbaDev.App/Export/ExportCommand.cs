using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

/// <summary>
/// Exports workbook modules into project or explicit source directories.
/// </summary>
public sealed class ExportCommand
{
    private readonly IWorkbookModuleExporter workbookModuleExporter;
    private readonly RecoverableExportDestinationTransaction destinationTransaction;

    /// <summary>
    /// Creates the export command.
    /// </summary>
    /// <param name="workbookModuleExporter">The workbook exporter used to read VBA project modules.</param>
    public ExportCommand(IWorkbookModuleExporter workbookModuleExporter)
        : this(workbookModuleExporter, new ExportDestinationFileOperations())
    {
    }

    /// <summary>
    /// Creates the export command with explicit destination file operations.
    /// </summary>
    /// <param name="workbookModuleExporter">The workbook exporter used to read VBA project modules.</param>
    /// <param name="destinationFileOperations">The filesystem mutations used by recoverable cleanup exports.</param>
    public ExportCommand(
        IWorkbookModuleExporter workbookModuleExporter,
        IExportDestinationFileOperations destinationFileOperations)
    {
        this.workbookModuleExporter = workbookModuleExporter;
        destinationTransaction = new RecoverableExportDestinationTransaction(destinationFileOperations);
    }

    /// <summary>
    /// Exports from a resolved project document workbook into its document source set by default.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="request">The export command request.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public CommandResult Run(ResolvedProjectContext context, ExportCommandRequest request)
        => RunAsync(context, request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Exports from a resolved project document workbook with cooperative cancellation.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="request">The export command request.</param>
    /// <param name="cancellationToken">Cancels workbook automation before destination mutation.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        ExportCommandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceWorkbookPath = string.IsNullOrWhiteSpace(request.FromPath)
                ? context.BinDocumentPath
                : ResolveOptionPath(request.WorkingDirectory, request.FromPath);
            var destinationDirectory = string.IsNullOrWhiteSpace(request.ToPath)
                ? context.DocumentSourceSetPath
                : ResolveOptionPath(request.WorkingDirectory, request.ToPath);
            var cleanDestination = true;
            var automationTimeouts = WorkbookAutomationTimeouts.Default with
            {
                WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest)
            };

            return await RunCoreAsync(
                    sourceWorkbookPath,
                    destinationDirectory,
                    cleanDestination,
                    automationTimeouts,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(ex.Message);
        }
        catch (IOException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    /// <summary>
    /// Exports from an explicit workbook path without project manifest resolution.
    /// </summary>
    /// <param name="request">The export command request containing the required --from path.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public CommandResult RunExplicit(ExportCommandRequest request)
        => RunExplicitAsync(request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Exports from an explicit workbook path with cooperative cancellation.
    /// </summary>
    /// <param name="request">The export command request containing the required --from path.</param>
    /// <param name="cancellationToken">Cancels workbook automation before destination mutation.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public async Task<CommandResult> RunExplicitAsync(
        ExportCommandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.FromPath))
            {
                return CommandResult.UsageError("--from requires a workbook path.");
            }

            var sourceWorkbookPath = ResolveOptionPath(request.WorkingDirectory, request.FromPath!);
            var destinationDirectory = string.IsNullOrWhiteSpace(request.ToPath)
                ? Path.GetFullPath(request.WorkingDirectory)
                : ResolveOptionPath(request.WorkingDirectory, request.ToPath);
            var cleanDestination = !string.IsNullOrWhiteSpace(request.ToPath);

            return await RunCoreAsync(
                    sourceWorkbookPath,
                    destinationDirectory,
                    cleanDestination,
                    WorkbookAutomationTimeouts.Default,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(ex.Message);
        }
        catch (IOException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private async Task<CommandResult> RunCoreAsync(
        string sourceWorkbookPath,
        string destinationDirectory,
        bool cleanDestination,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceWorkbookPath))
        {
            return CommandResult.UsageError($"Export source workbook was not found: {sourceWorkbookPath}");
        }

        if (File.Exists(destinationDirectory))
        {
            return CommandResult.UsageError($"Export destination is not a directory: {destinationDirectory}");
        }

        await ExportThroughStagingAsync(
                sourceWorkbookPath,
                destinationDirectory,
                cleanDestination,
                automationTimeouts,
                cancellationToken)
            .ConfigureAwait(false);

        return CommandResult.Success($"Exported {sourceWorkbookPath} to {destinationDirectory}{Environment.NewLine}");
    }

    private async Task ExportThroughStagingAsync(
        string sourceWorkbookPath,
        string destinationDirectory,
        bool cleanDestination,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vba-dev-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await workbookModuleExporter.ExportModulesAsync(
                    sourceWorkbookPath,
                    temporaryDirectory,
                    automationTimeouts,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            destinationTransaction.Apply(
                temporaryDirectory,
                destinationDirectory,
                removeStaleSources: cleanDestination);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static void DeleteTemporaryDirectory(string temporaryDirectory)
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));

    private static CommandResult CreateFailureResult(Exception error)
    {
        var result = CommandResult.UsageError(error.Message);
        return WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error)
            ? result.MarkOwnedProcessReleaseUnproven()
            : result;
    }
}
