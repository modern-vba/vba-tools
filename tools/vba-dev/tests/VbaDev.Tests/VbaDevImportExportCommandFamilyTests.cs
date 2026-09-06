using System.CommandLine;
using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevImportExportCommandFamilyTests
{
    [Fact]
    public void ImportExportFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevImportExportCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void FamilyRegistersActualLeavesAndBindsEveryClosedIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var family = VbaDevImportExportCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities);
        var ruleSnapshot = rules.CreateSnapshot(root.Subcommands.Prepend(root));
        var router = new VbaDevGrammarFailureRouter(root, rules);

        Assert.Same(
            family.ExportCommand,
            Assert.Single(root.Subcommands, command => command.Name == "export"));
        Assert.Same(
            family.ImportCommand,
            Assert.Single(root.Subcommands, command => command.Name == "import"));
        Assert.Equal(
            ["export", "import"],
            capabilities.Select(capability => capability.CommandPath));
        Assert.Same(family.ExportCommand, capabilities[0].Command);
        Assert.Same(family.ImportCommand, capabilities[1].Command);
        Assert.All(capabilities, capability => Assert.Equal("1.0", capability.OutputSchemaVersion));
        Assert.NotNull(family.ExportCommand.Action);
        Assert.NotNull(family.ImportCommand.Action);
        Assert.Equal(
            ["--project", "--document", "--from", "--to"],
            family.ExportCommand.Options.Select(option => option.Name));
        Assert.Equal(
            ["--from", "--to"],
            family.ImportCommand.Options.Select(option => option.Name));
        Assert.Same(family.ExportProjectOption, family.ExportCommand.Options[0]);
        Assert.Same(family.ExportDocumentOption, family.ExportCommand.Options[1]);
        Assert.Same(family.ExportFromOption, family.ExportCommand.Options[2]);
        Assert.Same(family.ExportToOption, family.ExportCommand.Options[3]);
        Assert.Same(family.ImportFromOption, family.ImportCommand.Options[0]);
        Assert.Same(family.ImportToOption, family.ImportCommand.Options[1]);
        Assert.Equal(["-d"], family.ExportDocumentOption.Aliases);
        Assert.True(family.ImportFromOption.Required);
        Assert.True(family.ImportToOption.Required);
        Assert.Equal(
            [
                family.ExportProjectOption,
                family.ExportDocumentOption,
                family.ExportFromOption,
                family.ExportToOption,
                family.ImportFromOption,
                family.ImportToOption
            ],
            ruleSnapshot.NonEmptyOptions.Select(rule => rule.Option));
        Assert.Collection(
            ruleSnapshot.Relationships,
            relationship =>
            {
                Assert.Equal(VbaDevGrammarRelationshipKind.Conflicts, relationship.Kind);
                Assert.Same(family.ExportCommand, relationship.Command);
                Assert.Same(family.ExportFromOption, relationship.First);
                Assert.Same(family.ExportProjectOption, relationship.Second);
            },
            relationship =>
            {
                Assert.Equal(VbaDevGrammarRelationshipKind.Conflicts, relationship.Kind);
                Assert.Same(family.ExportCommand, relationship.Command);
                Assert.Same(family.ExportFromOption, relationship.First);
                Assert.Same(family.ExportDocumentOption, relationship.Second);
            });

        var importParse = ParseSuccessfully(
            root,
            router,
            ["import", "--from", "source", "--to", "target.xlsm"]);
        var importIntent = family.ImportIntentBinding.GetRequiredIntent(importParse);
        Assert.Equal("source", importIntent.SourceDirectory);
        Assert.Equal("target.xlsm", importIntent.TargetWorkbook);
        Assert.Same(
            importIntent,
            family.ImportIntentBinding.GetRequiredIntent(importParse));

        var projectParse = ParseSuccessfully(
            root,
            router,
            ["export", "--project", "project", "--document", "Book1", "--to", "source"]);
        var projectIntent = Assert.IsType<VbaDevExportCommandIntent.ProjectDocument>(
            family.ExportIntentBinding.GetRequiredIntent(projectParse));
        Assert.Equal("project", projectIntent.ProjectRoot);
        Assert.Equal("Book1", projectIntent.DocumentName);
        Assert.Equal("source", projectIntent.DestinationDirectory);

        var explicitParse = ParseSuccessfully(
            root,
            router,
            ["export", "--from", "Book1.xlsm"]);
        var explicitIntent = Assert.IsType<VbaDevExportCommandIntent.ExplicitWorkbook>(
            family.ExportIntentBinding.GetRequiredIntent(explicitParse));
        Assert.Equal("Book1.xlsm", explicitIntent.SourceWorkbook);
        Assert.Null(explicitIntent.DestinationDirectory);
        Assert.DoesNotContain(
            typeof(VbaDevExportCommandIntent).Assembly.GetTypes()
                .Where(type => type == typeof(VbaDevExportCommandIntent) ||
                    type.IsSubclassOf(typeof(VbaDevExportCommandIntent)))
                .SelectMany(type => type.GetProperties()),
            property => property.PropertyType == typeof(bool));
    }

    [Theory]
    [InlineData("export --", new[] { "--project", "--document", "--from", "--to" })]
    [InlineData("import --", new[] { "--from", "--to" })]
    public void FamilyOptionsParticipateInStaticCompletion(
        string commandLine,
        string[] expectedOptions)
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(
            [$"[suggest:{commandLine.Length}]", commandLine]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.All(expectedOptions, option => Assert.Contains(option, suggestions));
    }

    private static ParseResult ParseSuccessfully(
        RootCommand root,
        VbaDevGrammarFailureRouter router,
        IReadOnlyList<string> args)
    {
        var parseResult = root.Parse(args);
        using var standardError = new StringWriter();

        Assert.False(router.TryWriteFailure(parseResult, standardError));
        Assert.Empty(standardError.ToString());
        return parseResult;
    }
}
