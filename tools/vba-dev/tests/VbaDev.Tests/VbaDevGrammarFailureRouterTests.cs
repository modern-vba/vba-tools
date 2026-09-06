using System.CommandLine;
using VbaDev.Cli;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevGrammarFailureRouterTests
{
    [Fact]
    public void GrammarFailureAtInvocationBoundarySkipsCommandAction()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var pathOption = new Option<string>("--path");
        var actionCount = 0;
        command.Add(pathOption);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(pathOption);

        var result = Invoke(root, rules, ["probe", "--path", ""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--path' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void NonEmptyOptionFailureUsesTheCanonicalSymbolAndCommandPath()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var pathOption = new Option<string>("--path");
        command.Add(pathOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(pathOption);

        var result = Invoke(root, rules, ["probe", "--path", ""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--path' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void NonEmptyVariadicArgumentUsesTheLeftmostInvalidTokenAndSkipsTheAction()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var valuesArgument = new Argument<string[]>("values")
        {
            Arity = ArgumentArity.OneOrMore
        };
        var actionCount = 0;
        command.Add(valuesArgument);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(valuesArgument);

        var result = Invoke(root, rules, ["probe", "valid", " ", ""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Argument '<values>...' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void PositiveScalarFailureUsesTheCanonicalSymbolAndCommandPath()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var timeoutOption = new Option<int?>("--timeout-seconds");
        command.Add(timeoutOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequirePositive(timeoutOption);

        var result = Invoke(root, rules, ["probe", "--timeout-seconds", "0"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--timeout-seconds' requires a positive whole number.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void PositiveScalarUsesTheTypedParserValue()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var countOption = new Option<int?>("--count")
        {
            CustomParser = _ => 0
        };
        var actionCount = 0;
        command.Add(countOption);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequirePositive(countOption);

        var result = Invoke(root, rules, ["probe", "--count", "zero"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--count' requires a positive whole number.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void RequiresRelationshipUsesCanonicalOptionNames()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var moduleOption = new Option<string>("--module");
        var procedureOption = new Option<string>("--procedure");
        command.Add(moduleOption);
        command.Add(procedureOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, procedureOption, moduleOption);

        var result = Invoke(root, rules, ["probe", "--procedure", "Test_One"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--procedure' requires option '--module'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void ConflictsRelationshipUsesCanonicalOptionNames()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var sourceSnapshotOption = new Option<string>("--source-snapshot");
        var noBuildOption = new Option<bool>("--no-build");
        command.Add(sourceSnapshotOption);
        command.Add(noBuildOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Conflicts(command, sourceSnapshotOption, noBuildOption);

        var result = Invoke(
            root,
            rules,
            ["probe", "--source-snapshot", "snapshot", "--no-build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Options '--source-snapshot' and '--no-build' cannot be used together.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void AllOrNoneRelationshipUsesCanonicalOptionNames()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var sourceSnapshotOption = new Option<string>("--source-snapshot");
        var outputOption = new Option<string>("--output");
        command.Add(sourceSnapshotOption);
        command.Add(outputOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.AllOrNone(command, sourceSnapshotOption, outputOption);

        var result = Invoke(root, rules, ["probe", "--source-snapshot", "snapshot"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Options '--source-snapshot' and '--output' must be supplied together.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void ClosedIntentBindingFailureUsesTheCanonicalCommandPath()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        command.SetAction(_ => 0);
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(root, rules, ["probe"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(1, bindingAttempts);
        Assert.Equal(
            $"Error: The supplied values do not form a valid intent for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RelationshipFailureWithExplicitHelpUsesTheCanonicalContract()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var firstOption = new Option<bool>("--first");
        var secondOption = new Option<bool>("--second");
        command.Add(firstOption);
        command.Add(secondOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Conflicts(command, firstOption, secondOption);

        var result = Invoke(
            root,
            rules,
            ["probe", "--first", "--second", "--help"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Options '--first' and '--second' cannot be used together.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void ValidExplicitHelpSkipsMissingRequirementsAndIntentBinding()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var inputArgument = new Argument<string>("input");
        var requiredOption = new Option<string>("--required") { Required = true };
        command.Add(inputArgument);
        command.Add(requiredOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(root, rules, ["probe", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(0, bindingAttempts);
    }

    [Fact]
    public void ValueRulesUseTokenOrderRatherThanValidatorKind()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var pathOption = new Option<string>("--path");
        var formatOption = new VbaDevStringOption("--format", [], ["text"]);
        command.Add(pathOption);
        command.Add(formatOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(pathOption);

        var result = Invoke(
            root,
            rules,
            ["probe", "--path", "", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--path' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RelationshipCannotReferenceTheSameOptionTwice()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var option = new Option<bool>("--flag");
        command.Add(option);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            rules.Requires(command, option, option));

        Assert.Equal(
            "A grammar relationship cannot reference the same option twice.",
            exception.Message);
    }

    [Fact]
    public void RepeatedRelationshipTriggerUsesItsLeftmostOccurrence()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var repeatedOption = new Option<bool>("--repeated");
        var earlierOption = new Option<bool>("--earlier");
        var firstRequirement = new Option<bool>("--first-requirement");
        var secondRequirement = new Option<bool>("--second-requirement");
        command.Add(repeatedOption);
        command.Add(earlierOption);
        command.Add(firstRequirement);
        command.Add(secondRequirement);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, repeatedOption, firstRequirement);
        rules.Requires(command, earlierOption, secondRequirement);

        var result = Invoke(
            root,
            rules,
            ["probe", "--repeated", "--earlier", "--repeated"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--repeated' requires option '--first-requirement'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void EarlierValueFailureSuppressesRelationshipsAndIntentBinding()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var formatOption = new VbaDevStringOption("--format", [], ["text"]);
        var triggerOption = new Option<bool>("--trigger");
        var requiredOption = new Option<bool>("--required");
        command.Add(formatOption);
        command.Add(triggerOption);
        command.Add(requiredOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, triggerOption, requiredOption);
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(
            root,
            rules,
            ["probe", "--format", "json", "--trigger"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--format' does not accept value 'json'. Accepted values: text.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, bindingAttempts);
    }

    [Fact]
    public void ParticipantValueFailureSuppressesItsRelationshipAndIntentBinding()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var triggerOption = new Option<string>("--trigger");
        var requiredOption = new Option<bool>("--required");
        command.Add(triggerOption);
        command.Add(requiredOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(triggerOption);
        rules.Requires(command, triggerOption, requiredOption);
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(root, rules, ["probe", "--trigger", ""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--trigger' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, bindingAttempts);
    }

    [Fact]
    public void RelationshipFailureSuppressesIntentBinding()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var triggerOption = new Option<bool>("--trigger");
        var requiredOption = new Option<bool>("--required");
        command.Add(triggerOption);
        command.Add(requiredOption);
        command.SetAction(_ => 0);
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, triggerOption, requiredOption);
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(root, rules, ["probe", "--trigger"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--trigger' requires option '--required'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, bindingAttempts);
    }

    [Fact]
    public void RelationshipSelectionUsesTokenOrderBeforeRelationshipKind()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var conflictFirst = new Option<bool>("--conflict-first");
        var conflictSecond = new Option<bool>("--conflict-second");
        var requiresTrigger = new Option<bool>("--requires-trigger");
        var requirement = new Option<bool>("--requirement");
        command.Add(conflictFirst);
        command.Add(conflictSecond);
        command.Add(requiresTrigger);
        command.Add(requirement);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, requiresTrigger, requirement);
        rules.Conflicts(command, conflictFirst, conflictSecond);

        var result = Invoke(
            root,
            rules,
            ["probe", "--conflict-first", "--conflict-second", "--requires-trigger"]);

        Assert.Equal(
            $"Error: Options '--conflict-first' and '--conflict-second' cannot be used together.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RelationshipSelectionUsesKindBeforeDeclarationOrderForRemainingTies()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var trigger = new Option<bool>("--trigger");
        var conflict = new Option<bool>("--conflict");
        var pair = new Option<bool>("--pair");
        var requirement = new Option<bool>("--requirement");
        command.Add(trigger);
        command.Add(conflict);
        command.Add(pair);
        command.Add(requirement);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.AllOrNone(command, trigger, pair);
        rules.Conflicts(command, trigger, conflict);
        rules.Requires(command, trigger, requirement);

        var result = Invoke(root, rules, ["probe", "--trigger", "--conflict"]);

        Assert.Equal(
            $"Error: Option '--trigger' requires option '--requirement'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RelationshipSelectionUsesDeclarationOrderAsTheFinalTieBreak()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var trigger = new Option<bool>("--trigger");
        var firstRequirement = new Option<bool>("--first-requirement");
        var secondRequirement = new Option<bool>("--second-requirement");
        command.Add(trigger);
        command.Add(firstRequirement);
        command.Add(secondRequirement);
        command.SetAction(_ => 0);
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, trigger, secondRequirement);
        rules.Requires(command, trigger, firstRequirement);

        var result = Invoke(root, rules, ["probe", "--trigger"]);

        Assert.Equal(
            $"Error: Option '--trigger' requires option '--second-requirement'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RequiredOptionsWithoutTriggersUseDisplayedHelpOrder()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var firstOption = new Option<string>("--first") { Required = true };
        var secondOption = new Option<string>("--second") { Required = true };
        command.Add(firstOption);
        command.Add(secondOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(root, new VbaDevGrammarFailureRules(), ["probe"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--first' is required for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void TriggeredMissingOptionValuePrecedesUntriggeredRequiredOption()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var firstOption = new Option<int?>("--first") { Required = true };
        var secondOption = new Option<int?>("--second");
        command.Add(firstOption);
        command.Add(secondOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--second"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--second' requires a value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void TriggeredMissingOptionValuePrecedesUntriggeredRequiredArgument()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var inputArgument = new Argument<string>("input");
        var valueOption = new Option<string>("--value");
        command.Add(inputArgument);
        command.Add(valueOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--value"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--value' requires a value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void CardinalityFailurePrecedesAnEarlierValueFailure()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var formatOption = new VbaDevStringOption("--format", [], ["text"]);
        var requiredOption = new Option<string>("--required") { Required = true };
        command.Add(formatOption);
        command.Add(requiredOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--required' is required for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void GrammarValidInvocationBindsAndExecutesExactlyOnce()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var valueOption = new Option<string>("--value") { Required = true };
        command.Add(valueOption);
        var bindingAttempts = 0;
        var actionCount = 0;
        root.Add(command);
        var rules = new VbaDevGrammarFailureRules();
        ProbeIntent? producedIntent = null;
        ProbeIntent? executedIntent = null;
        var binding = rules.BindIntent(command, parseResult =>
        {
            bindingAttempts++;
            producedIntent = new ProbeIntent(parseResult.GetValue(valueOption)!);
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Bound(producedIntent);
        });
        command.SetAction(parseResult =>
        {
            actionCount++;
            executedIntent = binding.GetRequiredIntent(parseResult);
            return 0;
        });

        var result = Invoke(root, rules, ["probe", "--value", "bound-once"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
        Assert.Equal(1, bindingAttempts);
        Assert.Equal(1, actionCount);
        Assert.Same(producedIntent, executedIntent);
        Assert.Equal("bound-once", executedIntent!.Value);
    }

    [Fact]
    public void RouterUsesTheRuleSnapshotTakenAtConstruction()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var pathOption = new Option<string>("--path");
        var actionCount = 0;
        command.Add(pathOption);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);
        var cancellationTransportOption = AddCancellationTransport(root);
        var rules = new VbaDevGrammarFailureRules();
        var router = new VbaDevGrammarFailureRouter(root, rules);
        rules.RequireNonEmpty(pathOption);
        var commandLine = new VbaDevCommandLine(new VbaDevCommandGraph(
            root,
            cancellationTransportOption,
            [],
            router));

        var result = Invoke(commandLine, ["probe", "--path", ""]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, actionCount);
    }

    [Fact]
    public void UnknownTokenEscapesLineSeparatorsInTheTwoLineContract()
    {
        var root = new RootCommand();

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["unknown\r\nvalue"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Unrecognized token 'unknown\\r\\nvalue' for command 'vba-dev'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(2, result.StandardError.Split(Environment.NewLine).Length - 1);
    }

    [Fact]
    public void CommandGraphRejectsARouterFromAnotherRoot()
    {
        var root = new RootCommand();
        var cancellationTransportOption = AddCancellationTransport(root);
        var otherRoot = new RootCommand();
        var router = new VbaDevGrammarFailureRouter(otherRoot);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new VbaDevCommandGraph(root, cancellationTransportOption, [], router));

        Assert.Equal(
            "The grammar failure router does not own the completed root graph.",
            exception.Message);
    }

    [Fact]
    public void RepeatedOptionOccurrencesAreNotMistakenForUnmatchedTokens()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var flagOption = new Option<bool>("--flag");
        command.Add(flagOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--flag", "--flag", "--unknown", "--", "--flag"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Unrecognized token '--unknown' for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RepeatedRecursiveParentOptionOccurrencesPreserveTheActualUnmatchedToken()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            [
                "probe",
                "--cancellation-transport",
                "stdin-v1",
                "--cancellation-transport",
                "stdin-v1",
                "--unknown",
                "--",
                "--cancellation-transport"
            ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Unrecognized token '--unknown' for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void OptionLikeTokenWithDifferentCaseRemainsUnmatched()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        command.Add(new Option<bool>("--flag"));
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--FLAG"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Unrecognized token '--FLAG' for command 'vba-dev probe'.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void RepeatedScalarOptionValuesRespectTheAggregateMaximum()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var countOption = new Option<int>("--count");
        var actionCount = 0;
        command.Add(countOption);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--count", "1", "--count", "2"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--count' accepts at most 1 values.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void RepeatedScalarOptionReportsTheTrailingMissingValue()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var countOption = new Option<int>("--count");
        var actionCount = 0;
        command.Add(countOption);
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--count", "1", "--count"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--count' requires a value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void RepeatedScalarCardinalityUsesTheLeftmostOccurrenceDefect()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var countOption = new Option<int>("--count");
        command.Add(countOption);
        command.SetAction(_ => 0);
        root.Add(command);

        var result = Invoke(
            root,
            new VbaDevGrammarFailureRules(),
            ["probe", "--count", "--count=1", "--count=2"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--count' requires a value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
    }

    [Fact]
    public void StandaloneFailureSuppressesIntentBindingInTheSamePhase()
    {
        var root = new RootCommand();
        var modeOption = new Option<bool>("--mode") { Arity = ArgumentArity.Zero };
        root.Add(modeOption);
        root.SetAction(_ => 0);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireStandalone(modeOption);
        rules.BindIntent<ProbeIntent>(root, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(
            root,
            rules,
            ["--mode", "--cancellation-transport", "stdin-v1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            $"Error: Option '--mode' cannot be combined with other arguments.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, bindingAttempts);
    }

    [Fact]
    public void StandaloneOptionParsingFailurePrecedesStandaloneBindingFailure()
    {
        var root = new RootCommand();
        var modeOption = new Option<bool>("--mode") { Arity = ArgumentArity.Zero };
        modeOption.Validators.Add(result => result.AddError("validator sentinel"));
        root.Add(modeOption);
        var actionCount = 0;
        root.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireStandalone(modeOption);
        rules.BindIntent<ProbeIntent>(root, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(
            root,
            rules,
            ["--mode", "--cancellation-transport", "stdin-v1"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--mode' is invalid.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.DoesNotContain("validator sentinel", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, bindingAttempts);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void ValidatorMessageNeverLeaksPastTheCanonicalBoundary()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        var triggerOption = new Option<bool>("--trigger");
        triggerOption.Validators.Add(result => result.AddError("validator sentinel"));
        var requiredOption = new Option<bool>("--required");
        command.Add(triggerOption);
        command.Add(requiredOption);
        var actionCount = 0;
        command.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });
        root.Add(command);
        var bindingAttempts = 0;
        var rules = new VbaDevGrammarFailureRules();
        rules.Requires(command, triggerOption, requiredOption);
        rules.BindIntent<ProbeIntent>(command, _ =>
        {
            bindingAttempts++;
            return VbaDevGrammarIntentBindResult<ProbeIntent>.Unbound;
        });

        var result = Invoke(root, rules, ["probe", "--trigger"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--trigger' is invalid.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev probe --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.DoesNotContain("validator sentinel", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, bindingAttempts);
        Assert.Equal(0, actionCount);
    }

    [Fact]
    public void SymmetricRelationshipsRejectReverseDuplicateRegistrations()
    {
        var command = new Command("probe");
        var first = new Option<bool>("--first");
        var second = new Option<bool>("--second");
        var conflictRules = new VbaDevGrammarFailureRules();
        conflictRules.Conflicts(command, first, second);
        var pairRules = new VbaDevGrammarFailureRules();
        pairRules.AllOrNone(command, first, second);

        var conflictException = Assert.Throws<InvalidOperationException>(() =>
            conflictRules.Conflicts(command, second, first));
        var pairException = Assert.Throws<InvalidOperationException>(() =>
            pairRules.AllOrNone(command, second, first));

        Assert.Contains("more than once", conflictException.Message, StringComparison.Ordinal);
        Assert.Contains("more than once", pairException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterRejectsRulesForSymbolsOutsideTheCompletedGraph()
    {
        var root = new RootCommand();
        var detachedOption = new Option<string>("--detached");
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(detachedOption);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new VbaDevGrammarFailureRouter(root, rules));

        Assert.Equal(
            "Grammar rule symbol '--detached' is not part of the completed graph.",
            exception.Message);
    }

    [Fact]
    public void StandaloneRulesAreLimitedToZeroArityRootOptions()
    {
        var valueRoot = new RootCommand();
        var valueOption = new Option<string>("--mode");
        valueRoot.Add(valueOption);
        var valueRules = new VbaDevGrammarFailureRules();
        valueRules.RequireStandalone(valueOption);

        var valueException = Assert.Throws<InvalidOperationException>(() =>
            new VbaDevGrammarFailureRouter(valueRoot, valueRules));

        var leafRoot = new RootCommand();
        var command = new Command("probe");
        var leafOption = new Option<bool>("--mode");
        command.Add(leafOption);
        leafRoot.Add(command);
        var leafRules = new VbaDevGrammarFailureRules();
        leafRules.RequireStandalone(leafOption);

        var leafException = Assert.Throws<InvalidOperationException>(() =>
            new VbaDevGrammarFailureRouter(leafRoot, leafRules));

        Assert.Equal(
            "Standalone option '--mode' must be a zero-arity root option.",
            valueException.Message);
        Assert.Equal(
            "Standalone option '--mode' must be a zero-arity root option.",
            leafException.Message);
    }

    [Fact]
    public void RulesRejectDuplicateValueAndIntentRegistrations()
    {
        var command = new Command("probe");
        var pathOption = new Option<string>("--path");
        var rules = new VbaDevGrammarFailureRules();
        rules.RequireNonEmpty(pathOption);
        rules.BindIntent(
            command,
            _ => VbaDevGrammarIntentBindResult<ProbeIntent>.Bound(new ProbeIntent("first")));

        var valueException = Assert.Throws<InvalidOperationException>(() =>
            rules.RequireNonEmpty(pathOption));
        var bindingException = Assert.Throws<InvalidOperationException>(() =>
            rules.BindIntent(
                command,
                _ => VbaDevGrammarIntentBindResult<ProbeIntent>.Bound(new ProbeIntent("second"))));

        Assert.Contains("more than one non-empty rule", valueException.Message, StringComparison.Ordinal);
        Assert.Contains("more than one intent binding rule", bindingException.Message, StringComparison.Ordinal);
    }

    private static GrammarInvocationResult Invoke(
        RootCommand root,
        VbaDevGrammarFailureRules rules,
        IReadOnlyList<string> arguments)
    {
        var cancellationTransportOption = AddCancellationTransport(root);
        var commandLine = new VbaDevCommandLine(new VbaDevCommandGraph(
            root,
            cancellationTransportOption,
            [],
            new VbaDevGrammarFailureRouter(root, rules)));

        return Invoke(commandLine, arguments);
    }

    private static GrammarInvocationResult Invoke(
        VbaDevCommandLine commandLine,
        IReadOnlyList<string> arguments)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = commandLine.InvokeAsync(
                arguments,
                standardOutput,
                standardError,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new GrammarInvocationResult(
            exitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private static Option<string> AddCancellationTransport(RootCommand root)
    {
        var option = new Option<string>("--cancellation-transport")
        {
            Hidden = true,
            Recursive = true
        };
        root.Add(option);
        return option;
    }

    private sealed record GrammarInvocationResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record ProbeIntent(string Value);
}
