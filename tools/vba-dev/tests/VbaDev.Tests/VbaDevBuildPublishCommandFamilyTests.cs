using System.CommandLine;
using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevBuildPublishCommandFamilyTests
{
    [Fact]
    public void BuildPublishFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevBuildPublishCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void FamilyRegistersActualLeavesRulesAndEveryClosedIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var family = VbaDevBuildPublishCommandFamily.Create(
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());
        family.RegisterBuild(root);
        family.RegisterPublish(root);
        var ruleSnapshot = rules.CreateSnapshot(root.Subcommands.Prepend(root));
        var router = new VbaDevGrammarFailureRouter(root, rules);

        Assert.Same(
            family.BuildCommand,
            Assert.Single(root.Subcommands, command => command.Name == "build"));
        Assert.Same(
            family.PublishCommand,
            Assert.Single(root.Subcommands, command => command.Name == "publish"));
        Assert.Equal(
            ["build", "publish"],
            capabilities.Select(capability => capability.CommandPath));
        Assert.Same(family.BuildCommand, capabilities[0].Command);
        Assert.Same(family.PublishCommand, capabilities[1].Command);
        Assert.Equal(["3.0", "1.0"], capabilities.Select(capability => capability.OutputSchemaVersion));
        Assert.NotNull(family.BuildCommand.Action);
        Assert.NotNull(family.PublishCommand.Action);
        Assert.Equal(
            ["--project", "--document", "--source-snapshot", "--output"],
            family.BuildCommand.Options.Select(option => option.Name));
        Assert.Equal(
            ["--project", "--document"],
            family.PublishCommand.Options.Select(option => option.Name));
        Assert.DoesNotContain(
            family.PublishCommand.Options,
            option => option.Name is "--output" or "--format");
        Assert.Same(family.BuildProjectOption, family.BuildCommand.Options[0]);
        Assert.Same(family.BuildDocumentOption, family.BuildCommand.Options[1]);
        Assert.Same(family.BuildSourceSnapshotOption, family.BuildCommand.Options[2]);
        Assert.Same(family.BuildOutputOption, family.BuildCommand.Options[3]);
        Assert.Same(family.PublishProjectOption, family.PublishCommand.Options[0]);
        Assert.Same(family.PublishDocumentOption, family.PublishCommand.Options[1]);
        Assert.Equal(["-d"], family.BuildDocumentOption.Aliases);
        Assert.Equal(["-o"], family.BuildOutputOption.Aliases);
        Assert.Equal(["-d"], family.PublishDocumentOption.Aliases);
        Assert.Equal(
            [
                family.BuildProjectOption,
                family.BuildDocumentOption,
                family.BuildSourceSnapshotOption,
                family.BuildOutputOption,
                family.PublishProjectOption,
                family.PublishDocumentOption
            ],
            ruleSnapshot.NonEmptyOptions.Select(rule => rule.Option));
        var relationship = Assert.Single(ruleSnapshot.Relationships);
        Assert.Equal(VbaDevGrammarRelationshipKind.AllOrNone, relationship.Kind);
        Assert.Same(family.BuildCommand, relationship.Command);
        Assert.Same(family.BuildSourceSnapshotOption, relationship.First);
        Assert.Same(family.BuildOutputOption, relationship.Second);

        var persistentParse = ParseSuccessfully(
            root,
            router,
            ["build", "--project", "project", "--document", "Book1"]);
        var persistentIntent = Assert.IsType<VbaDevBuildCommandIntent.PersistentBuild>(
            family.BuildIntentBinding.GetRequiredIntent(persistentParse));
        Assert.Equal("project", persistentIntent.ProjectRoot);
        Assert.Equal("Book1", persistentIntent.DocumentName);

        var snapshotParse = ParseSuccessfully(
            root,
            router,
            [
                "build",
                "--project",
                "project",
                "--document",
                "Book1",
                "--source-snapshot",
                "snapshot",
                "-o",
                "Book1.xlsm"
            ]);
        var snapshotIntent = Assert.IsType<VbaDevBuildCommandIntent.SourceSnapshotBuild>(
            family.BuildIntentBinding.GetRequiredIntent(snapshotParse));
        Assert.Equal("project", snapshotIntent.ProjectRoot);
        Assert.Equal("Book1", snapshotIntent.DocumentName);
        Assert.Equal("snapshot", snapshotIntent.SourceSnapshotDirectory);
        Assert.Equal("Book1.xlsm", snapshotIntent.OutputWorkbook);

        var publishParse = ParseSuccessfully(
            root,
            router,
            ["publish", "--project", "project", "--document", "Book1"]);
        var publishIntent = family.PublishIntentBinding.GetRequiredIntent(publishParse);
        Assert.Equal("project", publishIntent.ProjectRoot);
        Assert.Equal("Book1", publishIntent.DocumentName);
        Assert.Same(
            publishIntent,
            family.PublishIntentBinding.GetRequiredIntent(publishParse));
        Assert.DoesNotContain(
            typeof(VbaDevBuildCommandIntent).Assembly.GetTypes()
                .Where(type => type == typeof(VbaDevBuildCommandIntent) ||
                    type.IsSubclassOf(typeof(VbaDevBuildCommandIntent)))
                .SelectMany(type => type.GetProperties()),
            property => property.PropertyType == typeof(bool));
    }

    [Theory]
    [InlineData("build --", new[] { "--project", "--document", "--source-snapshot", "--output" })]
    [InlineData("publish --", new[] { "--project", "--document" })]
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
        if (commandLine.StartsWith("publish", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("--output", suggestions);
            Assert.DoesNotContain("--format", suggestions);
        }
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
