using System.CommandLine;
using System.Reflection;
using VbaDev.Cli;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevCommandGrammarTests
{
    [Fact]
    public void CommandGrammarConstructsOneReusableRootAndPrivateCancellationSymbol()
    {
        var commandLine = CommandLineTestFactory.Create();

        var graph = commandLine.CommandGraph;

        Assert.Same(graph, commandLine.CommandGraph);
        Assert.Contains(
            graph.RootCommand.Options,
            option => ReferenceEquals(option, graph.CancellationTransportOption));
        Assert.True(graph.CancellationTransportOption.Hidden);
        Assert.True(graph.CancellationTransportOption.Recursive);

        var fields = typeof(VbaDevCommandLine).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields);
        Assert.Single(fields, field => field.FieldType == typeof(VbaDevCommandGraph));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(RootCommand));
    }

    [Fact]
    public void EveryPublicLeafHasExactlyOneSealedInternalFamilyOwner()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;
        var expected = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["new excel"] = typeof(VbaDevProjectCreationCommandFamily),
            ["common-module add"] = typeof(VbaDevCommonModuleCommandFamily),
            ["common-module list"] = typeof(VbaDevCommonModuleCommandFamily),
            ["common-module update"] = typeof(VbaDevCommonModuleCommandFamily),
            ["completions script pwsh"] = typeof(VbaDevContractCommandFamily),
            ["reference add"] = typeof(VbaDevReferenceCommandFamily),
            ["reference list"] = typeof(VbaDevReferenceCommandFamily),
            ["reference remove"] = typeof(VbaDevReferenceCommandFamily),
            ["host-event list"] = typeof(VbaDevHostEventCommandFamily),
            ["build"] = typeof(VbaDevBuildPublishCommandFamily),
            ["test"] = typeof(VbaDevTestCommandFamily),
            ["publish"] = typeof(VbaDevBuildPublishCommandFamily),
            ["export"] = typeof(VbaDevImportExportCommandFamily),
            ["import"] = typeof(VbaDevImportExportCommandFamily),
            ["check"] = typeof(VbaDevInspectionCommandFamily),
            ["doctor"] = typeof(VbaDevInspectionCommandFamily),
            ["capabilities"] = typeof(VbaDevContractCommandFamily)
        };

        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            EnumerateLeafPaths(graph.RootCommand).Order(StringComparer.Ordinal));
        Assert.Equal(expected.Count, graph.FamilyOwnershipRegistrations.Count);
        Assert.Equal(
            graph.FamilyOwnershipRegistrations.Count,
            graph.FamilyOwnershipRegistrations
                .Select(registration => registration.Command)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        foreach (var expectation in expected)
        {
            var command = ResolveCommand(graph.RootCommand, expectation.Key);
            var registration = Assert.Single(
                graph.FamilyOwnershipRegistrations,
                candidate => ReferenceEquals(candidate.Command, command));

            Assert.Equal(expectation.Value, registration.FamilyType);
            Assert.True(registration.FamilyType.IsSealed);
            Assert.False(registration.FamilyType.IsPublic);
        }
    }

    [Fact]
    public void AdvertisedCapabilitiesAreUniqueLeafRegistrationsOnTheCompletedGraph()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build"] = "1.0",
            ["common-module add"] = "1.0",
            ["common-module list"] = "1.0",
            ["common-module update"] = "1.0",
            ["doctor"] = "1.0",
            ["export"] = "1.0",
            ["host-event list"] = "1.0",
            ["import"] = "1.0",
            ["new excel"] = "1.0",
            ["publish"] = "1.0",
            ["reference add"] = "1.0",
            ["reference list"] = "1.0",
            ["reference remove"] = "1.0",
            ["test"] = "1.2"
        };

        Assert.Equal(expected.Count, graph.CapabilityRegistrations.Count);
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            graph.CapabilityRegistrations
                .Select(registration => registration.CommandPath)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            graph.CapabilityRegistrations.Count,
            graph.CapabilityRegistrations
                .Select(registration => registration.Command)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count());

        foreach (var registration in graph.CapabilityRegistrations)
        {
            var command = ResolveCommand(graph.RootCommand, registration.CommandPath);

            Assert.Same(registration.Command, command);
            Assert.Empty(command.Subcommands);
            Assert.NotNull(command.Action);
            Assert.Equal(expected[registration.CommandPath], registration.OutputSchemaVersion);
        }

        var leafPaths = EnumerateLeafPaths(graph.RootCommand).ToArray();
        Assert.Equal(17, leafPaths.Length);
        Assert.Equal(
            ["capabilities", "check", "completions script pwsh"],
            leafPaths
                .Except(expected.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CommandGraphKeepsCanonicalArgumentAndRequiredOptionCardinality()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;
        var expectedArguments = new Dictionary<string, (int Minimum, int Maximum)>(
            StringComparer.Ordinal)
        {
            ["common-module add <modules>"] =
                (1, ArgumentArity.OneOrMore.MaximumNumberOfValues),
            ["reference add <references>"] =
                (1, ArgumentArity.OneOrMore.MaximumNumberOfValues),
            ["reference remove <references>"] =
                (1, ArgumentArity.OneOrMore.MaximumNumberOfValues)
        };
        var actualArguments = EnumerateLeafPaths(graph.RootCommand)
            .Select(path => (Path: path, Command: ResolveCommand(graph.RootCommand, path)))
            .SelectMany(entry => entry.Command.Arguments.Select(argument => new
            {
                Key = $"{entry.Path} <{argument.Name}>",
                argument.Arity.MinimumNumberOfValues,
                argument.Arity.MaximumNumberOfValues
            }))
            .ToDictionary(
                entry => entry.Key,
                entry => (entry.MinimumNumberOfValues, entry.MaximumNumberOfValues),
                StringComparer.Ordinal);

        Assert.Equal(expectedArguments, actualArguments);

        var requiredOptions = EnumerateLeafPaths(graph.RootCommand)
            .Select(path => (Path: path, Command: ResolveCommand(graph.RootCommand, path)))
            .SelectMany(entry => entry.Command.Options
                .Where(option => option.Required)
                .Select(option => $"{entry.Path} {option.Name}"))
            .Order(StringComparer.Ordinal);
        Assert.Equal(["import --from", "import --to"], requiredOptions);
    }

    [Fact]
    public void CommandGraphKeepsCanonicalOptionCardinality()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;
        var expectedOptions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["new excel"] = ["--format", "--name", "--output"],
            ["common-module add"] = ["--document", "--force", "--format", "--project"],
            ["common-module list"] = ["--document", "--format", "--project"],
            ["common-module update"] = ["--format", "--project"],
            ["completions script pwsh"] = [],
            ["reference add"] = ["--document", "--format", "--project"],
            ["reference list"] =
                ["--available", "--document", "--format", "--no-resolve", "--project"],
            ["reference remove"] = ["--document", "--format", "--project"],
            ["host-event list"] = ["--format"],
            ["build"] = ["--document", "--output", "--project", "--source-snapshot"],
            ["test"] =
            [
                "--document",
                "--format",
                "--module",
                "--no-build",
                "--procedure",
                "--project",
                "--source-snapshot",
                "--timeout-seconds"
            ],
            ["publish"] = ["--document", "--project"],
            ["export"] = ["--document", "--from", "--project", "--to"],
            ["import"] = ["--from", "--to"],
            ["check"] = ["--project"],
            ["doctor"] = ["--format", "--project", "--scope"],
            ["capabilities"] = ["--format"]
        };
        var expectedFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "common-module add --force",
            "reference list --available",
            "reference list --no-resolve",
            "test --no-build"
        };
        var actual = EnumerateLeafPaths(graph.RootCommand)
            .Select(path => (Path: path, Command: ResolveCommand(graph.RootCommand, path)))
            .SelectMany(entry => entry.Command.Options.Select(option => new
            {
                Key = $"{entry.Path} {option.Name}",
                option.Arity.MinimumNumberOfValues,
                option.Arity.MaximumNumberOfValues,
                option.AllowMultipleArgumentsPerToken
            }))
            .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var expectedKeys = expectedOptions
            .SelectMany(entry => entry.Value.Select(option => $"{entry.Key} {option}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedOptions.Keys.Order(StringComparer.Ordinal),
            EnumerateLeafPaths(graph.RootCommand).Order(StringComparer.Ordinal));
        Assert.Equal(expectedKeys, actual.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in actual)
        {
            var expectedMinimum = expectedFlags.Contains(entry.Key) ? 0 : 1;
            Assert.Equal(expectedMinimum, entry.Value.MinimumNumberOfValues);
            Assert.Equal(1, entry.Value.MaximumNumberOfValues);
            Assert.False(entry.Value.AllowMultipleArgumentsPerToken);
        }
    }

    [Fact]
    public void EveryDeclaredRelationshipStopsBeforeItsActualLeafAction()
    {
        var commandLine = CommandLineTestFactory.Create();
        (string CommandPath, string[] Arguments)[] invalidRelationships =
        [
            ("build", ["build", "--source-snapshot", "source"]),
            ("test", ["test", "--procedure", "Procedure1"]),
            ("test", ["test", "--source-snapshot", "source", "--no-build"]),
            ("reference list", ["reference", "list", "--available", "--no-resolve"]),
            ("export", ["export", "--from", "book.xlsm", "--project", "project"]),
            ("export", ["export", "--from", "book.xlsm", "--document", "Book1"]),
            ("doctor", ["doctor", "--scope", "environment", "--project", "project"])
        ];

        foreach (var invalidRelationship in invalidRelationships)
        {
            var actionCount = 0;
            ResolveCommand(commandLine.CommandGraph.RootCommand, invalidRelationship.CommandPath)
                .SetAction(_ =>
                {
                    actionCount++;
                    return 0;
                });

            var result = commandLine.Run(invalidRelationship.Arguments);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.StartsWith("Error: ", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(0, actionCount);
        }
    }

    [Fact]
    public void CommandGraphKeepsTheCanonicalShortAliasesBesideTheirOptions()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;
        (string CommandPath, string OptionName, string Alias)[] expected =
        [
            ("new excel", "--name", "-n"),
            ("new excel", "--output", "-o"),
            ("new excel", "--format", "-f"),
            ("common-module add", "--document", "-d"),
            ("common-module add", "--format", "-f"),
            ("common-module list", "--document", "-d"),
            ("common-module list", "--format", "-f"),
            ("common-module update", "--format", "-f"),
            ("reference add", "--document", "-d"),
            ("reference add", "--format", "-f"),
            ("reference list", "--document", "-d"),
            ("reference list", "--format", "-f"),
            ("reference remove", "--document", "-d"),
            ("reference remove", "--format", "-f"),
            ("host-event list", "--format", "-f"),
            ("doctor", "--format", "-f"),
            ("build", "--document", "-d"),
            ("build", "--output", "-o"),
            ("test", "--document", "-d"),
            ("test", "--format", "-f"),
            ("publish", "--document", "-d"),
            ("export", "--document", "-d"),
            ("capabilities", "--format", "-f")
        ];

        var actual = EnumerateLeafPaths(graph.RootCommand)
            .Select(path => (Path: path, Command: ResolveCommand(graph.RootCommand, path)))
            .SelectMany(entry => entry.Command.Options.SelectMany(option =>
                option.Aliases.Select(alias =>
                    (CommandPath: entry.Path, OptionName: option.Name, Alias: alias))))
            .OrderBy(entry => entry.CommandPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.OptionName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Alias, StringComparer.Ordinal);
        Assert.Equal(
            expected
                .OrderBy(entry => entry.CommandPath, StringComparer.Ordinal)
                .ThenBy(entry => entry.OptionName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Alias, StringComparer.Ordinal),
            actual);

        foreach (var expectation in expected)
        {
            var command = ResolveCommand(graph.RootCommand, expectation.CommandPath);
            var option = Assert.Single(
                command.Options,
                candidate => candidate.Name.Equals(expectation.OptionName, StringComparison.Ordinal));

            Assert.Equal([expectation.Alias], option.Aliases);
        }
    }

    [Fact]
    public void CommandGraphKeepsTheCanonicalRootCommandOrder()
    {
        var graph = CommandLineTestFactory.Create().CommandGraph;

        Assert.Equal(
            [
                "new",
                "common-module",
                "completions",
                "reference",
                "host-event",
                "build",
                "test",
                "publish",
                "export",
                "import",
                "check",
                "doctor",
                "capabilities"
            ],
            graph.RootCommand.Subcommands.Select(command => command.Name));
    }

    [Fact]
    public void BuildTestAndPublishKeepTheirRootDisplayOrder()
    {
        var commandNames = CommandLineTestFactory.Create()
            .CommandGraph
            .RootCommand
            .Subcommands
            .Select(command => command.Name)
            .ToArray();

        Assert.True(Array.IndexOf(commandNames, "build") < Array.IndexOf(commandNames, "test"));
        Assert.True(Array.IndexOf(commandNames, "test") < Array.IndexOf(commandNames, "publish"));
    }

    private static Command ResolveCommand(RootCommand rootCommand, string commandPath)
    {
        Command current = rootCommand;
        foreach (var segment in commandPath.Split(' '))
        {
            current = Assert.Single(
                current.Subcommands,
                command => command.Name.Equals(segment, StringComparison.Ordinal));
        }

        return current;
    }

    private static IEnumerable<string> EnumerateLeafPaths(
        Command parent,
        string parentPath = "")
    {
        foreach (var command in parent.Subcommands)
        {
            var commandPath = string.IsNullOrEmpty(parentPath)
                ? command.Name
                : $"{parentPath} {command.Name}";
            if (command.Subcommands.Count == 0)
            {
                yield return commandPath;
                continue;
            }

            foreach (var descendant in EnumerateLeafPaths(command, commandPath))
            {
                yield return descendant;
            }
        }
    }
}
