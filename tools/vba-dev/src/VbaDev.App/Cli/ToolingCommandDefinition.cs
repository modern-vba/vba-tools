namespace VbaDev.App.Cli;

/// <summary>
/// Describes a user-facing VbaDev command surface.
/// </summary>
/// <param name="Name">The command name accepted by the command-line parser.</param>
/// <param name="Description">The short help text for the command.</param>
/// <param name="UsageSuffix">The usage suffix displayed after the command name.</param>
/// <param name="Options">The options accepted by the command.</param>
/// <param name="DisplayOrder">The ordering value used in help output.</param>
/// <param name="ContextPolicy">The project or document context required before invoking the command.</param>
/// <param name="OutputSchemaVersion">The stable schema version for machine-readable command output.</param>
public sealed record ToolingCommandContract(
    string Name,
    string Description,
    string UsageSuffix,
    IReadOnlyList<CommandOptionDefinition> Options,
    int DisplayOrder,
    ToolingCommandContextPolicy ContextPolicy,
    string OutputSchemaVersion = "1.0");

/// <summary>
/// Connects a command name to the application function that executes it.
/// </summary>
/// <param name="CommandName">The command name handled by the delegate.</param>
/// <param name="Execute">The delegate invoked after command-line parsing and context resolution.</param>
public sealed record ToolingCommandHandler(
    string CommandName,
    Func<ToolingCommandInvocation, CommandResult> Execute);

/// <summary>
/// Defines how a command obtains project and document context before execution.
/// </summary>
public sealed record ToolingCommandContextPolicy
{
    private ToolingCommandContextPolicy(ProjectResolutionMode mode)
    {
        Mode = mode;
    }

    /// <summary>
    /// Gets the project resolution mode for the command.
    /// </summary>
    public ProjectResolutionMode Mode { get; }

    /// <summary>
    /// Gets the option that allows the command to run without document context.
    /// </summary>
    public string? ContextFreeOption { get; private init; }

    /// <summary>
    /// Gets the options that are rejected when the context-free option is present.
    /// </summary>
    public IReadOnlyList<string> RejectedOptionsWhenContextFree { get; private init; } = [];

    /// <summary>
    /// Gets a policy for commands that do not need project resolution.
    /// </summary>
    public static ToolingCommandContextPolicy None { get; } = new(ProjectResolutionMode.None);

    /// <summary>
    /// Gets a policy for commands that require a resolved project but no selected document.
    /// </summary>
    public static ToolingCommandContextPolicy ProjectRequired { get; } = new(ProjectResolutionMode.ProjectRequired);

    /// <summary>
    /// Gets a policy for commands that may use a project when one is available.
    /// </summary>
    public static ToolingCommandContextPolicy ProjectOptional { get; } = new(ProjectResolutionMode.ProjectOptional);

    /// <summary>
    /// Gets a policy for commands that require a resolved project document.
    /// </summary>
    public static ToolingCommandContextPolicy DocumentRequired { get; } = new(ProjectResolutionMode.DocumentRequired);

    /// <summary>
    /// Creates a document-required policy that becomes context-free when a specific option is present.
    /// </summary>
    /// <param name="optionName">The option that bypasses document context resolution.</param>
    /// <param name="rejectedOptions">Options that cannot be combined with the context-free option.</param>
    /// <returns>The conditional document context policy.</returns>
    public static ToolingCommandContextPolicy DocumentUnlessOptionPresent(
        string optionName,
        params string[] rejectedOptions)
        => new(ProjectResolutionMode.DocumentUnlessOptionPresent)
        {
            ContextFreeOption = optionName,
            RejectedOptionsWhenContextFree = rejectedOptions
        };
}

/// <summary>
/// Identifies how much project manifest context a command requires before execution.
/// </summary>
public enum ProjectResolutionMode
{
    /// <summary>
    /// The command runs without resolving a project.
    /// </summary>
    None,

    /// <summary>
    /// The command requires a project manifest but not a document selection.
    /// </summary>
    ProjectRequired,

    /// <summary>
    /// The command can run with or without a resolved project manifest.
    /// </summary>
    ProjectOptional,

    /// <summary>
    /// The command requires both a project manifest and a document selection.
    /// </summary>
    DocumentRequired,

    /// <summary>
    /// The command requires a document unless a designated context-free option is present.
    /// </summary>
    DocumentUnlessOptionPresent
}
