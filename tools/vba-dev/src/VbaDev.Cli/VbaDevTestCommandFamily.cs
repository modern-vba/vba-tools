using System.CommandLine;
using System.CommandLine.Parsing;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Composition;
using AppCommandResult = VbaDev.App.Cli.CommandResult;

namespace VbaDev.Cli;

/// <summary>
/// Owns the test leaf on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevTestCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevTestCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        this.composition = composition;

        TestCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "test",
            "Run VBA unit tests for the selected document.",
            "test",
            "1.2",
            capabilityRegistrations);
        var projectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(TestCommand);
        ProjectOption = projectOptions.Project;
        DocumentOption = projectOptions.Document;
        FormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Test output format.",
            "text|ndjson",
            ["text", "ndjson"],
            "-f");
        NoBuildOption = new Option<bool>("--no-build")
        {
            Description = "Skip building before running tests."
        };
        SourceSnapshotOption = VbaDevCommandGrammar.CreateStringOption(
            "--source-snapshot",
            "Complete caller-owned source snapshot directory.",
            "dir");
        TimeoutSecondsOption = new Option<int?>("--timeout-seconds")
        {
            Description = "Test macro execution timeout in positive whole seconds.",
            HelpName = "seconds"
        };
        ModuleOption = VbaDevCommandGrammar.CreateStringOption(
            "--module",
            "Run tests from one test module.",
            "name");
        ProcedureOption = VbaDevCommandGrammar.CreateStringOption(
            "--procedure",
            "Run one test procedure. Requires --module.",
            "name");
        TestCommand.Add(FormatOption);
        TestCommand.Add(NoBuildOption);
        TestCommand.Add(SourceSnapshotOption);
        TestCommand.Add(TimeoutSecondsOption);
        TestCommand.Add(ModuleOption);
        TestCommand.Add(ProcedureOption);
        grammarFailureRules.RequireNonEmpty(ProjectOption);
        grammarFailureRules.RequireNonEmpty(DocumentOption);
        grammarFailureRules.RequireNonEmpty(SourceSnapshotOption);
        grammarFailureRules.RequirePositive(TimeoutSecondsOption);
        grammarFailureRules.Requires(TestCommand, ProcedureOption, ModuleOption);
        grammarFailureRules.Conflicts(TestCommand, SourceSnapshotOption, NoBuildOption);
        IntentBinding = grammarFailureRules.BindIntent<VbaDevTestCommandIntent>(
            TestCommand,
            BindIntent);
        TestCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        commandFamilyOwnership.Register(this, TestCommand);
    }

    internal Command TestCommand { get; }

    internal Option<string> ProjectOption { get; }

    internal Option<string> DocumentOption { get; }

    internal Option<string> FormatOption { get; }

    internal Option<bool> NoBuildOption { get; }

    internal Option<string> SourceSnapshotOption { get; }

    internal Option<int?> TimeoutSecondsOption { get; }

    internal Option<string> ModuleOption { get; }

    internal Option<string> ProcedureOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevTestCommandIntent> IntentBinding { get; }

    internal static VbaDevTestCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        ArgumentNullException.ThrowIfNull(commandFamilyOwnership);
        return new VbaDevTestCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations,
            commandFamilyOwnership);
    }

    private VbaDevGrammarIntentBindResult<VbaDevTestCommandIntent> BindIntent(
        ParseResult parseResult)
    {
        VbaDevTestSourceIntent source;
        if (parseResult.GetResult(SourceSnapshotOption) is { Implicit: false })
        {
            var sourceSnapshotDirectory = parseResult.GetValue(SourceSnapshotOption);
            if (sourceSnapshotDirectory is null)
            {
                return VbaDevGrammarIntentBindResult<VbaDevTestCommandIntent>.Unbound;
            }

            source = new VbaDevTestSourceIntent.SourceSnapshotBuild(sourceSnapshotDirectory);
        }
        else if (parseResult.GetValue(NoBuildOption))
        {
            source = new VbaDevTestSourceIntent.ExistingWorkbook();
        }
        else
        {
            source = new VbaDevTestSourceIntent.PersistentBuild();
        }

        VbaDevTestSelectorIntent selector;
        if (parseResult.GetResult(ProcedureOption) is { Implicit: false })
        {
            var moduleName = parseResult.GetValue(ModuleOption);
            var procedureName = parseResult.GetValue(ProcedureOption);
            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(procedureName))
            {
                return VbaDevGrammarIntentBindResult<VbaDevTestCommandIntent>.Unbound;
            }

            selector = new VbaDevTestSelectorIntent.Procedure(moduleName, procedureName);
        }
        else if (parseResult.GetResult(ModuleOption) is { Implicit: false })
        {
            var moduleName = parseResult.GetValue(ModuleOption);
            if (string.IsNullOrEmpty(moduleName))
            {
                return VbaDevGrammarIntentBindResult<VbaDevTestCommandIntent>.Unbound;
            }

            selector = new VbaDevTestSelectorIntent.Module(moduleName);
        }
        else
        {
            selector = new VbaDevTestSelectorIntent.AllTests();
        }

        return VbaDevGrammarIntentBindResult<VbaDevTestCommandIntent>.Bound(
            new VbaDevTestCommandIntent(
                parseResult.GetValue(ProjectOption),
                parseResult.GetValue(DocumentOption),
                parseResult.GetValue(FormatOption),
                parseResult.GetValue(TimeoutSecondsOption),
                source,
                selector));
    }

    private Task<AppCommandResult> RunAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = IntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveDocumentContextAsync(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            async (context, operationCancellationToken) =>
            {
                try
                {
                    var format = CommandDefaultResolver.ResolveTestFormat(
                        context.Manifest,
                        intent.ExplicitFormat);
                    var executionTimeout = CommandDefaultResolver.ResolveTestExecutionTimeout(
                        context.Manifest,
                        intent.ExplicitTimeoutSeconds);
                    var selector = intent.Selector switch
                    {
                        VbaDevTestSelectorIntent.AllTests => new WorkbookTestSelector(),
                        VbaDevTestSelectorIntent.Module module =>
                            new WorkbookTestSelector(module.ModuleName),
                        VbaDevTestSelectorIntent.Procedure procedure =>
                            new WorkbookTestSelector(
                                procedure.ModuleName,
                                procedure.ProcedureName),
                        _ => throw new InvalidOperationException(
                            $"Unsupported test selector intent '{intent.Selector.GetType().FullName}'.")
                    };
                    var request = intent.Source switch
                    {
                        VbaDevTestSourceIntent.PersistentBuild =>
                            new TestCommandRequest(
                                format,
                                BuildFirst: true,
                                selector,
                                executionTimeout),
                        VbaDevTestSourceIntent.SourceSnapshotBuild snapshotBuild =>
                            new TestCommandRequest(
                                format,
                                BuildFirst: true,
                                selector,
                                executionTimeout,
                                Path.GetFullPath(
                                    snapshotBuild.SourceSnapshotDirectory,
                                    composition.WorkingDirectory)),
                        VbaDevTestSourceIntent.ExistingWorkbook =>
                            new TestCommandRequest(
                                format,
                                BuildFirst: false,
                                selector,
                                executionTimeout),
                        _ => throw new InvalidOperationException(
                            $"Unsupported test source intent '{intent.Source.GetType().FullName}'.")
                    };
                    return await composition.TestCommand.RunAsync(
                            context,
                            request,
                            operationCancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    return AppCommandResult.UsageError(ex.Message);
                }
            },
            cancellationToken);
    }
}

internal sealed record VbaDevTestCommandIntent(
    string? ProjectRoot,
    string? DocumentName,
    string? ExplicitFormat,
    int? ExplicitTimeoutSeconds,
    VbaDevTestSourceIntent Source,
    VbaDevTestSelectorIntent Selector);

internal abstract record VbaDevTestSourceIntent
{
    private VbaDevTestSourceIntent()
    {
    }

    internal sealed record PersistentBuild : VbaDevTestSourceIntent;

    internal sealed record SourceSnapshotBuild(
        string SourceSnapshotDirectory) : VbaDevTestSourceIntent;

    internal sealed record ExistingWorkbook : VbaDevTestSourceIntent;
}

internal abstract record VbaDevTestSelectorIntent
{
    private VbaDevTestSelectorIntent()
    {
    }

    internal sealed record AllTests : VbaDevTestSelectorIntent;

    internal sealed record Module(string ModuleName) : VbaDevTestSelectorIntent;

    internal sealed record Procedure(
        string ModuleName,
        string ProcedureName) : VbaDevTestSelectorIntent;
}
