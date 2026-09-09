using System.CommandLine;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevProjectSelectorTests
{
    private const string ProjectSpelling = @"  .\MiXeD Path\..\Project_Σ  ";
    private const string DocumentSpelling = "  BoOk_Σ  ";

    private static readonly string[] ProjectLeaves =
    [
        "build", "test", "publish", "export",
        "common-module add", "common-module list", "common-module update",
        "reference add", "reference list", "reference remove", "check", "doctor"
    ];

    private static readonly string[] ProjectOnlyLeaves =
        ["common-module update", "check", "doctor"];

    private static bool HasDocument(string commandPath)
        => !ProjectOnlyLeaves.Contains(commandPath, StringComparer.Ordinal);

    public static IEnumerable<object[]> BlankSelectors()
    {
        foreach (var path in ProjectLeaves)
        {
            foreach (var value in new[] { "", " \t ", "\u00a0\u3000" })
            {
                yield return [path, "--project", value];
                if (HasDocument(path))
                {
                    yield return [path, "--document", value];
                    yield return [path, "-d", value];
                }
            }
        }
    }

    public static IEnumerable<object?[]> OmittedAndSuppliedSelectors()
    {
        foreach (var path in ProjectLeaves)
        {
            foreach (var project in new string?[] { null, ProjectSpelling })
            {
                yield return [path, project, null, false];
                if (HasDocument(path))
                {
                    yield return [path, project, DocumentSpelling, false];
                    yield return [path, project, DocumentSpelling, true];
                }
            }
        }
    }

    public static IEnumerable<object[]> BlankCompletionSelectors()
        => BlankSelectors().Where(values => values[0] is "reference add" or "reference remove");

    [Theory]
    [MemberData(nameof(BlankCompletionSelectors))]
    public void ReferenceNameCompletionRejectsBlankSelectorsQuietlyBeforeProjectAccess(
        string commandPath,
        string option,
        string value)
    {
        using var temp = TempDirectory.Create();
        var manifestPath = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        File.WriteAllText(manifestPath, "{}");
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var store = new RejectingProjectManifestStore();
        var resolver = new FakeVbaProjectReferenceResolver { ThrowOnResolve = true };
        var commandLine = CommandLineTestFactory.Create(
            temp.Path, projectManifestStore: store, vbaProjectReferenceResolver: resolver);
        var line = $"{commandPath} {option} \"{value}\" Z";

        var result = commandLine.Run([$"[suggest:{line.Length}]", line]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.StandardError);
        Assert.Equal(0, store.LoadCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(resolver.RequestedNames);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
        Assert.Equal("{}", File.ReadAllText(manifestPath));
    }

    public static IEnumerable<object[]> BothBlankSelectors()
    {
        foreach (var path in ProjectLeaves.Where(HasDocument))
        {
            yield return [path, true];
            yield return [path, false];
        }
    }

    [Fact]
    public void CompletedGraphExposesExactlyTheProjectAndDocumentSelectorLeaves()
    {
        using var temp = TempDirectory.Create();
        var root = CommandLineTestFactory.Create(temp.Path).CommandGraph.RootCommand;
        var leaves = EnumerateLeaves(root).ToArray();

        Assert.Equal(
            ProjectLeaves.Order(StringComparer.Ordinal),
            leaves.Where(leaf => leaf.Command.Options.Any(option => option.Name == "--project"))
                .Select(leaf => leaf.Path).Order(StringComparer.Ordinal));
        Assert.Equal(
            ProjectLeaves.Where(HasDocument).Order(StringComparer.Ordinal),
            leaves.Where(leaf => leaf.Command.Options.Any(option => option.Name == "--document"))
                .Select(leaf => leaf.Path).Order(StringComparer.Ordinal));
        foreach (var leaf in leaves.Where(leaf => ProjectLeaves.Contains(leaf.Path, StringComparer.Ordinal)))
        {
            Assert.Single(leaf.Command.Options, option => option.Name == "--project");
            if (HasDocument(leaf.Path))
            {
                var document = Assert.Single(leaf.Command.Options, option => option.Name == "--document");
                Assert.Equal(["-d"], document.Aliases);
            }
        }
    }

    [Theory]
    [MemberData(nameof(BlankSelectors))]
    public async Task BlankSelectorsFailBeforeIntentBindingOrDomainWork(
        string commandPath,
        string option,
        string value)
    {
        var canonicalOption = option == "-d" ? "--document" : option;
        await AssertGrammarFailureAsync(
            commandPath,
            [.. BaseArguments(commandPath), option, value],
            $"Option '{canonicalOption}' requires a non-empty value.");
    }

    [Theory]
    [MemberData(nameof(OmittedAndSuppliedSelectors))]
    public void BoundSelectorsPreserveNullOmissionAndTheExactSuppliedSpelling(
        string commandPath,
        string? project,
        string? document,
        bool useDocumentAlias)
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFamilyGraph(temp.Path);
        var arguments = BaseArguments(commandPath).ToList();
        if (project is not null)
        {
            arguments.AddRange(["--project", project]);
        }
        if (document is not null)
        {
            arguments.AddRange([useDocumentAlias ? "-d" : "--document", document]);
        }
        var parse = fixture.Root.Parse(arguments);
        using var error = new StringWriter();

        Assert.False(fixture.Router.TryWriteFailure(parse, error));
        Assert.Empty(error.ToString());
        var selectors = fixture.Leaves[commandPath].ReadSelectors(parse);

        Assert.Equal(project, selectors.ProjectRoot);
        Assert.Equal(document, selectors.DocumentName);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Theory]
    [MemberData(nameof(BothBlankSelectors))]
    public async Task SelectorValueErrorsFollowTokenOrderAndUseCanonicalOptionNames(
        string commandPath,
        bool projectFirst)
    {
        string[] selectorArguments = projectFirst
            ? ["--project", " \t ", "-d", "\u3000"]
            : ["-d", "\u3000", "--project", " \t "];
        var expectedOption = projectFirst ? "--project" : "--document";
        await AssertGrammarFailureAsync(
            commandPath,
            [.. BaseArguments(commandPath), .. selectorArguments],
            $"Option '{expectedOption}' requires a non-empty value.");
    }

    [Theory]
    [InlineData("doctor", new[] { "doctor", "--scope", "environment", "--project", "" },
        "Option '--project' requires a non-empty value.")]
    [InlineData("export", new[] { "export", "--from", "book.xlsm", "-d", "\u3000" },
        "Option '--document' requires a non-empty value.")]
    [InlineData("build", new[] { "build", "--source-snapshot", "snapshot", "--project", "" },
        "Option '--project' requires a non-empty value.")]
    [InlineData("test", new[] { "test", "--procedure", "TestOne", "--document", "" },
        "Option '--document' requires a non-empty value.")]
    [InlineData("reference list",
        new[] { "reference", "list", "--available", "--no-resolve", "--project", "" },
        "Option '--project' requires a non-empty value.")]
    [InlineData("doctor", new[] { "doctor", "--scope", "machine", "--project", "" },
        "Option '--scope' does not accept value 'machine'. Accepted values: project, environment.")]
    [InlineData("doctor", new[] { "doctor", "--project", "", "--scope", "machine" },
        "Option '--project' requires a non-empty value.")]
    public async Task SelectorValueValidationPrecedesRelationshipsAndRetainsValueTokenOrder(
        string commandPath,
        string[] arguments,
        string expectedDiagnostic)
        => await AssertGrammarFailureAsync(commandPath, arguments, expectedDiagnostic);

    private static async Task AssertGrammarFailureAsync(
        string commandPath,
        string[] arguments,
        string expectedDiagnostic)
    {
        using var temp = TempDirectory.Create();
        var manifestPath = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        File.WriteAllText(manifestPath, "{}");
        var initialEntries = Directory.GetFileSystemEntries(temp.Path);
        var manifestStore = new RejectingProjectManifestStore();
        var environment = new RejectingEnvironmentDiagnosticPort();
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Fixture Library", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0))
        {
            ThrowOnResolve = true
        };
        var automation = new FakeWorkbookGenerationAutomation();
        var commandLine = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environment,
            workbookGenerationAutomation: automation,
            vbaProjectReferenceResolver: resolver,
            projectManifestStore: manifestStore);
        var expectedError = $"Error: {expectedDiagnostic}{Environment.NewLine}"
            + $"Hint: Run 'vba-dev {commandPath} --help' for usage.{Environment.NewLine}";

        var result = await commandLine.RunAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(expectedError, result.StandardError);
        Assert.Equal(0, manifestStore.LoadCount);
        Assert.Equal(0, manifestStore.SaveCount);
        Assert.Equal(0, environment.InvocationCount);
        Assert.Empty(resolver.RequestedNames);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(initialEntries, Directory.GetFileSystemEntries(temp.Path));
        Assert.Equal("{}", File.ReadAllText(manifestPath));

        // Use the real family bindings to observe that the value phase never invoked them.
        var fixture = CreateFamilyGraph(temp.Path);
        var parse = fixture.Root.Parse(arguments);
        using var error = new StringWriter();
        Assert.True(fixture.Router.TryWriteFailure(parse, error));
        Assert.Equal(expectedError, error.ToString());
        var leaf = fixture.Leaves[commandPath];
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = leaf.ReadSelectors(parse);
        });
        Assert.Equal(
            $"Intent for command '{leaf.Command.Name}' has not been bound by the grammar router.",
            exception.Message);
    }

    private static string[] BaseArguments(string commandPath)
        => commandPath switch
        {
            "common-module add" => ["common-module", "add", "ModuleOne"],
            "reference add" => ["reference", "add", "LibraryOne"],
            "reference remove" => ["reference", "remove", "LibraryOne"],
            _ => commandPath.Split(' ')
        };

    private static IEnumerable<(string Path, Command Command)> EnumerateLeaves(
        Command parent,
        string parentPath = "")
    {
        foreach (var command in parent.Subcommands)
        {
            var path = parentPath.Length == 0 ? command.Name : $"{parentPath} {command.Name}";
            if (command.Subcommands.Count == 0)
            {
                yield return (path, command);
            }
            else
            {
                foreach (var leaf in EnumerateLeaves(command, path))
                {
                    yield return leaf;
                }
            }
        }
    }

    private static FamilyGraph CreateFamilyGraph(string workingDirectory)
    {
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var ownership = new VbaDevCommandFamilyOwnership();
        var composition = ToolingCompositionRoot.CreateApplicationComposition(workingDirectory);
        var buildPublish = VbaDevBuildPublishCommandFamily.Create(
            composition, rules, capabilities, ownership);
        buildPublish.RegisterBuild(root);
        buildPublish.RegisterPublish(root);
        var test = VbaDevTestCommandFamily.Register(root, composition, rules, capabilities, ownership);
        var importExport = VbaDevImportExportCommandFamily.Register(
            root, composition, rules, capabilities, ownership);
        var common = VbaDevCommonModuleCommandFamily.Register(root, composition, rules, capabilities, ownership);
        var reference = VbaDevReferenceCommandFamily.Register(root, composition, rules, capabilities, ownership);
        var inspection = VbaDevInspectionCommandFamily.Register(root, composition, rules, capabilities, ownership);
        var leaves = new Dictionary<string, SelectorLeaf>(StringComparer.Ordinal)
        {
            ["build"] = Leaf(buildPublish.BuildCommand, buildPublish.BuildIntentBinding, intent =>
            {
                var bound = Assert.IsType<VbaDevBuildCommandIntent.PersistentBuild>(intent);
                return new SelectorValues(bound.ProjectRoot, bound.DocumentName);
            }),
            ["test"] = Leaf(test.TestCommand, test.IntentBinding, intent =>
            {
                Assert.IsType<VbaDevTestSourceIntent.PersistentBuild>(intent.Source);
                return new SelectorValues(intent.ProjectRoot, intent.DocumentName);
            }),
            ["publish"] = Leaf(buildPublish.PublishCommand, buildPublish.PublishIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, intent.DocumentName)),
            ["export"] = Leaf(importExport.ExportCommand, importExport.ExportIntentBinding, intent =>
            {
                var bound = Assert.IsType<VbaDevExportCommandIntent.ProjectDocument>(intent);
                return new SelectorValues(bound.ProjectRoot, bound.DocumentName);
            }),
            ["common-module add"] = Leaf(common.AddCommand, common.AddIntentBinding, intent =>
            {
                var bound = Assert.IsType<VbaDevCommonModuleAddCommandIntent.Ordinary>(intent);
                return new SelectorValues(bound.ProjectRoot, bound.DocumentName);
            }),
            ["common-module list"] = Leaf(common.ListCommand, common.ListIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, intent.DocumentName)),
            ["common-module update"] = Leaf(common.UpdateCommand, common.UpdateIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, null)),
            ["reference add"] = Leaf(reference.AddCommand, reference.AddIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, intent.DocumentName)),
            ["reference list"] = Leaf(reference.ListCommand, reference.ListIntentBinding, intent =>
            {
                var bound = Assert.IsType<VbaDevReferenceListCommandIntent.SelectedAndResolved>(intent);
                return new SelectorValues(bound.ProjectRoot, bound.DocumentName);
            }),
            ["reference remove"] = Leaf(reference.RemoveCommand, reference.RemoveIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, intent.DocumentName)),
            ["check"] = Leaf(inspection.CheckCommand, inspection.CheckIntentBinding,
                intent => new SelectorValues(intent.ProjectRoot, null)),
            ["doctor"] = Leaf(inspection.DoctorCommand, inspection.DoctorIntentBinding, intent =>
            {
                var bound = Assert.IsType<VbaDevDoctorCommandIntent.Project>(intent);
                return new SelectorValues(bound.ProjectRoot, null);
            })
        };
        return new FamilyGraph(root, new VbaDevGrammarFailureRouter(root, rules), leaves);
    }

    private static SelectorLeaf Leaf<TIntent>(
        Command command,
        VbaDevGrammarIntentBinding<TIntent> binding,
        Func<TIntent, SelectorValues> project)
        where TIntent : class
        => new(command, parse => project(binding.GetRequiredIntent(parse)));

    private sealed record SelectorValues(string? ProjectRoot, string? DocumentName);

    private sealed record SelectorLeaf(
        Command Command,
        Func<ParseResult, SelectorValues> ReadSelectors);

    private sealed record FamilyGraph(
        RootCommand Root,
        VbaDevGrammarFailureRouter Router,
        IReadOnlyDictionary<string, SelectorLeaf> Leaves);

    private sealed class RejectingProjectManifestStore : IProjectManifestStore
    {
        internal int LoadCount { get; private set; }
        internal int SaveCount { get; private set; }

        public ProjectManifest Load(string manifestPath)
        {
            LoadCount++;
            throw new InvalidOperationException("Project manifest reads were not expected.");
        }

        public void Save(string projectRoot, ProjectManifest manifest)
        {
            SaveCount++;
            throw new InvalidOperationException("Project manifest writes were not expected.");
        }
    }

    private sealed class RejectingEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
    {
        internal int InvocationCount { get; private set; }

        public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("Environment diagnostics were not expected.");
        }
    }
}
