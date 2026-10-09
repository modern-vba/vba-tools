using VbaDev.App.Cli;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Builds a workbook-backed document from saved source files into its source workbook.
/// </summary>
public sealed class BuildCommand
{
    private readonly WorkbookOutputCommand outputCommand;
    private readonly SourceWorkbookBuildCommand? sourceWorkbookBuildCommand;
    private readonly BuildSourceSnapshotCaptureFactory snapshotCaptureFactory;
    private readonly BuildSourceSnapshotOutputSafetyValidator snapshotOutputSafetyValidator;

    /// <summary>
    /// Creates the build command.
    /// </summary>
    /// <param name="outputCommand">The shared workbook output command implementation.</param>
    internal BuildCommand(
        WorkbookOutputCommand outputCommand,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        IExactFileSystemObjectOwnershipFactory ownershipFactory)
        : this(
            outputCommand,
            new BuildSourceSnapshotCaptureFactory(ownershipFactory),
            new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver),
            null)
    {
    }

    internal BuildCommand(
        WorkbookOutputCommand outputCommand,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        SourceWorkbookBuildCommand sourceWorkbookBuildCommand)
        : this(
            outputCommand,
            new BuildSourceSnapshotCaptureFactory(ownershipFactory),
            new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver),
            sourceWorkbookBuildCommand)
    {
    }

    internal BuildCommand(
        WorkbookOutputCommand outputCommand,
        BuildSourceSnapshotCaptureFactory snapshotCaptureFactory,
        BuildSourceSnapshotOutputSafetyValidator snapshotOutputSafetyValidator)
        : this(outputCommand, snapshotCaptureFactory, snapshotOutputSafetyValidator, null)
    {
    }

    private BuildCommand(
        WorkbookOutputCommand outputCommand,
        BuildSourceSnapshotCaptureFactory snapshotCaptureFactory,
        BuildSourceSnapshotOutputSafetyValidator snapshotOutputSafetyValidator,
        SourceWorkbookBuildCommand? sourceWorkbookBuildCommand)
    {
        this.outputCommand = outputCommand;
        this.sourceWorkbookBuildCommand = sourceWorkbookBuildCommand;
        this.snapshotCaptureFactory = snapshotCaptureFactory;
        this.snapshotOutputSafetyValidator = snapshotOutputSafetyValidator;
    }

    /// <summary>
    /// Imports saved build sources into the selected source workbook and saves it in place.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The command result describing the generated workbook or any user-facing failure.</returns>
    public CommandResult Run(ResolvedProjectContext context)
        => RunAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Builds the selected source workbook with cooperative invocation cancellation.
    /// </summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => RunAsync(context, null, cancellationToken);

    /// <summary>Builds the source workbook, requesting consent for already-open unsaved changes.</summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        CancellationToken cancellationToken)
        => sourceWorkbookBuildCommand is null
            ? outputCommand.RunBuildAsync(context, cancellationToken)
            : outputCommand.RunSourceBuildAsync(context, sourceWorkbookBuildCommand,
                confirmUnsavedChanges, cancellationToken);

    /// <summary>
    /// Generates a caller-selected workbook from a complete caller-owned source snapshot.
    /// </summary>
    public Task<CommandResult> RunSnapshotAsync(
        ResolvedProjectContext context,
        SourceSnapshotBuildCommandRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return outputCommand.RunSnapshotBuildAsync(
            context,
            Path.GetFullPath(
                request.SourceSnapshotDirectory,
                request.WorkingDirectory),
            Path.GetFullPath(
                request.OutputWorkbook,
                request.WorkingDirectory),
            snapshotCaptureFactory,
            snapshotOutputSafetyValidator,
            cancellationToken);
    }

    internal Task<TestWorkbookBuildCommandResult> RunTestBuildIntentAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => outputCommand.RunTestBuildIntentAsync(context, cancellationToken);

    internal Task<TestWorkbookBuildCommandResult> RunSnapshotIntentAsync(
        ResolvedProjectContext context,
        BuildSourceSnapshotCapture sourceCapture,
        string outputPath,
        CancellationToken cancellationToken)
    {
        return outputCommand.RunSnapshotIntentAsync(
            context,
            sourceCapture,
            outputPath,
            cancellationToken);
    }
}
