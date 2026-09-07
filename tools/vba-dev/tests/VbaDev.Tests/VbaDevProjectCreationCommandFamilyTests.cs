using System.CommandLine;
using System.CommandLine.Parsing;
using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevProjectCreationCommandFamilyTests
{
    [Fact]
    public void ProjectCreationFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevProjectCreationCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void ProjectCreationFamilyOwnsTheActualNewExcelGrammarAndCapability()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();

        var family = VbaDevProjectCreationCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());

        Assert.Same(family.NewCommand, Assert.Single(root.Subcommands));
        Assert.Equal("new", family.NewCommand.Name);
        Assert.Same(family.ExcelCommand, Assert.Single(family.NewCommand.Subcommands));
        Assert.Equal("excel", family.ExcelCommand.Name);
        Assert.Equal(
            [family.NameOption, family.OutputOption, family.FormatOption],
            family.ExcelCommand.Options);
        Assert.Equal(["-n"], family.NameOption.Aliases);
        Assert.Equal(["-o"], family.OutputOption.Aliases);
        Assert.Equal(["-f"], family.FormatOption.Aliases);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.FormatOption).AcceptedValues);
        Assert.NotNull(family.ExcelCommand.Action);

        var capability = Assert.Single(capabilities);
        Assert.Same(family.ExcelCommand, capability.Command);
        Assert.Equal("new excel", capability.CommandPath);
        Assert.Equal("1.0", capability.OutputSchemaVersion);
    }

    [Fact]
    public void ProjectCreationFamilyBindsOmittedInputsToAClosedTextIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevProjectCreationCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>(),
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var parseResult = ParseSuccessfully(root, router, ["new", "excel"]);
        var intent = family.ExcelIntentBinding.GetRequiredIntent(parseResult);

        Assert.IsType<VbaDevProjectCreationValue.Omitted>(intent.ProjectName);
        Assert.IsType<VbaDevProjectCreationValue.Omitted>(intent.OutputDirectory);
        Assert.IsType<VbaDevProjectCreationOutputFormat.Text>(intent.Format);
        Assert.Same(intent, family.ExcelIntentBinding.GetRequiredIntent(parseResult));
    }

    [Fact]
    public void ProjectCreationFamilyPreservesExplicitEmptyInputsInAClosedJsonIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevProjectCreationCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>(),
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var intent = family.ExcelIntentBinding.GetRequiredIntent(
            ParseSuccessfully(
                root,
                router,
                ["new", "excel", "-n", "", "-o", "", "-f", "JSON"]));

        Assert.Equal(
            string.Empty,
            Assert.IsType<VbaDevProjectCreationValue.Specified>(intent.ProjectName).Value);
        Assert.Equal(
            string.Empty,
            Assert.IsType<VbaDevProjectCreationValue.Specified>(intent.OutputDirectory).Value);
        Assert.IsType<VbaDevProjectCreationOutputFormat.Json>(intent.Format);
    }

    [Fact]
    public void ProjectCreationFamilyKeepsIndependentNameAndOutputPresence()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevProjectCreationCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>(),
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var nameOnly = family.ExcelIntentBinding.GetRequiredIntent(
            ParseSuccessfully(root, router, ["new", "excel", "--name", "Project1"]));
        var outputOnly = family.ExcelIntentBinding.GetRequiredIntent(
            ParseSuccessfully(root, router, ["new", "excel", "--output", "Project2"]));

        Assert.Equal(
            "Project1",
            Assert.IsType<VbaDevProjectCreationValue.Specified>(nameOnly.ProjectName).Value);
        Assert.IsType<VbaDevProjectCreationValue.Omitted>(nameOnly.OutputDirectory);
        Assert.IsType<VbaDevProjectCreationValue.Omitted>(outputOnly.ProjectName);
        Assert.Equal(
            "Project2",
            Assert.IsType<VbaDevProjectCreationValue.Specified>(outputOnly.OutputDirectory).Value);
    }

    [Fact]
    public void InvalidProjectCreationFormatStopsBeforeTheActualLeafAction()
    {
        using var temp = TempDirectory.Create();
        var commandLine = CommandLineTestFactory.Create(temp.Path);
        var newCommand = Assert.Single(
            commandLine.CommandGraph.RootCommand.Subcommands,
            command => command.Name == "new");
        var excelCommand = Assert.Single(newCommand.Subcommands);
        var actionCount = 0;
        excelCommand.SetAction(_ =>
        {
            actionCount++;
            return 0;
        });

        var result = commandLine.Run(["new", "excel", "-f", "yaml"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--format' does not accept value 'yaml'. " +
            $"Accepted values: text, json.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev new excel --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, actionCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private static ParseResult ParseSuccessfully(
        RootCommand root,
        VbaDevGrammarFailureRouter router,
        IReadOnlyList<string> arguments)
    {
        var result = root.Parse(arguments);
        using var standardError = new StringWriter();

        Assert.False(router.TryWriteFailure(result, standardError));
        Assert.Empty(standardError.ToString());
        return result;
    }
}
