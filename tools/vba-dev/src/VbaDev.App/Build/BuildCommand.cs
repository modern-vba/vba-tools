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
    public BuildCommand(
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
        => outputCommand.Run(context, WorkbookOutputProfile.Build);

    /// <summary>
    /// Generates the document's bin workbook with cooperative invocation cancellation.
    /// </summary>
    public Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => outputCommand.RunAsync(
            context,
            WorkbookOutputProfile.Build,
            cancellationToken);

    /// <summary>
    /// Generates the document's bin workbook from an already planned immutable source list.
    /// </summary>
    internal CommandResult Run(
        ResolvedProjectContext context,
        IReadOnlyList<VbaSourceFile> sourceFiles)
        => outputCommand.Run(context, WorkbookOutputProfile.Build, sourceFiles);

    internal Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        CancellationToken cancellationToken)
        => outputCommand.RunAsync(
            context,
            WorkbookOutputProfile.Build,
            sourceFiles,
            cancellationToken);

    /// <summary>
    /// Generates a caller-selected workbook from a complete caller-owned source snapshot.
    /// </summary>
    public Task<CommandResult> RunSnapshotAsync(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        BuildSourceSnapshotValidatedPaths? validatedPaths = null;
        BuildSourceSnapshotValidatedPaths ResolveValidatedPaths()
            => validatedPaths ??= snapshotOutputSafetyValidator.Validate(
                context,
                sourceSnapshotPath,
                outputPath);

        return outputCommand.RunWithOwnedSourceAsync(
            context,
            WorkbookOutputProfile.Build,
            () => snapshotCaptureFactory.Create(
                ResolveValidatedPaths().SourceSnapshotPath,
                cancellationToken),
            () => ResolveValidatedPaths().OutputPath,
            cancellationToken);
    }

    internal Task<CommandResult> RunCapturedSnapshotAsync(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var validatedPaths = snapshotOutputSafetyValidator.Validate(
            context,
            sourceSnapshotPath,
            outputPath);
        return outputCommand.RunWithOwnedSourceAsync(
            context,
            WorkbookOutputProfile.Build,
            () => new BorrowedWorkbookGenerationSourceInput(sourceFiles),
            () => validatedPaths.OutputPath,
            cancellationToken);
    }
}
