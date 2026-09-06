using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;

namespace VbaDev.Cli;

/// <summary>
/// Selects and renders the one grammar failure that owns an invocation.
/// </summary>
internal sealed class VbaDevGrammarFailureRouter
{
    private const string ExecutableName = "vba-dev";
    private readonly RootCommand rootCommand;
    private readonly IReadOnlyDictionary<Command, string> commandPaths;
    private readonly IReadOnlyDictionary<Command, int> commandDisplayOrders;
    private readonly IReadOnlyList<VbaDevStringOption> acceptedValueOptions;
    private readonly VbaDevGrammarFailureRuleSnapshot rules;

    internal VbaDevGrammarFailureRouter(RootCommand rootCommand)
        : this(rootCommand, new VbaDevGrammarFailureRules())
    {
    }

    internal VbaDevGrammarFailureRouter(
        RootCommand rootCommand,
        VbaDevGrammarFailureRules rules)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(rules);
        this.rootCommand = rootCommand;
        commandPaths = BuildCommandPaths(rootCommand);
        IEqualityComparer<Command> commandComparer = ReferenceEqualityComparer.Instance;
        commandDisplayOrders = commandPaths.Keys
            .Select((command, index) => (command, index))
            .ToDictionary(
                item => item.command,
                item => item.index,
                commandComparer);
        this.rules = rules.CreateSnapshot(commandPaths.Keys);
        acceptedValueOptions = EnumerateCommands(rootCommand)
            .SelectMany(command => command.Options)
            .OfType<VbaDevStringOption>()
            .Where(option => option.AcceptedValues is not null)
            .Distinct<VbaDevStringOption>(ReferenceEqualityComparer.Instance)
            .ToArray();
    }

    internal RootCommand RootCommand => rootCommand;

    internal bool TryWriteFailure(
        ParseResult parseResult,
        TextWriter standardError,
        bool isExplicitHelp = false)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(standardError);

        var tokenOrder = new VbaDevGrammarTokenOrder(parseResult);
        var cardinalityCandidates = BuildCardinalityCandidates(parseResult, tokenOrder);
        var parsingCandidates = BuildParsingCandidates(
            parseResult,
            tokenOrder,
            cardinalityCandidates.Count > 0);
        if (TryWriteFirstFailure(
                parseResult,
                parsingCandidates,
                standardError,
                isExplicitHelp) ||
            TryWriteFirstFailure(
                parseResult,
                cardinalityCandidates,
                standardError,
                isExplicitHelp) ||
            TryWriteFirstFailure(
                parseResult,
                BuildValueCandidates(parseResult, tokenOrder),
                standardError,
                isExplicitHelp) ||
            TryWriteFirstFailure(
                parseResult,
                BuildRelationshipCandidates(parseResult, tokenOrder),
                standardError,
                isExplicitHelp) ||
            TryWriteFirstFailure(
                parseResult,
                BuildBindingCandidates(
                    parseResult,
                    tokenOrder,
                    includeIntentBindings: !isExplicitHelp),
                standardError,
                isExplicitHelp))
        {
            return true;
        }

        if (isExplicitHelp || parseResult.Errors.Count == 0)
        {
            return false;
        }

        WriteFailure(
            standardError,
            $"Input for command '{GetCommandPath(parseResult.CommandResult.Command)}' is invalid.",
            GetCommandPath(parseResult.CommandResult.Command));
        return true;
    }

    private IReadOnlyList<VbaDevGrammarFailureCandidate> BuildParsingCandidates(
        ParseResult parseResult,
        VbaDevGrammarTokenOrder tokenOrder,
        bool hasCardinalityDefect)
    {
        var command = parseResult.CommandResult.Command;
        var candidates = tokenOrder.UnmatchedTokens
            .Select(unmatched => new VbaDevGrammarFailureCandidate(
                unmatched.Index,
                VbaDevGrammarSymbolKind.Command,
                GetCommandDisplayOrder(command),
                int.MaxValue,
                int.MaxValue,
                $"Unrecognized token '{Escape(unmatched.Token.Value)}' for command " +
                $"'{GetCommandPath(command)}'."))
            .ToList();
        var errorResults = parseResult.Errors
            .Where(error => error.SymbolResult is not null)
            .Select(error => NormalizeErrorResult(error.SymbolResult!))
            .Distinct<SymbolResult>(ReferenceEqualityComparer.Instance)
            .ToArray();
        var hasSpecificError = errorResults.Any(result => result is not CommandResult);

        foreach (var result in errorResults)
        {
            if (HasCardinalityDefect(result, tokenOrder) ||
                HasAcceptedValueDefect(result) ||
                IsTerminatingStandaloneCombinationError(parseResult, result))
            {
                continue;
            }

            if (result is CommandResult &&
                (candidates.Count > 0 || hasCardinalityDefect || hasSpecificError))
            {
                continue;
            }

            candidates.Add(CreateParsingCandidate(parseResult, result, tokenOrder));
        }

        if (parseResult.Errors.Count > 0 && candidates.Count == 0 &&
            !hasCardinalityDefect &&
            !errorResults.Any(HasAcceptedValueDefect) &&
            !errorResults.Any(result =>
                IsTerminatingStandaloneCombinationError(parseResult, result)))
        {
            candidates.Add(new VbaDevGrammarFailureCandidate(
                int.MaxValue,
                VbaDevGrammarSymbolKind.Command,
                GetCommandDisplayOrder(command),
                int.MaxValue,
                int.MaxValue,
                $"Input for command '{GetCommandPath(command)}' is invalid."));
        }

        return candidates;
    }

    private IReadOnlyList<VbaDevGrammarFailureCandidate> BuildCardinalityCandidates(
        ParseResult parseResult,
        VbaDevGrammarTokenOrder tokenOrder)
    {
        var command = parseResult.CommandResult.Command;
        var candidates = new List<VbaDevGrammarFailureCandidate>();

        for (var index = 0; index < command.Arguments.Count; index++)
        {
            var argument = command.Arguments[index];
            var result = parseResult.GetResult(argument);
            var tokenCount = result?.Tokens.Count ?? 0;
            if (tokenCount < argument.Arity.MinimumNumberOfValues)
            {
                var anchor = result is { Tokens.Count: > 0 }
                    ? tokenOrder.GetIndex(result.Tokens[0])
                    : int.MaxValue;
                var diagnostic = tokenCount == 0 && argument.Arity.MinimumNumberOfValues == 1
                    ? $"Argument '{FormatArgument(argument)}' is required for command " +
                      $"'{GetCommandPath(command)}'."
                    : $"Argument '{FormatArgument(argument)}' requires at least " +
                      $"{argument.Arity.MinimumNumberOfValues} values.";
                candidates.Add(new VbaDevGrammarFailureCandidate(
                    anchor,
                    VbaDevGrammarSymbolKind.Argument,
                    index,
                    int.MaxValue,
                    int.MaxValue,
                    diagnostic,
                    SuppressForExplicitHelp: tokenCount == 0));
            }
            else if (tokenCount > argument.Arity.MaximumNumberOfValues)
            {
                candidates.Add(new VbaDevGrammarFailureCandidate(
                    tokenOrder.GetIndex(result!.Tokens[argument.Arity.MaximumNumberOfValues]),
                    VbaDevGrammarSymbolKind.Argument,
                    index,
                    int.MaxValue,
                    int.MaxValue,
                    $"Argument '{FormatArgument(argument)}' accepts at most " +
                    $"{argument.Arity.MaximumNumberOfValues} values."));
            }
        }

        var options = command.Options
            .Concat(EnumerateOptionResults(parseResult.RootCommandResult)
                .Where(result => !result.Implicit)
                .Select(result => result.Option))
            .Distinct<Option>(ReferenceEqualityComparer.Instance)
            .ToArray();
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index];
            var result = parseResult.GetResult(option);
            var defect = result is { Implicit: false }
                ? GetOptionCardinalityDefect(result, tokenOrder)
                : null;
            if (defect is { Kind: VbaDevGrammarCardinalityDefectKind.Missing })
            {
                candidates.Add(new VbaDevGrammarFailureCandidate(
                    defect.TokenIndex,
                    VbaDevGrammarSymbolKind.Option,
                    index,
                    int.MaxValue,
                    int.MaxValue,
                    $"Option '{option.Name}' requires a value."));
            }
            else if (defect is { Kind: VbaDevGrammarCardinalityDefectKind.Excess })
            {
                candidates.Add(new VbaDevGrammarFailureCandidate(
                    defect.TokenIndex,
                    VbaDevGrammarSymbolKind.Option,
                    index,
                    int.MaxValue,
                    int.MaxValue,
                    $"Option '{option.Name}' accepts at most " +
                    $"{option.Arity.MaximumNumberOfValues} values."));
            }
            else if (option.Required && result is not { Implicit: false })
            {
                candidates.Add(new VbaDevGrammarFailureCandidate(
                    int.MaxValue,
                    VbaDevGrammarSymbolKind.Option,
                    index,
                    int.MaxValue,
                    int.MaxValue,
                    $"Option '{option.Name}' is required for command " +
                    $"'{GetCommandPath(command)}'.",
                    SuppressForExplicitHelp: true));
            }
        }

        return candidates;
    }

    private IReadOnlyList<VbaDevGrammarFailureCandidate> BuildValueCandidates(
        ParseResult parseResult,
        VbaDevGrammarTokenOrder tokenOrder)
    {
        var command = parseResult.CommandResult.Command;
        var candidates = new List<VbaDevGrammarFailureCandidate>();

        foreach (var option in acceptedValueOptions)
        {
            var result = parseResult.GetResult(option);
            if (result is not { Implicit: false, Tokens.Count: > 0 })
            {
                continue;
            }

            var suppliedValue = result.Tokens[0].Value;
            var acceptedValues = option.AcceptedValues
                ?? throw new InvalidOperationException(
                    $"Accepted values for option '{option.Name}' are unavailable.");
            if (acceptedValues.Any(value => value.Equals(
                    suppliedValue,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(result.Tokens[0]),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(command, option),
                int.MaxValue,
                int.MaxValue,
                $"Option '{option.Name}' does not accept value '{Escape(suppliedValue)}'. " +
                $"Accepted values: {string.Join(", ", acceptedValues)}."));
        }

        foreach (var rule in rules.NonEmptyOptions)
        {
            var result = parseResult.GetResult(rule.Option);
            if (result is not { Implicit: false, Tokens.Count: > 0 } ||
                !string.IsNullOrWhiteSpace(result.Tokens[0].Value))
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(result.Tokens[0]),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(command, rule.Option),
                int.MaxValue,
                rule.DeclarationOrder,
                $"Option '{rule.Option.Name}' requires a non-empty value."));
        }

        foreach (var rule in rules.NonEmptyArguments)
        {
            var result = parseResult.GetResult(rule.Argument);
            var emptyToken = result?.Tokens.FirstOrDefault(token =>
                string.IsNullOrWhiteSpace(token.Value));
            if (emptyToken is null)
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(emptyToken),
                VbaDevGrammarSymbolKind.Argument,
                GetArgumentDisplayOrder(command, rule.Argument),
                int.MaxValue,
                rule.DeclarationOrder,
                $"Argument '{FormatArgument(rule.Argument)}' requires a non-empty value."));
        }

        foreach (var rule in rules.PositiveOptions)
        {
            var result = parseResult.GetResult(rule.Option);
            if (result is not { Implicit: false, Tokens.Count: > 0 } ||
                parseResult.GetValue(rule.Option) is not int value || value > 0)
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(result.Tokens[0]),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(command, rule.Option),
                int.MaxValue,
                rule.DeclarationOrder,
                $"Option '{rule.Option.Name}' requires a positive whole number."));
        }

        return candidates;
    }

    private IReadOnlyList<VbaDevGrammarFailureCandidate> BuildRelationshipCandidates(
        ParseResult parseResult,
        VbaDevGrammarTokenOrder tokenOrder)
    {
        var command = parseResult.CommandResult.Command;
        var candidates = new List<VbaDevGrammarFailureCandidate>();
        foreach (var relationship in rules.Relationships.Where(rule =>
                     ReferenceEquals(rule.Command, command)))
        {
            var first = GetExplicitResult(parseResult, relationship.First);
            var second = GetExplicitResult(parseResult, relationship.Second);
            var violated = relationship.Kind switch
            {
                VbaDevGrammarRelationshipKind.Requires => first is not null && second is null,
                VbaDevGrammarRelationshipKind.Conflicts => first is not null && second is not null,
                VbaDevGrammarRelationshipKind.AllOrNone => (first is null) != (second is null),
                _ => throw new InvalidOperationException(
                    $"Unsupported grammar relationship '{relationship.Kind}'.")
            };
            if (!violated)
            {
                continue;
            }

            var anchorResult = relationship.Kind switch
            {
                VbaDevGrammarRelationshipKind.Requires => first,
                VbaDevGrammarRelationshipKind.Conflicts => Earliest(first, second, tokenOrder),
                VbaDevGrammarRelationshipKind.AllOrNone => first ?? second,
                _ => null
            };
            var anchorOption = anchorResult?.Option ?? relationship.First;
            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(anchorResult?.IdentifierToken),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(command, anchorOption),
                (int)relationship.Kind,
                relationship.DeclarationOrder,
                FormatRelationshipDiagnostic(relationship)));
        }

        return candidates;
    }

    private IReadOnlyList<VbaDevGrammarFailureCandidate> BuildBindingCandidates(
        ParseResult parseResult,
        VbaDevGrammarTokenOrder tokenOrder,
        bool includeIntentBindings)
    {
        var command = parseResult.CommandResult.Command;
        var candidates = new List<VbaDevGrammarFailureCandidate>();
        foreach (var standalone in rules.StandaloneOptions)
        {
            var result = GetExplicitResult(parseResult, standalone.Option);
            if (result is null ||
                parseResult.Tokens.All(token => ReferenceEquals(token, result.IdentifierToken)))
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(result.IdentifierToken),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(command, standalone.Option),
                int.MaxValue,
                standalone.DeclarationOrder,
                $"Option '{standalone.Option.Name}' cannot be combined with other arguments."));
        }

        if (candidates.Count > 0 || !includeIntentBindings)
        {
            return candidates;
        }

        foreach (var binding in rules.IntentBindings.Where(rule =>
                     ReferenceEquals(rule.Command, command)))
        {
            if (binding.TryBind(parseResult))
            {
                continue;
            }

            candidates.Add(new VbaDevGrammarFailureCandidate(
                int.MaxValue,
                VbaDevGrammarSymbolKind.Command,
                GetCommandDisplayOrder(command),
                int.MaxValue,
                binding.DeclarationOrder,
                $"The supplied values do not form a valid intent for command " +
                $"'{GetCommandPath(command)}'."));
        }

        return candidates;
    }

    private VbaDevGrammarFailureCandidate CreateParsingCandidate(
        ParseResult parseResult,
        SymbolResult result,
        VbaDevGrammarTokenOrder tokenOrder)
        => result switch
        {
            OptionResult optionResult => new VbaDevGrammarFailureCandidate(
                GetValueOrIdentifierIndex(optionResult, tokenOrder),
                VbaDevGrammarSymbolKind.Option,
                GetOptionDisplayOrder(parseResult.CommandResult.Command, optionResult.Option),
                int.MaxValue,
                int.MaxValue,
                optionResult.Tokens.Count > 0
                    ? $"Value '{Escape(optionResult.Tokens[0].Value)}' for option " +
                      $"'{optionResult.Option.Name}' is invalid."
                    : $"Option '{optionResult.Option.Name}' is invalid."),
            ArgumentResult argumentResult => new VbaDevGrammarFailureCandidate(
                GetFirstTokenIndex(argumentResult, tokenOrder),
                VbaDevGrammarSymbolKind.Argument,
                GetArgumentDisplayOrder(
                    parseResult.CommandResult.Command,
                    argumentResult.Argument),
                int.MaxValue,
                int.MaxValue,
                argumentResult.Tokens.Count > 0
                    ? $"Value '{Escape(argumentResult.Tokens[0].Value)}' for argument " +
                      $"'{FormatArgument(argumentResult.Argument)}' is invalid."
                    : $"Argument '{FormatArgument(argumentResult.Argument)}' is invalid."),
            CommandResult commandResult => new VbaDevGrammarFailureCandidate(
                tokenOrder.GetIndex(commandResult.IdentifierToken),
                VbaDevGrammarSymbolKind.Command,
                GetCommandDisplayOrder(commandResult.Command),
                int.MaxValue,
                int.MaxValue,
                $"Input for command '{GetCommandPath(commandResult.Command)}' is invalid.",
                SuppressForExplicitHelp: true),
            _ => new VbaDevGrammarFailureCandidate(
                int.MaxValue,
                VbaDevGrammarSymbolKind.Command,
                GetCommandDisplayOrder(parseResult.CommandResult.Command),
                int.MaxValue,
                int.MaxValue,
                $"Input for command '{GetCommandPath(parseResult.CommandResult.Command)}' is invalid.")
        };

    private bool TryWriteFirstFailure(
        ParseResult parseResult,
        IReadOnlyCollection<VbaDevGrammarFailureCandidate> candidates,
        TextWriter standardError,
        bool isExplicitHelp)
    {
        var candidate = candidates
            .Where(item => !isExplicitHelp || !item.SuppressForExplicitHelp)
            .OrderBy(item => item.TokenIndex)
            .ThenBy(item => item.SymbolKind)
            .ThenBy(item => item.DisplayOrder)
            .ThenBy(item => item.RelationshipOrder)
            .ThenBy(item => item.DeclarationOrder)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        WriteFailure(
            standardError,
            candidate.Diagnostic,
            GetCommandPath(parseResult.CommandResult.Command));
        return true;
    }

    private static SymbolResult NormalizeErrorResult(SymbolResult result)
        => result is ArgumentResult { Parent: OptionResult optionResult }
            ? optionResult
            : result;

    private static bool HasCardinalityDefect(
        SymbolResult result,
        VbaDevGrammarTokenOrder tokenOrder)
        => result switch
        {
            OptionResult optionResult => GetOptionCardinalityDefect(
                optionResult,
                tokenOrder) is not null,
            ArgumentResult argumentResult =>
                argumentResult.Tokens.Count < argumentResult.Argument.Arity.MinimumNumberOfValues ||
                argumentResult.Tokens.Count > argumentResult.Argument.Arity.MaximumNumberOfValues,
            _ => false
        };

    private static VbaDevGrammarCardinalityDefect? GetOptionCardinalityDefect(
        OptionResult result,
        VbaDevGrammarTokenOrder tokenOrder)
    {
        var option = result.Option;
        var identifiers = tokenOrder.GetOptionIdentifierTokens(result);
        VbaDevGrammarCardinalityDefect? selected = null;
        if (result.Tokens.Count > option.Arity.MaximumNumberOfValues)
        {
            selected = new VbaDevGrammarCardinalityDefect(
                tokenOrder.GetIndex(result.Tokens[option.Arity.MaximumNumberOfValues]),
                VbaDevGrammarCardinalityDefectKind.Excess);
        }

        if (identifiers.Count <= 1)
        {
            if (result.Tokens.Count < option.Arity.MinimumNumberOfValues)
            {
                return new VbaDevGrammarCardinalityDefect(
                    tokenOrder.GetIndex(result.IdentifierToken),
                    VbaDevGrammarCardinalityDefectKind.Missing);
            }

            return selected;
        }

        var valuesByOccurrence = identifiers
            .Select(_ => new List<Token>())
            .ToArray();
        foreach (var token in result.Tokens)
        {
            var tokenIndex = tokenOrder.GetIndex(token);
            var occurrenceIndex = 0;
            for (var index = 1; index < identifiers.Count; index++)
            {
                if (tokenOrder.GetIndex(identifiers[index]) > tokenIndex)
                {
                    break;
                }

                occurrenceIndex = index;
            }

            valuesByOccurrence[occurrenceIndex].Add(token);
        }

        for (var index = 0; index < identifiers.Count; index++)
        {
            VbaDevGrammarCardinalityDefect? candidate = null;
            if (valuesByOccurrence[index].Count < option.Arity.MinimumNumberOfValues)
            {
                candidate = new VbaDevGrammarCardinalityDefect(
                    tokenOrder.GetIndex(identifiers[index]),
                    VbaDevGrammarCardinalityDefectKind.Missing);
            }
            if (candidate is not null &&
                (selected is null || candidate.TokenIndex < selected.TokenIndex))
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private static IEnumerable<OptionResult> EnumerateOptionResults(CommandResult commandResult)
    {
        foreach (var child in commandResult.Children)
        {
            if (child is OptionResult optionResult)
            {
                yield return optionResult;
            }
            else if (child is CommandResult childCommand)
            {
                foreach (var descendant in EnumerateOptionResults(childCommand))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static bool HasAcceptedValueDefect(SymbolResult result)
        => result is OptionResult
           {
               Option: VbaDevStringOption { AcceptedValues: not null } option,
               Tokens.Count: > 0
           } optionResult &&
           !option.AcceptedValues.Any(value => value.Equals(
               optionResult.Tokens[0].Value,
               StringComparison.OrdinalIgnoreCase));

    private bool IsTerminatingStandaloneCombinationError(
        ParseResult parseResult,
        SymbolResult result)
        => result is OptionResult optionResult &&
           rules.StandaloneOptions.Any(rule => ReferenceEquals(rule.Option, optionResult.Option)) &&
           optionResult.Option.Action?.Terminating is true &&
           (optionResult.Option.Validators.Count == 0 || optionResult.Option is VersionOption) &&
           parseResult.Tokens.Any(token =>
               !ReferenceEquals(token, optionResult.IdentifierToken));

    private static OptionResult? GetExplicitResult(ParseResult parseResult, Option option)
        => parseResult.GetResult(option) is { Implicit: false } result ? result : null;

    private static OptionResult? Earliest(
        OptionResult? first,
        OptionResult? second,
        VbaDevGrammarTokenOrder tokenOrder)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return tokenOrder.GetIndex(first.IdentifierToken) <=
               tokenOrder.GetIndex(second.IdentifierToken)
            ? first
            : second;
    }

    private static int GetValueOrIdentifierIndex(
        OptionResult result,
        VbaDevGrammarTokenOrder tokenOrder)
        => result.Tokens.Count > 0
            ? tokenOrder.GetIndex(result.Tokens[0])
            : tokenOrder.GetIndex(result.IdentifierToken);

    private static int GetFirstTokenIndex(
        ArgumentResult result,
        VbaDevGrammarTokenOrder tokenOrder)
        => result.Tokens.Count > 0
            ? tokenOrder.GetIndex(result.Tokens[0])
            : int.MaxValue;

    private static string FormatArgument(Argument argument)
        => $"<{argument.HelpName ?? argument.Name}>" +
           (argument.Arity.MaximumNumberOfValues > 1 ? "..." : string.Empty);

    private static string FormatRelationshipDiagnostic(VbaDevGrammarRelationship relationship)
        => relationship.Kind switch
        {
            VbaDevGrammarRelationshipKind.Requires =>
                $"Option '{relationship.First.Name}' requires option '{relationship.Second.Name}'.",
            VbaDevGrammarRelationshipKind.Conflicts =>
                $"Options '{relationship.First.Name}' and '{relationship.Second.Name}' cannot be used together.",
            VbaDevGrammarRelationshipKind.AllOrNone =>
                $"Options '{relationship.First.Name}' and '{relationship.Second.Name}' must be supplied together.",
            _ => throw new InvalidOperationException(
                $"Unsupported grammar relationship '{relationship.Kind}'.")
        };

    private string GetCommandPath(Command command)
        => commandPaths.TryGetValue(command, out var path)
            ? path
            : throw new InvalidOperationException(
                $"Command '{command.Name}' is not part of the completed grammar graph.");

    private int GetCommandDisplayOrder(Command command)
        => commandDisplayOrders.TryGetValue(command, out var order)
            ? order
            : int.MaxValue;

    private static int GetArgumentDisplayOrder(Command command, Argument argument)
    {
        for (var index = 0; index < command.Arguments.Count; index++)
        {
            if (ReferenceEquals(command.Arguments[index], argument))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static int GetOptionDisplayOrder(Command command, Option option)
    {
        for (var index = 0; index < command.Options.Count; index++)
        {
            if (ReferenceEquals(command.Options[index], option))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

    private static void WriteFailure(TextWriter standardError, string diagnostic, string commandPath)
        => standardError.Write(
            $"Error: {diagnostic}{Environment.NewLine}" +
            $"Hint: Run '{commandPath} --help' for usage.{Environment.NewLine}");

    private static IReadOnlyDictionary<Command, string> BuildCommandPaths(RootCommand rootCommand)
    {
        var paths = new Dictionary<Command, string>(ReferenceEqualityComparer.Instance)
        {
            [rootCommand] = ExecutableName
        };
        AddDescendantPaths(rootCommand, ExecutableName, paths);
        return paths;
    }

    private static IEnumerable<Command> EnumerateCommands(Command command)
    {
        yield return command;
        foreach (var child in command.Subcommands)
        {
            foreach (var descendant in EnumerateCommands(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AddDescendantPaths(
        Command parent,
        string parentPath,
        IDictionary<Command, string> paths)
    {
        foreach (var command in parent.Subcommands)
        {
            var commandPath = $"{parentPath} {command.Name}";
            paths.Add(command, commandPath);
            AddDescendantPaths(command, commandPath, paths);
        }
    }
}

internal sealed class VbaDevGrammarFailureRules
{
    private readonly List<VbaDevGrammarNonEmptyOption> nonEmptyOptions = [];
    private readonly List<VbaDevGrammarNonEmptyArgument> nonEmptyArguments = [];
    private readonly List<VbaDevGrammarPositiveOption> positiveOptions = [];
    private readonly List<VbaDevGrammarRelationship> relationships = [];
    private readonly List<VbaDevGrammarIntentBinding> intentBindings = [];
    private readonly List<VbaDevGrammarStandaloneOption> standaloneOptions = [];
    private int nextDeclarationOrder;

    internal void RequireNonEmpty(Option<string> option)
    {
        ArgumentNullException.ThrowIfNull(option);
        EnsureNew(nonEmptyOptions.Any(rule => ReferenceEquals(rule.Option, option)), option, "non-empty");
        nonEmptyOptions.Add(new VbaDevGrammarNonEmptyOption(option, nextDeclarationOrder++));
    }

    internal void RequireNonEmpty(Argument<string[]> argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        EnsureNew(
            nonEmptyArguments.Any(rule => ReferenceEquals(rule.Argument, argument)),
            argument,
            "non-empty");
        nonEmptyArguments.Add(new VbaDevGrammarNonEmptyArgument(
            argument,
            nextDeclarationOrder++));
    }

    internal void RequirePositive(Option<int?> option)
    {
        ArgumentNullException.ThrowIfNull(option);
        EnsureNew(positiveOptions.Any(rule => ReferenceEquals(rule.Option, option)), option, "positive");
        positiveOptions.Add(new VbaDevGrammarPositiveOption(option, nextDeclarationOrder++));
    }

    internal void Requires(Command command, Option first, Option second)
        => AddRelationship(command, VbaDevGrammarRelationshipKind.Requires, first, second);

    internal void Conflicts(Command command, Option first, Option second)
        => AddRelationship(command, VbaDevGrammarRelationshipKind.Conflicts, first, second);

    internal void AllOrNone(Command command, Option first, Option second)
        => AddRelationship(command, VbaDevGrammarRelationshipKind.AllOrNone, first, second);

    internal VbaDevGrammarIntentBinding<TIntent> BindIntent<TIntent>(
        Command command,
        Func<ParseResult, VbaDevGrammarIntentBindResult<TIntent>> binder)
        where TIntent : class
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(binder);
        if (intentBindings.Any(binding => ReferenceEquals(binding.Command, command)))
        {
            throw new InvalidOperationException(
                $"Command '{command.Name}' has more than one intent binding rule.");
        }

        var binding = new VbaDevGrammarIntentBinding<TIntent>(
            command,
            binder,
            nextDeclarationOrder++);
        intentBindings.Add(binding);
        return binding;
    }

    internal void RequireStandalone(Option option)
    {
        ArgumentNullException.ThrowIfNull(option);
        EnsureNew(
            standaloneOptions.Any(rule => ReferenceEquals(rule.Option, option)),
            option,
            "standalone");
        standaloneOptions.Add(new VbaDevGrammarStandaloneOption(option, nextDeclarationOrder++));
    }

    internal VbaDevGrammarFailureRuleSnapshot CreateSnapshot(IEnumerable<Command> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var commandSet = commands.ToHashSet<Command>(ReferenceEqualityComparer.Instance);
        var root = commandSet.OfType<RootCommand>().Single();
        var optionSet = commandSet
            .SelectMany(command => command.Options)
            .ToHashSet<Option>(ReferenceEqualityComparer.Instance);
        var argumentSet = commandSet
            .SelectMany(command => command.Arguments)
            .ToHashSet<Argument>(ReferenceEqualityComparer.Instance);

        foreach (var option in nonEmptyOptions.Select(rule => (Option)rule.Option)
                     .Concat(positiveOptions.Select(rule => (Option)rule.Option)))
        {
            EnsureReachable(option, optionSet);
        }

        foreach (var argument in nonEmptyArguments.Select(rule => (Argument)rule.Argument))
        {
            EnsureReachable(argument, argumentSet);
        }

        foreach (var standalone in standaloneOptions)
        {
            EnsureReachable(standalone.Option, optionSet);
            if (standalone.Option.Arity.MaximumNumberOfValues != 0 ||
                !root.Options.Any(option => ReferenceEquals(option, standalone.Option)))
            {
                throw new InvalidOperationException(
                    $"Standalone option '{standalone.Option.Name}' must be a zero-arity root option.");
            }
        }

        foreach (var relationship in relationships)
        {
            EnsureReachable(relationship.Command, commandSet);
            if (!relationship.Command.Options.Any(option =>
                    ReferenceEquals(option, relationship.First)) ||
                !relationship.Command.Options.Any(option =>
                    ReferenceEquals(option, relationship.Second)))
            {
                throw new InvalidOperationException(
                    $"Relationship options for command '{relationship.Command.Name}' are not " +
                    "part of its completed grammar.");
            }
        }

        foreach (var binding in intentBindings)
        {
            EnsureReachable(binding.Command, commandSet);
        }

        return new VbaDevGrammarFailureRuleSnapshot(
            Array.AsReadOnly(nonEmptyOptions.ToArray()),
            Array.AsReadOnly(nonEmptyArguments.ToArray()),
            Array.AsReadOnly(positiveOptions.ToArray()),
            Array.AsReadOnly(relationships.ToArray()),
            Array.AsReadOnly(intentBindings.ToArray()),
            Array.AsReadOnly(standaloneOptions.ToArray()));
    }

    private void AddRelationship(
        Command command,
        VbaDevGrammarRelationshipKind kind,
        Option first,
        Option second)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (ReferenceEquals(first, second))
        {
            throw new InvalidOperationException(
                "A grammar relationship cannot reference the same option twice.");
        }

        if (relationships.Any(relationship =>
                ReferenceEquals(relationship.Command, command) &&
                relationship.Kind == kind &&
                (ReferenceEquals(relationship.First, first) &&
                 ReferenceEquals(relationship.Second, second) ||
                 kind is not VbaDevGrammarRelationshipKind.Requires &&
                 ReferenceEquals(relationship.First, second) &&
                 ReferenceEquals(relationship.Second, first))))
        {
            throw new InvalidOperationException(
                $"Command '{command.Name}' has the same '{kind}' relationship more than once.");
        }

        relationships.Add(new VbaDevGrammarRelationship(
            command,
            kind,
            first,
            second,
            nextDeclarationOrder++));
    }

    private static void EnsureNew(bool duplicate, Option option, string rule)
    {
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"Option '{option.Name}' has more than one {rule} rule.");
        }
    }

    private static void EnsureNew(bool duplicate, Argument argument, string rule)
    {
        if (duplicate)
        {
            throw new InvalidOperationException(
                $"Argument '{argument.Name}' has more than one {rule} rule.");
        }
    }

    private static void EnsureReachable<T>(T symbol, IReadOnlySet<T> reachable)
        where T : Symbol
    {
        if (!reachable.Contains(symbol))
        {
            throw new InvalidOperationException(
                $"Grammar rule symbol '{symbol.Name}' is not part of the completed graph.");
        }
    }
}

internal sealed class VbaDevGrammarTokenOrder
{
    private readonly IReadOnlyList<Token> tokens;
    private readonly IReadOnlyDictionary<Token, int> tokenIndexes;

    internal VbaDevGrammarTokenOrder(ParseResult parseResult)
    {
        tokens = parseResult.Tokens.ToArray();
        var indexes = new Dictionary<Token, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < parseResult.Tokens.Count; index++)
        {
            indexes[parseResult.Tokens[index]] = index;
        }

        tokenIndexes = indexes;
        UnmatchedTokens = FindUnmatchedTokens(parseResult, indexes);
    }

    internal IReadOnlyList<VbaDevGrammarUnmatchedToken> UnmatchedTokens { get; }

    internal int GetIndex(Token? token)
        => token is not null && tokenIndexes.TryGetValue(token, out var index)
            ? index
            : int.MaxValue;

    internal IReadOnlyList<Token> GetOptionIdentifierTokens(OptionResult result)
    {
        var identifiers = tokens
            .TakeWhile(token => token.Value != "--")
            .Where(token =>
                token.Type == TokenType.Option &&
                MatchesOptionSpelling(token.Value, result.Option))
            .Take(result.IdentifierTokenCount)
            .ToList();
        if (result.IdentifierToken is not null &&
            !identifiers.Contains(result.IdentifierToken, ReferenceEqualityComparer.Instance))
        {
            identifiers.Add(result.IdentifierToken);
            identifiers.Sort((first, second) => GetIndex(first).CompareTo(GetIndex(second)));
        }

        return identifiers;
    }

    private static IReadOnlyList<VbaDevGrammarUnmatchedToken> FindUnmatchedTokens(
        ParseResult parseResult,
        IReadOnlyDictionary<Token, int> tokenIndexes)
    {
        var claimed = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        var optionResults = new List<OptionResult>();
        AddClaimedTokens(parseResult.RootCommandResult, claimed, optionResults);
        foreach (var result in optionResults)
        {
            var remainingIdentifiers = result.IdentifierTokenCount - 1;
            if (remainingIdentifiers <= 0)
            {
                continue;
            }

            foreach (var token in parseResult.Tokens.TakeWhile(token => token.Value != "--"))
            {
                if (remainingIdentifiers == 0)
                {
                    break;
                }

                if (claimed.Contains(token) ||
                    token.Type != TokenType.Option ||
                    !MatchesOptionSpelling(token.Value, result.Option))
                {
                    continue;
                }

                claimed.Add(token);
                remainingIdentifiers--;
            }
        }

        var remaining = parseResult.UnmatchedTokens
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var unmatched = new List<VbaDevGrammarUnmatchedToken>();

        foreach (var token in parseResult.Tokens)
        {
            if (claimed.Contains(token) ||
                !remaining.TryGetValue(token.Value, out var count) || count == 0)
            {
                continue;
            }

            unmatched.Add(new VbaDevGrammarUnmatchedToken(token, tokenIndexes[token]));
            remaining[token.Value] = count - 1;
        }

        if (unmatched.Count == parseResult.UnmatchedTokens.Count)
        {
            return unmatched;
        }

        var selected = unmatched
            .Select(item => item.Token)
            .ToHashSet<Token>(ReferenceEqualityComparer.Instance);
        foreach (var value in parseResult.UnmatchedTokens)
        {
            var expected = parseResult.UnmatchedTokens.Count(item =>
                item.Equals(value, StringComparison.Ordinal));
            var actual = unmatched.Count(item =>
                item.Token.Value.Equals(value, StringComparison.Ordinal));
            if (actual >= expected)
            {
                continue;
            }

            var token = parseResult.Tokens.FirstOrDefault(candidate =>
                !selected.Contains(candidate) &&
                !claimed.Contains(candidate) &&
                candidate.Value.Equals(value, StringComparison.Ordinal));
            if (token is null)
            {
                continue;
            }

            selected.Add(token);
            unmatched.Add(new VbaDevGrammarUnmatchedToken(token, tokenIndexes[token]));
        }

        return unmatched.OrderBy(item => item.Index).ToArray();
    }

    private static bool MatchesOptionSpelling(string token, Option option)
        => option.Aliases.Prepend(option.Name).Any(spelling =>
            token.Equals(spelling, StringComparison.Ordinal) ||
            token.StartsWith($"{spelling}=", StringComparison.Ordinal));

    private static void AddClaimedTokens(
        SymbolResult result,
        ISet<Token> claimed,
        ICollection<OptionResult> optionResults)
    {
        switch (result)
        {
            case CommandResult commandResult:
                if (commandResult.IdentifierToken is not null)
                {
                    claimed.Add(commandResult.IdentifierToken);
                }

                foreach (var child in commandResult.Children)
                {
                    AddClaimedTokens(child, claimed, optionResults);
                }

                break;
            case OptionResult optionResult:
                optionResults.Add(optionResult);
                if (optionResult.IdentifierToken is not null)
                {
                    claimed.Add(optionResult.IdentifierToken);
                }

                foreach (var token in optionResult.Tokens)
                {
                    claimed.Add(token);
                }

                break;
            case DirectiveResult directiveResult:
                if (directiveResult.Token is not null)
                {
                    claimed.Add(directiveResult.Token);
                }

                break;
            default:
                foreach (var token in result.Tokens)
                {
                    claimed.Add(token);
                }

                break;
        }
    }
}

internal enum VbaDevGrammarSymbolKind
{
    Command,
    Argument,
    Option
}

internal enum VbaDevGrammarRelationshipKind
{
    Requires,
    Conflicts,
    AllOrNone
}

internal sealed record VbaDevGrammarFailureCandidate(
    int TokenIndex,
    VbaDevGrammarSymbolKind SymbolKind,
    int DisplayOrder,
    int RelationshipOrder,
    int DeclarationOrder,
    string Diagnostic,
    bool SuppressForExplicitHelp = false);

internal sealed record VbaDevGrammarUnmatchedToken(Token Token, int Index);

internal enum VbaDevGrammarCardinalityDefectKind
{
    Missing,
    Excess
}

internal sealed record VbaDevGrammarCardinalityDefect(
    int TokenIndex,
    VbaDevGrammarCardinalityDefectKind Kind);

internal sealed record VbaDevGrammarNonEmptyOption(
    Option<string> Option,
    int DeclarationOrder);

internal sealed record VbaDevGrammarNonEmptyArgument(
    Argument<string[]> Argument,
    int DeclarationOrder);

internal sealed record VbaDevGrammarPositiveOption(
    Option<int?> Option,
    int DeclarationOrder);

internal sealed record VbaDevGrammarRelationship(
    Command Command,
    VbaDevGrammarRelationshipKind Kind,
    Option First,
    Option Second,
    int DeclarationOrder);

internal abstract class VbaDevGrammarIntentBinding
{
    protected VbaDevGrammarIntentBinding(Command command, int declarationOrder)
    {
        Command = command;
        DeclarationOrder = declarationOrder;
    }

    internal Command Command { get; }

    internal int DeclarationOrder { get; }

    internal abstract bool TryBind(ParseResult parseResult);
}

internal sealed class VbaDevGrammarIntentBinding<TIntent> : VbaDevGrammarIntentBinding
    where TIntent : class
{
    private readonly Func<ParseResult, VbaDevGrammarIntentBindResult<TIntent>> binder;
    private readonly ConditionalWeakTable<
        ParseResult,
        Lazy<VbaDevGrammarIntentBindResult<TIntent>>> results = new();

    internal VbaDevGrammarIntentBinding(
        Command command,
        Func<ParseResult, VbaDevGrammarIntentBindResult<TIntent>> binder,
        int declarationOrder)
        : base(command, declarationOrder)
    {
        this.binder = binder;
    }

    internal override bool TryBind(ParseResult parseResult)
        => GetResult(parseResult).IsBound;

    internal TIntent GetRequiredIntent(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        if (!results.TryGetValue(parseResult, out var result))
        {
            throw new InvalidOperationException(
                $"Intent for command '{Command.Name}' has not been bound by the grammar router.");
        }

        var bindResult = result.Value;
        if (!bindResult.IsBound || bindResult.Intent is null)
        {
            throw new InvalidOperationException(
                $"Intent for command '{Command.Name}' is unavailable.");
        }

        return bindResult.Intent;
    }

    private VbaDevGrammarIntentBindResult<TIntent> GetResult(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return results.GetValue(
            parseResult,
            key => new Lazy<VbaDevGrammarIntentBindResult<TIntent>>(
                () => binder(key),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}

internal readonly record struct VbaDevGrammarIntentBindResult<TIntent>(
    bool IsBound,
    TIntent? Intent)
    where TIntent : class
{
    internal static VbaDevGrammarIntentBindResult<TIntent> Bound(TIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new VbaDevGrammarIntentBindResult<TIntent>(true, intent);
    }

    internal static VbaDevGrammarIntentBindResult<TIntent> Unbound => new(false, null);
}

internal sealed record VbaDevGrammarStandaloneOption(
    Option Option,
    int DeclarationOrder);

internal sealed record VbaDevGrammarFailureRuleSnapshot(
    IReadOnlyList<VbaDevGrammarNonEmptyOption> NonEmptyOptions,
    IReadOnlyList<VbaDevGrammarNonEmptyArgument> NonEmptyArguments,
    IReadOnlyList<VbaDevGrammarPositiveOption> PositiveOptions,
    IReadOnlyList<VbaDevGrammarRelationship> Relationships,
    IReadOnlyList<VbaDevGrammarIntentBinding> IntentBindings,
    IReadOnlyList<VbaDevGrammarStandaloneOption> StandaloneOptions);
