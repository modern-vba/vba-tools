namespace VbaDev.App.Cli;

public sealed record ToolingCommandContract(
    string Name,
    string Description,
    string UsageSuffix,
    IReadOnlyList<CommandOptionDefinition> Options,
    int DisplayOrder,
    ToolingCommandContextPolicy ContextPolicy,
    string OutputSchemaVersion = "1.0");

public sealed record ToolingCommandHandler(
    string CommandName,
    Func<ToolingCommandInvocation, CommandResult> Execute);

public sealed record ToolingCommandContextPolicy
{
    private ToolingCommandContextPolicy(ProjectResolutionMode mode)
    {
        Mode = mode;
    }

    public ProjectResolutionMode Mode { get; }

    public string? ContextFreeOption { get; private init; }

    public IReadOnlyList<string> RejectedOptionsWhenContextFree { get; private init; } = [];

    public static ToolingCommandContextPolicy None { get; } = new(ProjectResolutionMode.None);

    public static ToolingCommandContextPolicy ProjectRequired { get; } = new(ProjectResolutionMode.ProjectRequired);

    public static ToolingCommandContextPolicy ProjectOptional { get; } = new(ProjectResolutionMode.ProjectOptional);

    public static ToolingCommandContextPolicy DocumentRequired { get; } = new(ProjectResolutionMode.DocumentRequired);

    public static ToolingCommandContextPolicy DocumentUnlessOptionPresent(
        string optionName,
        params string[] rejectedOptions)
        => new(ProjectResolutionMode.DocumentUnlessOptionPresent)
        {
            ContextFreeOption = optionName,
            RejectedOptionsWhenContextFree = rejectedOptions
        };
}

public enum ProjectResolutionMode
{
    None,
    ProjectRequired,
    ProjectOptional,
    DocumentRequired,
    DocumentUnlessOptionPresent
}
