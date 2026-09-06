using System.CommandLine;
using System.CommandLine.Parsing;
using AppCommandResult = VbaDev.App.Cli.CommandResult;
using VbaDev.App.Diagnostics;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns the check and doctor leaves on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevInspectionCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevInspectionCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        this.composition = composition;

        CheckCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "check",
            "Validate deterministic project facts without starting Excel.");
        CheckProjectOption = VbaDevCommandGrammar.AddProjectOption(CheckCommand);
        CheckIntentBinding = grammarFailureRules.BindIntent<VbaDevCheckCommandIntent>(
            CheckCommand,
            parseResult => VbaDevGrammarIntentBindResult<VbaDevCheckCommandIntent>.Bound(
                new VbaDevCheckCommandIntent(parseResult.GetValue(CheckProjectOption))));
        CheckCommand.SetAction(parseResult => VbaDevCommandGrammar.WriteCommandResult(
            parseResult,
            RunCheck(parseResult)));

        DoctorCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "doctor",
            "Check project and machine prerequisites.",
            "doctor",
            "1.0",
            capabilityRegistrations);
        DoctorProjectOption = VbaDevCommandGrammar.AddProjectOption(DoctorCommand);
        DoctorScopeOption = VbaDevCommandGrammar.CreateStringOption(
            "--scope",
            "Diagnostic scope.",
            "project|environment",
            ["project", "environment"]);
        DoctorFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Doctor output format.",
            "text|json",
            ["text", "json"],
            "-f");
        DoctorCommand.Add(DoctorScopeOption);
        DoctorCommand.Add(DoctorFormatOption);

        grammarFailureRules.ConflictsWhenValue(
            DoctorCommand,
            DoctorScopeOption,
            "environment",
            DoctorProjectOption);
        DoctorIntentBinding = grammarFailureRules.BindIntent<VbaDevDoctorCommandIntent>(
            DoctorCommand,
            BindDoctorIntent);
        DoctorCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunDoctorAsync(parseResult, cancellationToken).ConfigureAwait(false)));
    }

    internal Command CheckCommand { get; }

    internal Option<string> CheckProjectOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevCheckCommandIntent> CheckIntentBinding { get; }

    internal Command DoctorCommand { get; }

    internal Option<string> DoctorProjectOption { get; }

    internal Option<string> DoctorScopeOption { get; }

    internal Option<string> DoctorFormatOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevDoctorCommandIntent>
        DoctorIntentBinding { get; }

    internal static VbaDevInspectionCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        return new VbaDevInspectionCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations);
    }

    private VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent> BindDoctorIntent(
        ParseResult parseResult)
    {
        var format = BindDoctorFormat(parseResult);
        if (format is null)
        {
            return VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent>.Unbound;
        }

        if (parseResult.GetResult(DoctorScopeOption) is not { Implicit: false })
        {
            return VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent>.Bound(
                new VbaDevDoctorCommandIntent.Project(
                    parseResult.GetValue(DoctorProjectOption),
                    format.Value));
        }

        return parseResult.GetValue(DoctorScopeOption) switch
        {
            "project" => VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent>.Bound(
                new VbaDevDoctorCommandIntent.Project(
                    parseResult.GetValue(DoctorProjectOption),
                    format.Value)),
            "environment" when parseResult.GetResult(DoctorProjectOption) is not
                { Implicit: false } =>
                VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent>.Bound(
                    new VbaDevDoctorCommandIntent.Environment(format.Value)),
            _ => VbaDevGrammarIntentBindResult<VbaDevDoctorCommandIntent>.Unbound
        };
    }

    private DoctorOutputFormat? BindDoctorFormat(ParseResult parseResult)
    {
        if (parseResult.GetResult(DoctorFormatOption) is not { Implicit: false })
        {
            return DoctorOutputFormat.Text;
        }

        return parseResult.GetValue(DoctorFormatOption) switch
        {
            "text" => DoctorOutputFormat.Text,
            "json" => DoctorOutputFormat.Json,
            _ => null
        };
    }

    private AppCommandResult RunCheck(ParseResult parseResult)
    {
        var intent = CheckIntentBinding.GetRequiredIntent(parseResult);
        return composition.StaticProjectCheckCommand.Run(
            new StaticProjectCheckRequest(
                intent.ProjectRoot,
                composition.WorkingDirectory));
    }

    private Task<AppCommandResult> RunDoctorAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = DoctorIntentBinding.GetRequiredIntent(parseResult);
        var request = intent switch
        {
            VbaDevDoctorCommandIntent.Project project => new DoctorCommandRequest(
                project.ProjectRoot,
                composition.WorkingDirectory,
                DoctorScope.Project,
                project.Format),
            VbaDevDoctorCommandIntent.Environment environment => new DoctorCommandRequest(
                null,
                composition.WorkingDirectory,
                DoctorScope.Environment,
                environment.Format),
            _ => throw new InvalidOperationException(
                $"Unsupported doctor intent '{intent.GetType().FullName}'.")
        };
        return composition.DoctorCommand.RunAsync(request, cancellationToken);
    }
}

internal sealed record VbaDevCheckCommandIntent(string? ProjectRoot);

internal abstract record VbaDevDoctorCommandIntent
{
    private VbaDevDoctorCommandIntent(DoctorOutputFormat format)
    {
        Format = format;
    }

    internal DoctorOutputFormat Format { get; }

    internal sealed record Project : VbaDevDoctorCommandIntent
    {
        internal Project(string? projectRoot, DoctorOutputFormat format)
            : base(format)
        {
            ProjectRoot = projectRoot;
        }

        internal string? ProjectRoot { get; }
    }

    internal sealed record Environment : VbaDevDoctorCommandIntent
    {
        internal Environment(DoctorOutputFormat format)
            : base(format)
        {
        }
    }
}
