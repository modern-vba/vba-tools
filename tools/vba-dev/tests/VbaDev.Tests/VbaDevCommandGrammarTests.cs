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
        Assert.Single(fields, field => field.FieldType == typeof(VbaDevCommandGraph));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(RootCommand));
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
            ("build", "--document", "-d"),
            ("test", "--document", "-d"),
            ("test", "--format", "-f"),
            ("publish", "--document", "-d"),
            ("export", "--document", "-d"),
            ("capabilities", "--format", "-f")
        ];

        foreach (var expectation in expected)
        {
            var command = ResolveCommand(graph.RootCommand, expectation.CommandPath);
            var option = Assert.Single(
                command.Options,
                candidate => candidate.Name.Equals(expectation.OptionName, StringComparison.Ordinal));

            Assert.Equal([expectation.Alias], option.Aliases);
        }
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
