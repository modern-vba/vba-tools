using System.CommandLine;
using VbaDev.App.Projects;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevCommonModuleCommandFamilyTests
{
    [Fact]
    public void CommonModuleFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevCommonModuleCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void FamilyRegistersTheActualGraphRulesActionsAndCapabilities()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();

        var family = VbaDevCommonModuleCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());
        var snapshot = rules.CreateSnapshot(
            [
                root,
                family.CommonModuleCommand,
                family.AddCommand,
                family.ListCommand,
                family.UpdateCommand
            ]);

        Assert.Same(
            family.CommonModuleCommand,
            Assert.Single(root.Subcommands, command => command.Name == "common-module"));
        Assert.Equal(
            ["add", "list", "update"],
            family.CommonModuleCommand.Subcommands.Select(command => command.Name));
        Assert.Equal(
            ["common-module add", "common-module list", "common-module update"],
            capabilities.Select(capability => capability.CommandPath));
        Assert.All(capabilities, capability => Assert.Equal("1.0", capability.OutputSchemaVersion));
        Assert.Same(family.AddCommand, capabilities[0].Command);
        Assert.Same(family.ListCommand, capabilities[1].Command);
        Assert.Same(family.UpdateCommand, capabilities[2].Command);
        Assert.NotNull(family.AddCommand.Action);
        Assert.NotNull(family.ListCommand.Action);
        Assert.NotNull(family.UpdateCommand.Action);
        Assert.Equal(
            ["--project", "--document", "--force", "--format"],
            family.AddCommand.Options.Select(option => option.Name));
        Assert.Equal(
            ["--project", "--document", "--format"],
            family.ListCommand.Options.Select(option => option.Name));
        Assert.Equal(
            ["--project", "--format"],
            family.UpdateCommand.Options.Select(option => option.Name));
        Assert.Equal(1, family.AddModulesArgument.Arity.MinimumNumberOfValues);
        Assert.True(family.AddModulesArgument.Arity.MaximumNumberOfValues > 1);
        Assert.Equal(["-d"], family.AddDocumentOption.Aliases);
        Assert.Equal(["-f"], family.AddFormatOption.Aliases);
        Assert.Equal(["-d"], family.ListDocumentOption.Aliases);
        Assert.Equal(["-f"], family.ListFormatOption.Aliases);
        Assert.Equal(["-f"], family.UpdateFormatOption.Aliases);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.AddFormatOption).AcceptedValues);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.ListFormatOption).AcceptedValues);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.UpdateFormatOption).AcceptedValues);
        Assert.Same(
            family.AddModulesArgument,
            Assert.Single(snapshot.NonEmptyArguments).Argument);
        Assert.Empty(snapshot.Relationships);
    }

    [Fact]
    public void FamilyBindsListOrdinaryAddForcedAddAndUpdateIntents()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevCommonModuleCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>(),
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var listParse = ParseSuccessfully(
            root,
            router,
            [
                "common-module",
                "list",
                "--project",
                "project",
                "--document",
                "Book1",
                "--format",
                "JSON"
            ]);
        var list = family.ListIntentBinding.GetRequiredIntent(listParse);
        Assert.Equal("project", list.ProjectRoot);
        Assert.Equal("Book1", list.DocumentName);
        Assert.Equal("json", list.Format);
        Assert.Same(list, family.ListIntentBinding.GetRequiredIntent(listParse));

        var addParse = ParseSuccessfully(
            root,
            router,
            [
                "common-module",
                "add",
                "  Alpha  ",
                "Beta",
                "--project",
                "project",
                "--document",
                "Book1",
                "--format",
                "JSON"
            ]);
        var add = Assert.IsType<VbaDevCommonModuleAddCommandIntent.Ordinary>(
            family.AddIntentBinding.GetRequiredIntent(addParse));
        Assert.Equal("project", add.ProjectRoot);
        Assert.Equal("Book1", add.DocumentName);
        Assert.Equal(["  Alpha  ", "Beta"], add.ModuleNames);
        Assert.Equal("json", add.Format);
        Assert.Same(add, family.AddIntentBinding.GetRequiredIntent(addParse));

        var forcedParse = ParseSuccessfully(
            root,
            router,
            ["common-module", "add", "Feature", "--force"]);
        var forced = Assert.IsType<VbaDevCommonModuleAddCommandIntent.Forced>(
            family.AddIntentBinding.GetRequiredIntent(forcedParse));
        Assert.Null(forced.ProjectRoot);
        Assert.Null(forced.DocumentName);
        Assert.Equal(["Feature"], forced.ModuleNames);
        Assert.Equal("text", forced.Format);

        var updateParse = ParseSuccessfully(
            root,
            router,
            ["common-module", "update", "--project", "project", "--format", "JSON"]);
        var update = family.UpdateIntentBinding.GetRequiredIntent(updateParse);
        Assert.Equal("project", update.ProjectRoot);
        Assert.Equal("json", update.Format);
        Assert.Same(update, family.UpdateIntentBinding.GetRequiredIntent(updateParse));

        Assert.DoesNotContain(
            typeof(VbaDevCommonModuleAddCommandIntent).Assembly.GetTypes()
                .Where(type => type == typeof(VbaDevCommonModuleAddCommandIntent) ||
                    type.IsSubclassOf(typeof(VbaDevCommonModuleAddCommandIntent)))
                .SelectMany(type => type.GetProperties()),
            property => property.PropertyType == typeof(bool));
    }

    [Theory]
    [InlineData(
        "common-module ",
        new[] { "--help", "-?", "-h", "/?", "/h", "add", "list", "update" })]
    [InlineData(
        "common-module add --",
        new[] { "--document", "--force", "--format", "--help", "--project" })]
    [InlineData(
        "common-module list --",
        new[] { "--document", "--format", "--help", "--project" })]
    [InlineData(
        "common-module update --",
        new[] { "--format", "--help", "--project" })]
    [InlineData(
        "common-module add Feature --format ",
        new[] { "json", "text" })]
    public void FamilyGraphProvidesStaticCompletionWithoutReadingTheProject(
        string commandLine,
        string[] expectedSuggestions)
    {
        using var temp = TempDirectory.Create();
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var manifestStore = new RejectingProjectManifestStore();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            projectManifestStore: manifestStore);

        var result = application.Run(
            [$"[suggest:{commandLine.Length}]", commandLine]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal(expectedSuggestions, suggestions);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
    }

    [Theory]
    [InlineData(
        new[] { "common-module", "add" },
        "Argument '<modules>...' is required for command 'vba-dev common-module add'.")]
    [InlineData(
        new[] { "common-module", "add", "Alpha", "" },
        "Argument '<modules>...' requires a non-empty value.")]
    [InlineData(
        new[] { "common-module", "add", "Alpha", "   " },
        "Argument '<modules>...' requires a non-empty value.")]
    [InlineData(
        new[] { "common-module", "add", "\u00A0" },
        "Argument '<modules>...' requires a non-empty value.")]
    public void InvalidAddGrammarStopsBeforeProjectPackageOrFilesystemAccess(
        string[] arguments,
        string expectedDiagnostic)
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, ProjectManifest.ManifestFileName),
            "{}");
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var manifestStore = new RejectingProjectManifestStore();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            projectManifestStore: manifestStore);

        var result = commandLine.Run(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: {expectedDiagnostic}{Environment.NewLine}" +
            $"Hint: Run 'vba-dev common-module add --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public void ExplicitBlankProjectForCommonModuleListFailsBeforeProjectAccess()
    {
        using var temp = TempDirectory.Create();
        var manifestPath = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        File.WriteAllText(manifestPath, "{}");
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var manifestStore = new RejectingProjectManifestStore();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            projectManifestStore: manifestStore);

        var result = commandLine.Run(["common-module", "list", "--project", ""]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: Option '--project' requires a non-empty value.{Environment.NewLine}" +
            $"Hint: Run 'vba-dev common-module list --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
        Assert.Equal("{}", File.ReadAllText(manifestPath));
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

    private sealed class RejectingProjectManifestStore : IProjectManifestStore
    {
        internal int LoadCount { get; private set; }

        public ProjectManifest Load(string manifestPath)
        {
            LoadCount++;
            throw new InvalidOperationException("Project manifest reads were not expected.");
        }

        public void Save(string projectRoot, ProjectManifest manifest)
            => throw new InvalidOperationException("Project manifest writes were not expected.");
    }
}
