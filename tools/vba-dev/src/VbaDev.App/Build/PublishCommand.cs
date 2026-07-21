using VbaDev.App.Cli;
using VbaDev.App.Projects;

namespace VbaDev.App.Build;

/// <summary>
/// Publishes a workbook-backed document from its template and publishable source files.
/// </summary>
public sealed class PublishCommand
{
    private readonly WorkbookOutputCommand outputCommand;

    /// <summary>
    /// Creates the publish command.
    /// </summary>
    /// <param name="outputCommand">The shared workbook output command implementation.</param>
    public PublishCommand(WorkbookOutputCommand outputCommand)
    {
        this.outputCommand = outputCommand;
    }

    /// <summary>
    /// Generates the document's publish workbook while excluding test-only sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The command result describing the published workbook or any user-facing failure.</returns>
    public CommandResult Run(ResolvedProjectContext context)
        => outputCommand.Run(context, WorkbookOutputProfile.Publish);
}
