using System.CommandLine;
using AppCommandResult = VbaDev.App.Cli.CommandResult;
using VbaDev.App.Projects;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns the common-module command group on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevCommonModuleCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevCommonModuleCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        this.composition = composition;

        CommonModuleCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "common-module",
            "Manage CommonModules entries.");
        AddCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            CommonModuleCommand,
            "add",
            "Copy CommonModules entries into the selected document source set.",
            "common-module add",
            "1.0",
            capabilityRegistrations);
        var addProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(AddCommand);
        AddProjectOption = addProjectOptions.Project;
        AddDocumentOption = addProjectOptions.Document;
        AddModulesArgument = new Argument<string[]>("modules")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "CommonModules entries to add."
        };
        AddCommand.Add(AddModulesArgument);
        AddForceOption = new Option<bool>("--force")
        {
            Description = "Overwrite conflicting source files."
        };
        AddCommand.Add(AddForceOption);
        AddFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "CommonModules mutation output format.",
            "text|json",
            ["text", "json"],
            "-f");
        AddCommand.Add(AddFormatOption);

        ListCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            CommonModuleCommand,
            "list",
            "List CommonModules entries for the selected document.",
            "common-module list",
            "1.0",
            capabilityRegistrations);
        var listProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(ListCommand);
        ListProjectOption = listProjectOptions.Project;
        ListDocumentOption = listProjectOptions.Document;
        ListFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "CommonModules output format.",
            "text|json",
            ["text", "json"],
            "-f");
        ListCommand.Add(ListFormatOption);

        UpdateCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            CommonModuleCommand,
            "update",
            "Update installed CommonModules entries.",
            "common-module update",
            "1.0",
            capabilityRegistrations);
        UpdateProjectOption = VbaDevCommandGrammar.AddProjectOption(UpdateCommand);
        UpdateFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "CommonModules mutation output format.",
            "text|json",
            ["text", "json"],
            "-f");
        UpdateCommand.Add(UpdateFormatOption);

        grammarFailureRules.RequireNonEmpty(AddModulesArgument);
        AddIntentBinding = grammarFailureRules.BindIntent<
            VbaDevCommonModuleAddCommandIntent>(
            AddCommand,
            BindAddIntent);
        ListIntentBinding = grammarFailureRules.BindIntent<
            VbaDevCommonModuleListCommandIntent>(
            ListCommand,
            BindListIntent);
        UpdateIntentBinding = grammarFailureRules.BindIntent<
            VbaDevCommonModuleUpdateCommandIntent>(
            UpdateCommand,
            BindUpdateIntent);
        AddCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunAddAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        ListCommand.SetAction(parseResult =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                RunList(parseResult)));
        UpdateCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunUpdateAsync(parseResult, cancellationToken).ConfigureAwait(false)));
    }

    internal Command CommonModuleCommand { get; }

    internal Command AddCommand { get; }

    internal Option<string> AddProjectOption { get; }

    internal Option<string> AddDocumentOption { get; }

    internal Argument<string[]> AddModulesArgument { get; }

    internal Option<bool> AddForceOption { get; }

    internal Option<string> AddFormatOption { get; }

    internal Command ListCommand { get; }

    internal Option<string> ListProjectOption { get; }

    internal Option<string> ListDocumentOption { get; }

    internal Option<string> ListFormatOption { get; }

    internal Command UpdateCommand { get; }

    internal Option<string> UpdateProjectOption { get; }

    internal Option<string> UpdateFormatOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevCommonModuleAddCommandIntent>
        AddIntentBinding { get; }

    internal VbaDevGrammarIntentBinding<VbaDevCommonModuleListCommandIntent>
        ListIntentBinding { get; }

    internal VbaDevGrammarIntentBinding<VbaDevCommonModuleUpdateCommandIntent>
        UpdateIntentBinding { get; }

    internal static VbaDevCommonModuleCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        return new VbaDevCommonModuleCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations);
    }

    private VbaDevGrammarIntentBindResult<VbaDevCommonModuleAddCommandIntent> BindAddIntent(
        ParseResult parseResult)
    {
        var moduleNames = parseResult.GetValue(AddModulesArgument);
        if (moduleNames is null ||
            moduleNames.Length == 0 ||
            moduleNames.Any(string.IsNullOrWhiteSpace))
        {
            return VbaDevGrammarIntentBindResult<VbaDevCommonModuleAddCommandIntent>.Unbound;
        }

        var frozenModuleNames = Array.AsReadOnly(moduleNames.ToArray());
        var projectRoot = parseResult.GetValue(AddProjectOption);
        var documentName = parseResult.GetValue(AddDocumentOption);
        var format = parseResult.GetValue(AddFormatOption) ?? "text";
        VbaDevCommonModuleAddCommandIntent intent = parseResult.GetValue(AddForceOption)
            ? new VbaDevCommonModuleAddCommandIntent.Forced(
                projectRoot,
                documentName,
                frozenModuleNames,
                format)
            : new VbaDevCommonModuleAddCommandIntent.Ordinary(
                projectRoot,
                documentName,
                frozenModuleNames,
                format);
        return VbaDevGrammarIntentBindResult<VbaDevCommonModuleAddCommandIntent>.Bound(intent);
    }

    private VbaDevGrammarIntentBindResult<VbaDevCommonModuleListCommandIntent> BindListIntent(
        ParseResult parseResult)
        => VbaDevGrammarIntentBindResult<VbaDevCommonModuleListCommandIntent>.Bound(
            new VbaDevCommonModuleListCommandIntent(
                parseResult.GetValue(ListProjectOption),
                parseResult.GetValue(ListDocumentOption),
                parseResult.GetValue(ListFormatOption) ?? "text"));

    private VbaDevGrammarIntentBindResult<VbaDevCommonModuleUpdateCommandIntent> BindUpdateIntent(
        ParseResult parseResult)
        => VbaDevGrammarIntentBindResult<VbaDevCommonModuleUpdateCommandIntent>.Bound(
            new VbaDevCommonModuleUpdateCommandIntent(
                parseResult.GetValue(UpdateProjectOption),
                parseResult.GetValue(UpdateFormatOption) ?? "text"));

    private Task<AppCommandResult> RunAddAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = AddIntentBinding.GetRequiredIntent(parseResult);
        var force = intent switch
        {
            VbaDevCommonModuleAddCommandIntent.Ordinary => false,
            VbaDevCommonModuleAddCommandIntent.Forced => true,
            _ => throw new InvalidOperationException("Unsupported CommonModules add intent.")
        };
        return VbaDevCommandGrammar.ResolveDocumentContextAsync(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            (context, operationCancellationToken) => composition.CommonModulesService.AddAsync(
                context,
                intent.ModuleNames,
                force,
                intent.Format,
                operationCancellationToken),
            cancellationToken);
    }

    private AppCommandResult RunList(ParseResult parseResult)
    {
        var intent = ListIntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveDocumentContext(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            context => composition.CommonModulesService.List(context, intent.Format));
    }

    private Task<AppCommandResult> RunUpdateAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = UpdateIntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveProjectAsync(
            composition,
            intent.ProjectRoot,
            (project, operationCancellationToken) => composition.CommonModulesService.UpdateAsync(
                project,
                intent.Format,
                operationCancellationToken),
            cancellationToken);
    }
}

