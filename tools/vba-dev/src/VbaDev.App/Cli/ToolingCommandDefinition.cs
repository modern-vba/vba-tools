namespace VbaDev.App.Cli;

public sealed record ToolingCommandDefinition(
    string Name,
    string Description,
    string UsageSuffix,
    IReadOnlyList<CommandOptionDefinition> Options,
    int DisplayOrder,
    ProjectResolutionMode ProjectResolutionMode,
    Func<ToolingCommandInvocation, CommandResult> Execute,
    string OutputSchemaVersion = "1.0");

public enum ProjectResolutionMode
{
    None,
    ProjectRequired,
    ProjectOptional,
    DocumentRequired,
    ExplicitWorkbookOrDocumentRequired
}
