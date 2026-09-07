using System.CommandLine;
using VbaDev.App.Projects;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevTestCommandFamilyTests
{
    [Fact]
    public void TestFamilyIsASealedInternalModule()
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(
            "VbaDev.Cli.VbaDevTestCommandFamily");

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void FamilyRegistersActualLeafRulesAndEveryClosedIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var family = VbaDevTestCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());
        var ruleSnapshot = rules.CreateSnapshot(root.Subcommands.Prepend(root));
        var router = new VbaDevGrammarFailureRouter(root, rules);

        Assert.Same(
            family.TestCommand,
            Assert.Single(root.Subcommands, command => command.Name == "test"));
        var capability = Assert.Single(capabilities);
        Assert.Equal("test", capability.CommandPath);
        Assert.Equal("1.2", capability.OutputSchemaVersion);
        Assert.Same(family.TestCommand, capability.Command);
        Assert.NotNull(family.TestCommand.Action);
        Assert.Equal(
            [
                "--project",
                "--document",
                "--format",
                "--no-build",
                "--source-snapshot",
                "--timeout-seconds",
                "--module",
                "--procedure"
            ],
            family.TestCommand.Options.Select(option => option.Name));
        Assert.Same(family.ProjectOption, family.TestCommand.Options[0]);
        Assert.Same(family.DocumentOption, family.TestCommand.Options[1]);
        Assert.Same(family.FormatOption, family.TestCommand.Options[2]);
        Assert.Same(family.NoBuildOption, family.TestCommand.Options[3]);
        Assert.Same(family.SourceSnapshotOption, family.TestCommand.Options[4]);
        Assert.Same(family.TimeoutSecondsOption, family.TestCommand.Options[5]);
        Assert.Same(family.ModuleOption, family.TestCommand.Options[6]);
        Assert.Same(family.ProcedureOption, family.TestCommand.Options[7]);
        Assert.Equal(["-d"], family.DocumentOption.Aliases);
        Assert.Equal(["-f"], family.FormatOption.Aliases);
        Assert.Equal(
            ["text", "ndjson"],
            Assert.IsType<VbaDevStringOption>(family.FormatOption).AcceptedValues);
        Assert.Equal(
            [
                family.ProjectOption,
                family.DocumentOption,
                family.SourceSnapshotOption
            ],
            ruleSnapshot.NonEmptyOptions.Select(rule => rule.Option));
        Assert.Same(
            family.TimeoutSecondsOption,
            Assert.Single(ruleSnapshot.PositiveOptions).Option);
        Assert.Collection(
            ruleSnapshot.Relationships,
            relationship =>
            {
                Assert.Equal(VbaDevGrammarRelationshipKind.Requires, relationship.Kind);
                Assert.Same(family.TestCommand, relationship.Command);
                Assert.Same(family.ProcedureOption, relationship.First);
                Assert.Same(family.ModuleOption, relationship.Second);
            },
            relationship =>
            {
                Assert.Equal(VbaDevGrammarRelationshipKind.Conflicts, relationship.Kind);
                Assert.Same(family.TestCommand, relationship.Command);
                Assert.Same(family.SourceSnapshotOption, relationship.First);
                Assert.Same(family.NoBuildOption, relationship.Second);
            });

        var persistentParse = ParseSuccessfully(
            root,
            router,
            ["test", "--project", "project", "--document", "Book1"]);
        var persistentIntent = family.IntentBinding.GetRequiredIntent(persistentParse);
        Assert.Equal("project", persistentIntent.ProjectRoot);
        Assert.Equal("Book1", persistentIntent.DocumentName);
        Assert.Null(persistentIntent.ExplicitFormat);
        Assert.Null(persistentIntent.ExplicitTimeoutSeconds);
        Assert.IsType<VbaDevTestSourceIntent.PersistentBuild>(persistentIntent.Source);
        Assert.IsType<VbaDevTestSelectorIntent.AllTests>(persistentIntent.Selector);
        Assert.Same(persistentIntent, family.IntentBinding.GetRequiredIntent(persistentParse));

        var snapshotParse = ParseSuccessfully(
            root,
            router,
            [
                "test",
                "--source-snapshot",
                "snapshot",
                "--module",
                "Test_Module",
                "--format",
                "NDJSON",
                "--timeout-seconds",
                "31"
            ]);
        var snapshotIntent = family.IntentBinding.GetRequiredIntent(snapshotParse);
        var snapshotSource = Assert.IsType<VbaDevTestSourceIntent.SourceSnapshotBuild>(
            snapshotIntent.Source);
        Assert.Equal("snapshot", snapshotSource.SourceSnapshotDirectory);
        var moduleSelector = Assert.IsType<VbaDevTestSelectorIntent.Module>(
            snapshotIntent.Selector);
        Assert.Equal("Test_Module", moduleSelector.ModuleName);
        Assert.Equal("ndjson", snapshotIntent.ExplicitFormat);
        Assert.Equal(31, snapshotIntent.ExplicitTimeoutSeconds);

        var existingWorkbookParse = ParseSuccessfully(
            root,
            router,
            [
                "test",
                "--no-build",
                "--module",
                "Test_Module",
                "--procedure",
                "Test_One"
            ]);
        var existingWorkbookIntent = family.IntentBinding.GetRequiredIntent(existingWorkbookParse);
        Assert.IsType<VbaDevTestSourceIntent.ExistingWorkbook>(existingWorkbookIntent.Source);
        var procedureSelector = Assert.IsType<VbaDevTestSelectorIntent.Procedure>(
            existingWorkbookIntent.Selector);
        Assert.Equal("Test_Module", procedureSelector.ModuleName);
        Assert.Equal("Test_One", procedureSelector.ProcedureName);

        var exactCodePageIdentifierParse = ParseSuccessfully(
            root,
            router,
            ["test", "--no-build", "--module", "\u00A0"]);
        var exactCodePageIdentifierIntent = family.IntentBinding.GetRequiredIntent(
            exactCodePageIdentifierParse);
        Assert.Equal(
            "\u00A0",
            Assert.IsType<VbaDevTestSelectorIntent.Module>(
                exactCodePageIdentifierIntent.Selector).ModuleName);

        Assert.DoesNotContain(
            typeof(VbaDevTestSourceIntent).Assembly.GetTypes()
                .Where(type => type == typeof(VbaDevTestSourceIntent) ||
                    type.IsSubclassOf(typeof(VbaDevTestSourceIntent)))
                .SelectMany(type => type.GetProperties()),
            property => property.PropertyType == typeof(bool));
    }

    [Fact]
    public void FamilyOptionsParticipateInStaticCompletion()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);
        const string commandLine = "test --";

        var result = application.Run(
            [$"[suggest:{commandLine.Length}]", commandLine]);
        var suggestions = result.StandardOutput.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.All(
            new[]
            {
                "--project",
                "--document",
                "--format",
                "--no-build",
                "--source-snapshot",
                "--timeout-seconds",
                "--module",
                "--procedure"
            },
            option => Assert.Contains(option, suggestions));
    }

    [Theory]
    [InlineData(
        new[] { "test", "--procedure", "Test_One" },
        "Option '--procedure' requires option '--module'.")]
    [InlineData(
        new[] { "test", "--source-snapshot", "snapshot", "--no-build" },
        "Options '--source-snapshot' and '--no-build' cannot be used together.")]
    [InlineData(
        new[] { "test", "--timeout-seconds", "0" },
        "Option '--timeout-seconds' requires a positive whole number.")]
    [InlineData(
        new[] { "test", "--timeout-seconds", "-1" },
        "Option '--timeout-seconds' requires a positive whole number.")]
    [InlineData(
        new[] { "test", "--format", "json" },
        "Option '--format' does not accept value 'json'. Accepted values: text, ndjson.")]
    [InlineData(
        new[] { "test", "--project", "" },
        "Option '--project' requires a non-empty value.")]
    [InlineData(
        new[] { "test", "--document", "   " },
        "Option '--document' requires a non-empty value.")]
    [InlineData(
        new[] { "test", "--source-snapshot", "" },
        "Option '--source-snapshot' requires a non-empty value.")]
    [InlineData(
        new[] { "test", "--module", "" },
        "The supplied values do not form a valid intent for command 'vba-dev test'.")]
    [InlineData(
        new[] { "test", "--module", "Test_Module", "--procedure", "" },
        "The supplied values do not form a valid intent for command 'vba-dev test'.")]
    [InlineData(
        new[] { "test", "--module" },
        "Option '--module' requires a value.")]
    public void GrammarFailuresUseCanonicalDiagnosticsBeforeProjectOrWorkbookWork(
        string[] arguments,
        string expectedDiagnostic)
    {
        using var temp = TempDirectory.Create();
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var workbookAutomation = new FakeWorkbookGenerationAutomation();
        var workbookTestRunner = new FakeWorkbookTestRunner();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            workbookGenerationAutomation: workbookAutomation,
            workbookTestRunner: workbookTestRunner,
            projectManifestStore: new RejectingProjectManifestStore());

        var result = commandLine.Run(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(
            $"Error: {expectedDiagnostic}{Environment.NewLine}" +
            $"Hint: Run 'vba-dev test --help' for usage.{Environment.NewLine}",
            result.StandardError);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
        Assert.Empty(workbookAutomation.OpenedWorkbooks);
        Assert.Empty(workbookTestRunner.Workbooks);
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
        public ProjectManifest Load(string manifestPath)
            => throw new InvalidOperationException("Project manifest reads were not expected.");

        public void Save(string projectRoot, ProjectManifest manifest)
            => throw new InvalidOperationException("Project manifest writes were not expected.");
    }
}
