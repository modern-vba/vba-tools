using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;
using AppCommandResult = VbaDev.App.Cli.CommandResult;
using VbaDev.App.Projects;
using VbaDev.Composition;

namespace VbaDev.Cli;

/// <summary>
/// Owns the reference command group on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevReferenceCommandFamily
{
    private readonly RootCommand rootCommand;
    private readonly ToolingApplicationComposition composition;

    private VbaDevReferenceCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        this.rootCommand = rootCommand;
        this.composition = composition;

        ReferenceCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "reference",
            "Manage VBA project references.");
        AddCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            ReferenceCommand,
            "add",
            "Add VBA project references to the selected document manifest.",
            "reference add",
            "1.0",
            capabilityRegistrations);
        var addProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(AddCommand);
        AddProjectOption = addProjectOptions.Project;
        AddDocumentOption = addProjectOptions.Document;
        AddReferencesArgument = new Argument<string[]>("references")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "VBA project reference names to add."
        };
        AddReferencesArgument.CompletionSources.Add(CompleteAdd);
        AddCommand.Add(AddReferencesArgument);
        AddFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Reference mutation output format.",
            "text|json",
            ["text", "json"],
            "-f");
        AddCommand.Add(AddFormatOption);

        ListCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            ReferenceCommand,
            "list",
            "List VBA project references for the selected document.",
            "reference list",
            "1.0",
            capabilityRegistrations);
        var listProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(ListCommand);
        ListProjectOption = listProjectOptions.Project;
        ListDocumentOption = listProjectOptions.Document;
        ListAvailableOption = new Option<bool>("--available")
        {
            Description = "List registered references not selected by the document."
        };
        ListCommand.Add(ListAvailableOption);
        ListNoResolveOption = new Option<bool>("--no-resolve")
        {
            Description = "List the stored document reference selection without resolving references."
        };
        ListCommand.Add(ListNoResolveOption);
        ListFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Reference output format.",
            "text|json",
            ["text", "json"],
            "-f");
        ListCommand.Add(ListFormatOption);

        RemoveCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            ReferenceCommand,
            "remove",
            "Remove VBA project references from the selected document manifest.",
            "reference remove",
            "1.0",
            capabilityRegistrations);
        var removeProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(RemoveCommand);
        RemoveProjectOption = removeProjectOptions.Project;
        RemoveDocumentOption = removeProjectOptions.Document;
        RemoveReferencesArgument = new Argument<string[]>("references")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "VBA project reference names to remove."
        };
        RemoveReferencesArgument.CompletionSources.Add(CompleteRemove);
        RemoveCommand.Add(RemoveReferencesArgument);
        RemoveFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Reference mutation output format.",
            "text|json",
            ["text", "json"],
            "-f");
        RemoveCommand.Add(RemoveFormatOption);

        grammarFailureRules.RequireNonEmpty(AddReferencesArgument);
        grammarFailureRules.RequireNonEmpty(RemoveReferencesArgument);
        grammarFailureRules.Conflicts(
            ListCommand,
            ListAvailableOption,
            ListNoResolveOption);
        AddIntentBinding = grammarFailureRules.BindIntent<
            VbaDevReferenceAddCommandIntent>(
            AddCommand,
            BindAddIntent);
        ListIntentBinding = grammarFailureRules.BindIntent<
            VbaDevReferenceListCommandIntent>(
            ListCommand,
            BindListIntent);
        RemoveIntentBinding = grammarFailureRules.BindIntent<
            VbaDevReferenceRemoveCommandIntent>(
            RemoveCommand,
            BindRemoveIntent);
        AddCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunAddAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        ListCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunListAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        RemoveCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunRemoveAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        commandFamilyOwnership.Register(this, AddCommand);
        commandFamilyOwnership.Register(this, ListCommand);
        commandFamilyOwnership.Register(this, RemoveCommand);
    }

    internal Command ReferenceCommand { get; }

    internal Command AddCommand { get; }

    internal Option<string> AddProjectOption { get; }

    internal Option<string> AddDocumentOption { get; }

    internal Argument<string[]> AddReferencesArgument { get; }

    internal Option<string> AddFormatOption { get; }

    internal Command ListCommand { get; }

    internal Option<string> ListProjectOption { get; }

    internal Option<string> ListDocumentOption { get; }

    internal Option<bool> ListAvailableOption { get; }

    internal Option<bool> ListNoResolveOption { get; }

    internal Option<string> ListFormatOption { get; }

    internal Command RemoveCommand { get; }

    internal Option<string> RemoveProjectOption { get; }

    internal Option<string> RemoveDocumentOption { get; }

    internal Argument<string[]> RemoveReferencesArgument { get; }

    internal Option<string> RemoveFormatOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevReferenceAddCommandIntent>
        AddIntentBinding { get; }

    internal VbaDevGrammarIntentBinding<VbaDevReferenceListCommandIntent>
        ListIntentBinding { get; }

    internal VbaDevGrammarIntentBinding<VbaDevReferenceRemoveCommandIntent>
        RemoveIntentBinding { get; }

    internal static VbaDevReferenceCommandFamily Register(
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
        return new VbaDevReferenceCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations,
            commandFamilyOwnership);
    }

    private VbaDevGrammarIntentBindResult<VbaDevReferenceAddCommandIntent> BindAddIntent(
        ParseResult parseResult)
    {
        var referenceNames = parseResult.GetValue(AddReferencesArgument);
        if (referenceNames is null ||
            referenceNames.Length == 0 ||
            referenceNames.Any(string.IsNullOrWhiteSpace))
        {
            return VbaDevGrammarIntentBindResult<VbaDevReferenceAddCommandIntent>.Unbound;
        }

        return VbaDevGrammarIntentBindResult<VbaDevReferenceAddCommandIntent>.Bound(
            new VbaDevReferenceAddCommandIntent(
                parseResult.GetValue(AddProjectOption),
                parseResult.GetValue(AddDocumentOption),
                Array.AsReadOnly(referenceNames.ToArray()),
                parseResult.GetValue(AddFormatOption) ?? "text"));
    }

    private VbaDevGrammarIntentBindResult<VbaDevReferenceListCommandIntent> BindListIntent(
        ParseResult parseResult)
    {
        var projectRoot = parseResult.GetValue(ListProjectOption);
        var documentName = parseResult.GetValue(ListDocumentOption);
        var format = parseResult.GetValue(ListFormatOption) ?? "text";
        VbaDevReferenceListCommandIntent intent = parseResult.GetValue(ListAvailableOption)
            ? new VbaDevReferenceListCommandIntent.AvailableCatalog(
                projectRoot,
                documentName,
                format)
            : parseResult.GetValue(ListNoResolveOption)
                ? new VbaDevReferenceListCommandIntent.SelectedWithoutResolution(
                    projectRoot,
                    documentName,
                    format)
                : new VbaDevReferenceListCommandIntent.SelectedAndResolved(
                    projectRoot,
                    documentName,
                    format);
        return VbaDevGrammarIntentBindResult<VbaDevReferenceListCommandIntent>.Bound(intent);
    }

    private VbaDevGrammarIntentBindResult<VbaDevReferenceRemoveCommandIntent> BindRemoveIntent(
        ParseResult parseResult)
    {
        var referenceNames = parseResult.GetValue(RemoveReferencesArgument);
        if (referenceNames is null ||
            referenceNames.Length == 0 ||
            referenceNames.Any(string.IsNullOrWhiteSpace))
        {
            return VbaDevGrammarIntentBindResult<VbaDevReferenceRemoveCommandIntent>.Unbound;
        }

        return VbaDevGrammarIntentBindResult<VbaDevReferenceRemoveCommandIntent>.Bound(
            new VbaDevReferenceRemoveCommandIntent(
                parseResult.GetValue(RemoveProjectOption),
                parseResult.GetValue(RemoveDocumentOption),
                Array.AsReadOnly(referenceNames.ToArray()),
                parseResult.GetValue(RemoveFormatOption) ?? "text"));
    }

    private Task<AppCommandResult> RunAddAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = AddIntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveDocumentContextAsync(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            (context, operationCancellationToken) => composition.ReferenceService.AddAsync(
                context,
                intent.ReferenceNames,
                intent.Format,
                operationCancellationToken),
            cancellationToken);
    }

    private async Task<AppCommandResult> RunListAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = ListIntentBinding.GetRequiredIntent(parseResult);
        switch (intent)
        {
            case VbaDevReferenceListCommandIntent.SelectedAndResolved:
                return await VbaDevCommandGrammar.ResolveDocumentContextAsync(
                        composition,
                        intent.ProjectRoot,
                        intent.DocumentName,
                        (context, operationCancellationToken) =>
                            composition.ReferenceService.ListAsync(
                                context,
                                intent.Format,
                                operationCancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            case VbaDevReferenceListCommandIntent.SelectedWithoutResolution:
                return await VbaDevCommandGrammar.ResolveDocumentContextAsync(
                        composition,
                        intent.ProjectRoot,
                        intent.DocumentName,
                        (context, _) => Task.FromResult(
                            composition.ReferenceService.ListSelection(
                                context,
                                intent.Format)),
                        cancellationToken)
                    .ConfigureAwait(false);
            case VbaDevReferenceListCommandIntent.AvailableCatalog
                when intent.ProjectRoot is not null || intent.DocumentName is not null:
                return await VbaDevCommandGrammar.ResolveDocumentContextAsync(
                        composition,
                        intent.ProjectRoot,
                        intent.DocumentName,
                        (context, operationCancellationToken) =>
                            composition.ReferenceService.ListAvailableAsync(
                                context,
                                intent.Format,
                                operationCancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            case VbaDevReferenceListCommandIntent.AvailableCatalog:
                return await RunAvailableWithEnvironmentFallbackAsync(
                        intent.Format,
                        cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    $"Unsupported reference list intent '{intent.GetType().FullName}'.");
        }
    }

    private async Task<AppCommandResult> RunAvailableWithEnvironmentFallbackAsync(
        string format,
        CancellationToken cancellationToken)
    {
        try
        {
            if (composition.ProjectContextResolver.TryResolveImplicitDocumentContext(
                    composition.WorkingDirectory,
                    out var context))
            {
                return await composition.ReferenceService.ListAvailableAsync(
                        context!,
                        format,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await composition.ReferenceService.ListAvailableEnvironmentAsync(
                    format,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProjectManifestException exception)
        {
            return AppCommandResult.UsageError(exception.Message);
        }
    }

    private Task<AppCommandResult> RunRemoveAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = RemoveIntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveDocumentContextAsync(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            (context, operationCancellationToken) => composition.ReferenceService.RemoveAsync(
                context,
                intent.ReferenceNames,
                intent.Format,
                operationCancellationToken),
            cancellationToken);
    }

    private IEnumerable<CompletionItem> CompleteAdd(CompletionContext completionContext)
    {
        if (!ShouldEvaluateReferenceNameCompletion(completionContext))
        {
            return [];
        }

        return composition.ReferenceCompletionService.CompleteAdd(
                new ProjectResolutionRequest(
                    completionContext.ParseResult.GetValue(AddProjectOption),
                    completionContext.ParseResult.GetValue(AddDocumentOption),
                    composition.WorkingDirectory),
                completionContext.ParseResult.GetResult(AddReferencesArgument)?.Tokens
                    .Select(token => token.Value)
                    .ToArray()
                ?? [])
            .Select(name => new CompletionItem(name));
    }

    private IEnumerable<CompletionItem> CompleteRemove(CompletionContext completionContext)
    {
        if (!ShouldEvaluateReferenceNameCompletion(completionContext))
        {
            return [];
        }

        return composition.ReferenceCompletionService.CompleteRemove(
                new ProjectResolutionRequest(
                    completionContext.ParseResult.GetValue(RemoveProjectOption),
                    completionContext.ParseResult.GetValue(RemoveDocumentOption),
                    composition.WorkingDirectory),
                completionContext.ParseResult.GetResult(RemoveReferencesArgument)?.Tokens
                    .Select(token => token.Value)
                    .ToArray()
                ?? [])
            .Select(name => new CompletionItem(name));
    }

    private bool ShouldEvaluateReferenceNameCompletion(
        CompletionContext completionContext)
    {
        if (completionContext is not TextCompletionContext textContext)
        {
            return false;
        }

        if (!textContext.WordToComplete.StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        var cursorPosition = Math.Clamp(
            textContext.CursorPosition,
            0,
            textContext.CommandLineText.Length);
        var commandLinePrefix = textContext.CommandLineText[..cursorPosition];
        var prefixParseResult = rootCommand.Parse(
            commandLinePrefix,
            textContext.ParseResult.Configuration);
        var priorTokenCount = commandLinePrefix.Length > 0 &&
                              !char.IsWhiteSpace(commandLinePrefix[^1])
            ? Math.Max(0, prefixParseResult.Tokens.Count - 1)
            : prefixParseResult.Tokens.Count;
        var followsEndOfOptions = prefixParseResult.Tokens
            .Take(priorTokenCount)
            .Any(token => token.Type == TokenType.DoubleDash);
        if (followsEndOfOptions)
        {
            return true;
        }

        return !EnumerateAvailableOptions(prefixParseResult.CommandResult.Command)
            .SelectMany(option => option.Aliases.Prepend(option.Name))
            .Any(alias => alias.StartsWith(
                textContext.WordToComplete,
                StringComparison.Ordinal));
    }

    private static IEnumerable<Option> EnumerateAvailableOptions(Command command)
        => command.Options.Concat(
            command.Parents
                .OfType<Command>()
                .SelectMany(parent => parent.Options
                    .Where(option => option.Recursive)
                    .Concat(EnumerateRecursiveAncestorOptions(parent))));

    private static IEnumerable<Option> EnumerateRecursiveAncestorOptions(
        Command command)
        => command.Parents
            .OfType<Command>()
            .SelectMany(parent => parent.Options
                .Where(option => option.Recursive)
                .Concat(EnumerateRecursiveAncestorOptions(parent)));
}

internal sealed record VbaDevReferenceAddCommandIntent(
    string? ProjectRoot,
    string? DocumentName,
    IReadOnlyList<string> ReferenceNames,
    string Format);

internal sealed record VbaDevReferenceRemoveCommandIntent(
    string? ProjectRoot,
    string? DocumentName,
    IReadOnlyList<string> ReferenceNames,
    string Format);

internal abstract record VbaDevReferenceListCommandIntent
{
    private VbaDevReferenceListCommandIntent(
        string? projectRoot,
        string? documentName,
        string format)
    {
        ProjectRoot = projectRoot;
        DocumentName = documentName;
        Format = format;
    }

    internal string? ProjectRoot { get; }

    internal string? DocumentName { get; }

    internal string Format { get; }

    internal sealed record SelectedAndResolved(
        string? SelectedProjectRoot,
        string? SelectedDocumentName,
        string SelectedFormat) : VbaDevReferenceListCommandIntent(
            SelectedProjectRoot,
            SelectedDocumentName,
            SelectedFormat);

    internal sealed record SelectedWithoutResolution(
        string? SelectedProjectRoot,
        string? SelectedDocumentName,
        string SelectedFormat) : VbaDevReferenceListCommandIntent(
            SelectedProjectRoot,
            SelectedDocumentName,
            SelectedFormat);

    internal sealed record AvailableCatalog(
        string? SelectedProjectRoot,
        string? SelectedDocumentName,
        string SelectedFormat) : VbaDevReferenceListCommandIntent(
            SelectedProjectRoot,
            SelectedDocumentName,
            SelectedFormat);
}
