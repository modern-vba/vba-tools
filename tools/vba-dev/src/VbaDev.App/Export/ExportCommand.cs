using System.Runtime.ExceptionServices;
using VbaDev.App.FileSystem;
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
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly RecoverableExportDestinationTransaction destinationTransaction;

    /// <summary>
    /// Creates the export command.
    /// </summary>
    /// <param name="workbookModuleExporter">The workbook exporter used to read VBA project modules.</param>
    public ExportCommand(IExactFileSystemObjectOwnershipFactory ownershipFactory, IWorkbookModuleExporter workbookModuleExporter)
        : this(ownershipFactory, workbookModuleExporter, new ExportDestinationFileOperations())
    {
    }

    /// <summary>
    /// Creates the export command with explicit destination file operations.
    /// </summary>
    /// <param name="workbookModuleExporter">The workbook exporter used to read VBA project modules.</param>
    /// <param name="destinationFileOperations">The filesystem mutations used by recoverable cleanup exports.</param>
    public ExportCommand(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IWorkbookModuleExporter workbookModuleExporter,
        IExportDestinationFileOperations destinationFileOperations)
    {
        this.ownershipFactory = ownershipFactory;
        this.workbookModuleExporter = workbookModuleExporter;
        destinationTransaction = new RecoverableExportDestinationTransaction(destinationFileOperations);
    }

    /// <summary>
    /// Exports from a resolved project document workbook into its document source set by default.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="request">The export command request.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public CommandResult Run(ResolvedProjectContext context, ProjectExportCommandRequest request)
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
        ProjectExportCommandRequest request,
        CancellationToken cancellationToken)
    {
        return await RunWithTerminalFactsAsync(async () =>
        {
            var sourceWorkbookPath = context.BinDocumentPath;
            var destinationDirectory = request.DestinationDirectory is null
                ? context.DocumentSourceSetPath
                : ResolvePath(request.WorkingDirectory, request.DestinationDirectory);
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
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exports from an explicit workbook path without project manifest resolution.
    /// </summary>
    /// <param name="request">The export command request containing the required workbook path.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public CommandResult RunExplicit(ExplicitWorkbookExportCommandRequest request)
        => RunExplicitAsync(request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Exports from an explicit workbook path with cooperative cancellation.
    /// </summary>
    /// <param name="request">The export command request containing the required workbook path.</param>
    /// <param name="cancellationToken">Cancels workbook automation before destination mutation.</param>
    /// <returns>The command result describing the export operation or validation error.</returns>
    public async Task<CommandResult> RunExplicitAsync(
        ExplicitWorkbookExportCommandRequest request,
        CancellationToken cancellationToken)
    {
        return await RunWithTerminalFactsAsync(async () =>
        {
            var sourceWorkbookPath = ResolvePath(request.WorkingDirectory, request.SourceWorkbook);
            var destinationDirectory = request.DestinationDirectory is null
                ? Path.GetFullPath(request.WorkingDirectory)
                : ResolvePath(request.WorkingDirectory, request.DestinationDirectory);
            var cleanDestination = request.DestinationDirectory is not null;

            return await RunCoreAsync(
                    sourceWorkbookPath,
                    destinationDirectory,
                    cleanDestination,
                    WorkbookAutomationTimeouts.Default,
                    cancellationToken)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
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

        var warning = await ExportThroughStagingAsync(
                sourceWorkbookPath,
                destinationDirectory,
                cleanDestination,
                automationTimeouts,
                cancellationToken)
            .ConfigureAwait(false);

        return CommandResult.Success($"Exported {sourceWorkbookPath} to {destinationDirectory}{Environment.NewLine}")
            with { StandardError = warning };
    }

    private async Task<string> ExportThroughStagingAsync(
        string sourceWorkbookPath,
        string destinationDirectory,
        bool cleanDestination,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        using var staging = WorkbookExportStaging.Create(ownershipFactory);
        Exception? failure = null;
        var additionalEvidence = string.Empty;
        try
        {
            await workbookModuleExporter.ExportModulesAsync(sourceWorkbookPath, staging,
                automationTimeouts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }

        if (failure is not null)
        {
            WorkbookAutomationFailureClassifier.TryClassify(failure, out var facts);
            if (!facts.ProcessReleaseProven)
                throw new ExportScratchFailureException(failure,
                    $"Export staging was retained because owned Excel process release could not be proved: {staging.Path}{Environment.NewLine}");
        }

        try { staging.CompleteProduction(); }
        catch (Exception captureError)
        {
            if (failure is null) failure = captureError;
            else additionalEvidence = captureError.Message + Environment.NewLine;
        }
        if (failure is null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                staging.ProveUnchanged();
                destinationTransaction.Apply(staging.Path, destinationDirectory, removeStaleSources: cleanDestination);
            }
            catch (Exception error) { failure = error; }
        }

        var cleanup = staging.Cleanup();
        var cleanupText = cleanup.Status == InvocationScratchCleanupStatus.Removed ? string.Empty
            : $"Export staging could not be removed ({cleanup.Status}). Retained absolute paths: {string.Join(", ", cleanup.RetainedPaths)}. Inspect these paths and remove only obsolete staging after confirming Excel has exited.{Environment.NewLine}";
        if (failure is not null)
        {
            if (additionalEvidence.Length > 0 || cleanupText.Length > 0)
                throw new ExportScratchFailureException(failure, additionalEvidence + cleanupText);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        // Destination commitment is authoritative even if cleanup retains non-authoritative staging.
        return cleanupText.Length == 0 ? string.Empty : "Warning: " + cleanupText;
    }

    private static string ResolvePath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));

    private static async Task<CommandResult> RunWithTerminalFactsAsync(
        Func<Task<CommandResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ExportScratchFailureException ex)
        {
            var result = await RunWithTerminalFactsAsync(
                () => Task.FromException<CommandResult>(ex.PrimaryError), cancellationToken).ConfigureAwait(false);
            return result with { StandardError = result.StandardError + ex.CleanupEvidence };
        }
        catch (Exception ex) when (WorkbookAutomationFailureClassifier.TryClassify(
            ex, out var facts, cancellationToken.IsCancellationRequested))
        {
            var cancellation = facts.Failures.FirstOrDefault(failure =>
                failure.Error is WorkbookAutomationCanceledException);
            if (facts.PrimaryFailure!.Category == WorkbookAutomationFailureCategory.Cancellation &&
                cancellation is null && !cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            var result = facts.PrimaryFailure!.Category == WorkbookAutomationFailureCategory.Cancellation
                ? CommandResult.Cancelled(ex.Message)
                : CommandResult.UsageError(ex.Message);
            return facts.ProcessReleaseProven ? result : result.MarkOwnedProcessReleaseUnproven();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            WorkbookAutomationFailureClassifier.TryClassify(ex, out var facts);
            var result = CommandResult.UsageError(ex.Message);
            return facts.ProcessReleaseProven ? result : result.MarkOwnedProcessReleaseUnproven();
        }
    }
}

internal sealed class ExportScratchFailureException(Exception primary, string cleanupEvidence)
    : Exception(primary.Message + " " + cleanupEvidence, primary)
{
    internal Exception PrimaryError => InnerException!;
    internal string CleanupEvidence => cleanupEvidence;
}