internal abstract record VbaDevCommonModuleAddCommandIntent
{
    private VbaDevCommonModuleAddCommandIntent(
        string? projectRoot,
        string? documentName,
        IReadOnlyList<string> moduleNames,
        string format)
    {
        ProjectRoot = projectRoot;
        DocumentName = documentName;
        ModuleNames = moduleNames;
        Format = format;
    }

    internal string? ProjectRoot { get; }

    internal string? DocumentName { get; }

    internal IReadOnlyList<string> ModuleNames { get; }

    internal string Format { get; }

    internal sealed record Ordinary(
        string? SelectedProjectRoot,
        string? SelectedDocumentName,
        IReadOnlyList<string> SelectedModuleNames,
        string SelectedFormat) : VbaDevCommonModuleAddCommandIntent(
            SelectedProjectRoot,
            SelectedDocumentName,
            SelectedModuleNames,
            SelectedFormat);

    internal sealed record Forced(
        string? SelectedProjectRoot,
        string? SelectedDocumentName,
        IReadOnlyList<string> SelectedModuleNames,
        string SelectedFormat) : VbaDevCommonModuleAddCommandIntent(
            SelectedProjectRoot,
            SelectedDocumentName,
            SelectedModuleNames,
            SelectedFormat);
}

internal sealed record VbaDevCommonModuleListCommandIntent(
    string? ProjectRoot,
    string? DocumentName,
    string Format);

internal sealed record VbaDevCommonModuleUpdateCommandIntent(
    string? ProjectRoot,
    string Format);
