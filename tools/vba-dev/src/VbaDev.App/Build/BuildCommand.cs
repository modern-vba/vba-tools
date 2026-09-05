using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Builds a workbook-backed document from its template and full document source set.
/// </summary>
public sealed class BuildCommand
{
    private readonly WorkbookOutputCommand outputCommand;
    private readonly BuildSourceSnapshotCaptureFactory snapshotCaptureFactory;
    private readonly BuildSourceSnapshotOutputSafetyValidator snapshotOutputSafetyValidator;

    /// <summary>
    /// Creates the build command.
    /// </summary>
    /// <param name="outputCommand">The shared workbook output command implementation.</param>
    internal BuildCommand(
        WorkbookOutputCommand outputCommand,
        IFileSystemPathIdentityResolver pathIdentityResolver)
        : this(
            outputCommand,
            new BuildSourceSnapshotCaptureFactory(),
            new BuildSourceSnapshotOutputSafetyValidator(pathIdentityResolver))
    {
    }

    internal BuildCommand(
        WorkbookOutputCommand outputCommand,
        BuildSourceSnapshotCaptureFactory snapshotCaptureFactory,
        BuildSourceSnapshotOutputSafetyValidator snapshotOutputSafetyValidator)
    {
        this.outputCommand = outputCommand;
        this.snapshotCaptureFactory = snapshotCaptureFactory;
        this.snapshotOutputSafetyValidator = snapshotOutputSafetyValidator;
    }

    /// <summary>
    /// Generates the document's bin workbook and imports all build source files.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The command result describing the generated workbook or any user-facing failure.</returns>
    public CommandResult Run(ResolvedProjectContext context)
        => outputCommand.RunBuild(context);

    /// <summary>
    /// Generates the document's bin workbook with cooperative invocation cancellation.
    /// </summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => outputCommand.RunBuildAsync(context, cancellationToken);

    /// <summary>
    /// Generates a caller-selected workbook from a complete caller-owned source snapshot.
    /// </summary>
    public Task<CommandResult> RunSnapshotAsync(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        string outputPath,
        CancellationToken cancellationToken)
        => outputCommand.RunSnapshotBuildAsync(
            context,
            sourceSnapshotPath,
            outputPath,
            snapshotCaptureFactory,
            snapshotOutputSafetyValidator,
            cancellationToken);

    internal Task<SourceSnapshotBuildCommandResult> RunSnapshotIntentAsync(
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
