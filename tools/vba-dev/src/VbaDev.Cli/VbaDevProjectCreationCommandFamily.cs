using System.CommandLine;
using System.CommandLine.Parsing;
using AppCommandResult = VbaDev.App.Cli.CommandResult;
using VbaDev.App.Projects;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns project-creation commands on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevProjectCreationCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevProjectCreationCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        this.composition = composition;

        NewCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "new",
            "Create a VBA project.");
        ExcelCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            NewCommand,
            "excel",
            "Create an Excel workbook-backed VBA project.",
            "new excel",
            "1.0",
            capabilityRegistrations);
        NameOption = VbaDevCommandGrammar.CreateStringOption(
            "--name",
            "Project and document base name.",
            "name",
            aliases: "-n");
        OutputOption = VbaDevCommandGrammar.CreateStringOption(
            "--output",
            "Project root output directory.",
            "dir",
            aliases: "-o");
        FormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Project creation receipt format.",
            "text|json",
            ["text", "json"],
            "-f");
        ExcelCommand.Add(NameOption);
        ExcelCommand.Add(OutputOption);
        ExcelCommand.Add(FormatOption);

        ExcelIntentBinding = grammarFailureRules.BindIntent<VbaDevNewExcelCommandIntent>(
            ExcelCommand,
            BindExcelIntent);
        ExcelCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunExcelAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        commandFamilyOwnership.Register(this, ExcelCommand);
    }

    internal Command NewCommand { get; }

    internal Command ExcelCommand { get; }

    internal Option<string> NameOption { get; }

    internal Option<string> OutputOption { get; }

    internal Option<string> FormatOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevNewExcelCommandIntent>
        ExcelIntentBinding { get; }

    internal static VbaDevProjectCreationCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        ArgumentNullException.ThrowIfNull(commandFamilyOwnership);
        return new VbaDevProjectCreationCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations,
            commandFamilyOwnership);
    }

    private VbaDevGrammarIntentBindResult<VbaDevNewExcelCommandIntent> BindExcelIntent(
        ParseResult parseResult)
    {
        var projectName = BindValue(parseResult, NameOption);
        var outputDirectory = BindValue(parseResult, OutputOption);
        if (parseResult.GetResult(FormatOption) is not { Implicit: false })
        {
            return VbaDevGrammarIntentBindResult<VbaDevNewExcelCommandIntent>.Bound(
                new VbaDevNewExcelCommandIntent(
                    projectName,
                    outputDirectory,
                    new VbaDevProjectCreationOutputFormat.Text()));
        }

        VbaDevProjectCreationOutputFormat? format = parseResult.GetValue(FormatOption) switch
        {
            "text" => new VbaDevProjectCreationOutputFormat.Text(),
            "json" => new VbaDevProjectCreationOutputFormat.Json(),
            _ => null
        };
        return format is null
            ? VbaDevGrammarIntentBindResult<VbaDevNewExcelCommandIntent>.Unbound
            : VbaDevGrammarIntentBindResult<VbaDevNewExcelCommandIntent>.Bound(
                new VbaDevNewExcelCommandIntent(projectName, outputDirectory, format));
    }

    private Task<AppCommandResult> RunExcelAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = ExcelIntentBinding.GetRequiredIntent(parseResult);
        var (projectName, projectNameSpecified) = ToRequestValue(intent.ProjectName);
        var (outputDirectory, outputDirectorySpecified) = ToRequestValue(
            intent.OutputDirectory);
        var format = intent.Format switch
        {
            VbaDevProjectCreationOutputFormat.Text => "text",
            VbaDevProjectCreationOutputFormat.Json => "json",
            _ => throw new InvalidOperationException(
                $"Unsupported project-creation format '{intent.Format.GetType().FullName}'.")
        };
        return composition.NewProjectCommand.RunAsync(
            new NewProjectCommandRequest(
                projectName,
                null,
                outputDirectory,
                composition.WorkingDirectory,
                projectNameSpecified,
                outputDirectorySpecified,
                format),
            cancellationToken);
    }

    private static VbaDevProjectCreationValue BindValue(
        ParseResult parseResult,
        Option<string> option)
        => parseResult.GetResult(option) is { Implicit: false }
            ? new VbaDevProjectCreationValue.Specified(
                parseResult.GetValue(option) ?? string.Empty)
            : new VbaDevProjectCreationValue.Omitted();

    private static (string? Value, bool Specified) ToRequestValue(
        VbaDevProjectCreationValue value)
        => value switch
        {
            VbaDevProjectCreationValue.Omitted => (null, false),
            VbaDevProjectCreationValue.Specified specified => (specified.Value, true),
            _ => throw new InvalidOperationException(
                $"Unsupported project-creation value '{value.GetType().FullName}'.")
        };
}

internal sealed record VbaDevNewExcelCommandIntent(
    VbaDevProjectCreationValue ProjectName,
    VbaDevProjectCreationValue OutputDirectory,
    VbaDevProjectCreationOutputFormat Format);

internal abstract record VbaDevProjectCreationValue
{
    private VbaDevProjectCreationValue()
    {
    }

    internal sealed record Omitted : VbaDevProjectCreationValue;

    internal sealed record Specified(string Value) : VbaDevProjectCreationValue;
}

internal abstract record VbaDevProjectCreationOutputFormat
{
    private VbaDevProjectCreationOutputFormat()
    {
    }

    internal sealed record Text : VbaDevProjectCreationOutputFormat;

    internal sealed record Json : VbaDevProjectCreationOutputFormat;
}
