using System.CommandLine;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevReferenceCommandFamilyTests
{
    [Fact]
    public void ReferenceFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevReferenceCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void FamilyRegistersTheReferenceGraphWithoutEvaluatingDynamicCompletion()
    {
        using var temp = TempDirectory.Create();
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Available Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0));
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();

        var family = VbaDevReferenceCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                vbaProjectReferenceResolver: resolver),
            rules,
            capabilities);

        Assert.Same(
            family.ReferenceCommand,
            Assert.Single(root.Subcommands, command => command.Name == "reference"));
        Assert.Equal(
            ["add", "list", "remove"],
            family.ReferenceCommand.Subcommands.Select(command => command.Name));
        Assert.Equal(
            ["reference add", "reference list", "reference remove"],
            capabilities.Select(capability => capability.CommandPath));
        Assert.All(capabilities, capability => Assert.Equal("1.0", capability.OutputSchemaVersion));
        Assert.Same(family.AddCommand, capabilities[0].Command);
        Assert.Same(family.ListCommand, capabilities[1].Command);
        Assert.Same(family.RemoveCommand, capabilities[2].Command);
        Assert.NotNull(family.AddCommand.Action);
        Assert.NotNull(family.ListCommand.Action);
        Assert.NotNull(family.RemoveCommand.Action);
        Assert.Equal(1, family.AddReferencesArgument.Arity.MinimumNumberOfValues);
        Assert.Equal(1, family.RemoveReferencesArgument.Arity.MinimumNumberOfValues);
        Assert.Equal(["-d"], family.AddDocumentOption.Aliases);
        Assert.Equal(["-f"], family.AddFormatOption.Aliases);
        Assert.Equal(["-d"], family.ListDocumentOption.Aliases);
        Assert.Equal(["-f"], family.ListFormatOption.Aliases);
        Assert.Equal(["-d"], family.RemoveDocumentOption.Aliases);
        Assert.Equal(["-f"], family.RemoveFormatOption.Aliases);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.AddFormatOption).AcceptedValues);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.ListFormatOption).AcceptedValues);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.RemoveFormatOption).AcceptedValues);
        Assert.Empty(resolver.RequestedNames);
    }

    [Fact]
    public void ListBindsExactlyOneClosedModeAndOwnsTheActualConflict()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevReferenceCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>());
        var ruleSnapshot = rules.CreateSnapshot(
            [
                root,
                family.ReferenceCommand,
                family.AddCommand,
                family.ListCommand,
                family.RemoveCommand
            ]);
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var conflict = Assert.Single(ruleSnapshot.Relationships);
        Assert.Equal(VbaDevGrammarRelationshipKind.Conflicts, conflict.Kind);
        Assert.Same(family.ListCommand, conflict.Command);
        Assert.Same(family.ListAvailableOption, conflict.First);
        Assert.Same(family.ListNoResolveOption, conflict.Second);

        var selectedParse = ParseSuccessfully(root, router, ["reference", "list"]);
        var selected = Assert.IsType<
            VbaDevReferenceListCommandIntent.SelectedAndResolved>(
            family.ListIntentBinding.GetRequiredIntent(selectedParse));
        Assert.Null(selected.ProjectRoot);
        Assert.Null(selected.DocumentName);
        Assert.Equal("text", selected.Format);
        Assert.Same(selected, family.ListIntentBinding.GetRequiredIntent(selectedParse));

        var selectionParse = ParseSuccessfully(
            root,
            router,
            [
                "reference",
                "list",
                "--no-resolve",
                "--project",
                "project",
                "--document",
                "Book1",
                "--format",
                "JSON"
            ]);
        var selection = Assert.IsType<
            VbaDevReferenceListCommandIntent.SelectedWithoutResolution>(
            family.ListIntentBinding.GetRequiredIntent(selectionParse));
        Assert.Equal("project", selection.ProjectRoot);
        Assert.Equal("Book1", selection.DocumentName);
        Assert.Equal("json", selection.Format);
        Assert.Same(selection, family.ListIntentBinding.GetRequiredIntent(selectionParse));

        var availableParse = ParseSuccessfully(
            root,
            router,
            ["reference", "list", "--available"]);
        var available = Assert.IsType<
            VbaDevReferenceListCommandIntent.AvailableCatalog>(
            family.ListIntentBinding.GetRequiredIntent(availableParse));
        Assert.Null(available.ProjectRoot);
        Assert.Null(available.DocumentName);
        Assert.Equal("text", available.Format);
        Assert.Same(available, family.ListIntentBinding.GetRequiredIntent(availableParse));
    }

    [Fact]
    public void MutationLeavesBindOrderedNonemptyNameListsWithoutNormalizingThem()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevReferenceCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>());
        var ruleSnapshot = rules.CreateSnapshot(
            [
                root,
                family.ReferenceCommand,
                family.AddCommand,
                family.ListCommand,
                family.RemoveCommand
            ]);
        var router = new VbaDevGrammarFailureRouter(root, rules);

        Assert.Equal(
            [family.AddReferencesArgument, family.RemoveReferencesArgument],
            ruleSnapshot.NonEmptyArguments.Select(rule => rule.Argument));

        var addParse = ParseSuccessfully(
            root,
            router,
            [
                "reference",
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
        var add = family.AddIntentBinding.GetRequiredIntent(addParse);
        Assert.Equal("project", add.ProjectRoot);
        Assert.Equal("Book1", add.DocumentName);
        Assert.Equal(["  Alpha  ", "Beta"], add.ReferenceNames);
        Assert.Equal("json", add.Format);

        var removeParse = ParseSuccessfully(
            root,
            router,
            ["reference", "remove", "Alpha", "beta"]);
        var remove = family.RemoveIntentBinding.GetRequiredIntent(removeParse);
        Assert.Null(remove.ProjectRoot);
        Assert.Null(remove.DocumentName);
        Assert.Equal(["Alpha", "beta"], remove.ReferenceNames);
        Assert.Equal("text", remove.Format);
    }

    [Theory]
    [InlineData(
        new[] { "reference", "list", "--available", "--no-resolve" },
        "Options '--available' and '--no-resolve' cannot be used together.")]
    [InlineData(
        new[] { "reference", "add", "Alpha", "" },
        "Argument '<references>...' requires a non-empty value.")]
    [InlineData(
        new[] { "reference", "remove", "Alpha", "   " },
        "Argument '<references>...' requires a non-empty value.")]
    public void InvalidReferenceGrammarStopsBeforeProjectRegistryOrWorkbookWork(
        string[] arguments,
        string expectedDiagnostic)
    {
        using var temp = TempDirectory.Create();
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Available Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0))
        {
            ThrowOnResolve = true
        };
        var workbookAutomation = new FakeWorkbookGenerationAutomation();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            workbookGenerationAutomation: workbookAutomation,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: new RejectingProjectManifestStore());

        var result = commandLine.Run(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: {expectedDiagnostic}{Environment.NewLine}" +
            $"Hint: Run 'vba-dev {string.Join(' ', arguments.Take(2))} --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
        Assert.Empty(resolver.RequestedNames);
        Assert.Empty(workbookAutomation.OpenedWorkbooks);
    }

    [Theory]
    [InlineData("reference add --", 16, "--project")]
    [InlineData("reference add -- Alpha", 16, "--project")]
    [InlineData("reference add --f", 17, "--format")]
    [InlineData("reference add --h", 17, "--help")]
    [InlineData("reference add --f -- Alpha", 17, "--format")]
    [InlineData("reference remove --", 19, "--project")]
    [InlineData("reference remove --f", 20, "--format")]
    [InlineData("reference remove --h", 20, "--help")]
    public void StaticReferenceOptionCompletionDoesNotEvaluateDynamicNameSources(
        string commandText,
        int cursorPosition,
        string expectedOption)
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, ProjectManifest.ManifestFileName),
            "{}");
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Available Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0));
        var manifestStore = new RejectingProjectManifestStore();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);

        var result = commandLine.Run(
            [$"[suggest:{cursorPosition}]", commandText]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedOption, suggestions);
        Assert.DoesNotContain("Available Library", suggestions);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Empty(resolver.RequestedNames);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("reference add -", "Alpha-Beta Library")]
    [InlineData("reference add --Leg", "--Legacy Library")]
    [InlineData("reference add --v", "--verbose Library")]
    [InlineData("reference add -- -Leg", "-Legacy Library")]
    [InlineData("reference add -- --Leg", "--Legacy Library")]
    public void AddNameCompletionRetainsHyphenCandidatesInArgumentContext(
        string commandText,
        string expectedCandidate)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        new JsonProjectManifestStore().Save(
            projectRoot,
            ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                expectedCandidate,
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0));
        var commandLine = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = commandLine.Run(
            [$"[suggest:{commandText.Length}]", commandText]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedCandidate, suggestions);
        Assert.Equal([expectedCandidate], resolver.RequestedNames);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("reference remove -", "Alpha-Beta Library")]
    [InlineData("reference remove --Leg", "--Legacy Library")]
    [InlineData("reference remove --v", "--verbose Library")]
    [InlineData("reference remove -- -Leg", "-Legacy Library")]
    [InlineData("reference remove -- --Leg", "--Legacy Library")]
    public void RemoveNameCompletionRetainsHyphenCandidatesInArgumentContext(
        string commandText,
        string expectedCandidate)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var manifest = ProjectManifest.CreateDefault(
            "Project",
            "Book1",
            projectRoot,
            null);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference(expectedCandidate));
        new JsonProjectManifestStore().Save(projectRoot, manifest);
        var resolver = new FakeVbaProjectReferenceResolver
        {
            ThrowOnResolve = true
        };
        var commandLine = CommandLineTestFactory.Create(
            projectRoot,
            vbaProjectReferenceResolver: resolver);

        var result = commandLine.Run(
            [$"[suggest:{commandText.Length}]", commandText]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedCandidate, suggestions);
        Assert.Empty(resolver.RequestedNames);
        Assert.Empty(result.StandardError);
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
