using System.CommandLine;
using System.CommandLine.Parsing;
using AppCommandResult = VbaDev.App.Cli.CommandResult;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns the host-event command group on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevHostEventCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevHostEventCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        this.composition = composition;

        HostEventCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "host-event",
            "Inspect generic intrinsic host Events.");
        ListCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            HostEventCommand,
            "list",
            "List the environment's generic UserForm Event catalog.",
            "host-event list",
            "1.0",
            capabilityRegistrations);
        ListFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Host Event catalog output format.",
            "text|json",
            ["text", "json"],
            "-f");
        ListCommand.Add(ListFormatOption);

        ListIntentBinding = grammarFailureRules.BindIntent<
            VbaDevHostEventListCommandIntent>(
            ListCommand,
            BindListIntent);
        ListCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunListAsync(parseResult, cancellationToken).ConfigureAwait(false)));
    }

    internal Command HostEventCommand { get; }

    internal Command ListCommand { get; }

    internal Option<string> ListFormatOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevHostEventListCommandIntent>
        ListIntentBinding { get; }

    internal static VbaDevHostEventCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        return new VbaDevHostEventCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations);
    }

    private VbaDevGrammarIntentBindResult<VbaDevHostEventListCommandIntent> BindListIntent(
        ParseResult parseResult)
    {
        if (parseResult.GetResult(ListFormatOption) is not { Implicit: false })
        {
            return VbaDevGrammarIntentBindResult<VbaDevHostEventListCommandIntent>.Bound(
                new VbaDevHostEventListCommandIntent.Text());
        }

        VbaDevHostEventListCommandIntent? intent = parseResult.GetValue(ListFormatOption) switch
        {
            "text" => new VbaDevHostEventListCommandIntent.Text(),
            "json" => new VbaDevHostEventListCommandIntent.Json(),
            _ => null
        };
        return intent is null
            ? VbaDevGrammarIntentBindResult<VbaDevHostEventListCommandIntent>.Unbound
            : VbaDevGrammarIntentBindResult<VbaDevHostEventListCommandIntent>.Bound(intent);
    }

    private Task<AppCommandResult> RunListAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = ListIntentBinding.GetRequiredIntent(parseResult);
        var format = intent switch
        {
            VbaDevHostEventListCommandIntent.Text => "text",
            VbaDevHostEventListCommandIntent.Json => "json",
            _ => throw new InvalidOperationException(
                $"Unsupported host-event list intent '{intent.GetType().FullName}'.")
        };
        return composition.HostEventListCommand.RunAsync(format, cancellationToken);
    }
}

internal abstract record VbaDevHostEventListCommandIntent
{
    private VbaDevHostEventListCommandIntent()
    {
    }

    internal sealed record Text : VbaDevHostEventListCommandIntent;

    internal sealed record Json : VbaDevHostEventListCommandIntent;
}
