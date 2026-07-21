using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>
/// Builds a workbook-backed document from its template and full document source set.
/// </summary>
public sealed class BuildCommand
{
    private readonly WorkbookOutputCommand outputCommand;

    /// <summary>
    /// Creates the build command.
    /// </summary>
    /// <param name="outputCommand">The shared workbook output command implementation.</param>
    public BuildCommand(WorkbookOutputCommand outputCommand)
    {
        this.outputCommand = outputCommand;
    }

    /// <summary>
    /// Generates the document's bin workbook and imports all build source files.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The command result describing the generated workbook or any user-facing failure.</returns>
    public CommandResult Run(ResolvedProjectContext context)
        => outputCommand.Run(context, WorkbookOutputProfile.Build);

    internal Task<CommandResult> RunAsync(
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
}
