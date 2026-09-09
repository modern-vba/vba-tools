using System.CommandLine;
using System.CommandLine.Parsing;
using VbaDev.App.Build;
using VbaDev.Composition;
using AppCommandResult = VbaDev.App.Cli.CommandResult;

namespace VbaDev.Cli;

/// <summary>
/// Owns the build and publish leaves on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevBuildPublishCommandFamily
{
    private readonly ToolingApplicationComposition composition;
    private readonly VbaDevGrammarFailureRules grammarFailureRules;
    private readonly ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations;
    private readonly VbaDevCommandFamilyOwnership commandFamilyOwnership;

    private VbaDevBuildPublishCommandFamily(
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        this.composition = composition;
        this.grammarFailureRules = grammarFailureRules;
        this.capabilityRegistrations = capabilityRegistrations;
        this.commandFamilyOwnership = commandFamilyOwnership;
    }

    internal Command BuildCommand { get; private set; } = null!;

    internal Option<string> BuildProjectOption { get; private set; } = null!;

    internal Option<string> BuildDocumentOption { get; private set; } = null!;

    internal Option<string> BuildSourceSnapshotOption { get; private set; } = null!;

    internal Option<string> BuildOutputOption { get; private set; } = null!;

    internal VbaDevGrammarIntentBinding<VbaDevBuildCommandIntent> BuildIntentBinding { get; private set; }
        = null!;

    internal Command PublishCommand { get; private set; } = null!;

    internal Option<string> PublishProjectOption { get; private set; } = null!;

    internal Option<string> PublishDocumentOption { get; private set; } = null!;

    internal VbaDevGrammarIntentBinding<VbaDevPublishCommandIntent> PublishIntentBinding { get; private set; }
        = null!;

    internal static VbaDevBuildPublishCommandFamily Create(
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        ArgumentNullException.ThrowIfNull(commandFamilyOwnership);
        return new VbaDevBuildPublishCommandFamily(
            composition,
            grammarFailureRules,
            capabilityRegistrations,
            commandFamilyOwnership);
    }

    internal void RegisterBuild(RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        BuildCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "build",
            "Build the selected document into bin output.",
            "build",
            "2.0",
            capabilityRegistrations);
        var projectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(BuildCommand, grammarFailureRules);
        BuildProjectOption = projectOptions.Project;
        BuildDocumentOption = projectOptions.Document;
        BuildSourceSnapshotOption = VbaDevCommandGrammar.CreateStringOption(
            "--source-snapshot",
            "Complete caller-owned source snapshot directory.",
            "dir");
        BuildOutputOption = VbaDevCommandGrammar.CreateStringOption(
            "--output",
            "Caller-owned workbook output path for snapshot builds.",
            "workbook",
            aliases: "-o");
        BuildCommand.Add(BuildSourceSnapshotOption);
        BuildCommand.Add(BuildOutputOption);
        grammarFailureRules.RequireNonEmpty(BuildSourceSnapshotOption);
        grammarFailureRules.RequireNonEmpty(BuildOutputOption);
        grammarFailureRules.AllOrNone(
            BuildCommand,
            BuildSourceSnapshotOption,
            BuildOutputOption);
        BuildIntentBinding = grammarFailureRules.BindIntent<VbaDevBuildCommandIntent>(
            BuildCommand,
            BindBuildIntent);
        BuildCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunBuildAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        commandFamilyOwnership.Register(this, BuildCommand);
    }

    internal void RegisterPublish(RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        PublishCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "publish",
            "Publish the selected document.",
            "publish",
            "1.0",
            capabilityRegistrations);
        var projectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(PublishCommand, grammarFailureRules);
        PublishProjectOption = projectOptions.Project;
        PublishDocumentOption = projectOptions.Document;
        PublishIntentBinding = grammarFailureRules.BindIntent<VbaDevPublishCommandIntent>(
            PublishCommand,
            BindPublishIntent);
        PublishCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunPublishAsync(parseResult, cancellationToken).ConfigureAwait(false)));
        commandFamilyOwnership.Register(this, PublishCommand);
    }

    private VbaDevGrammarIntentBindResult<VbaDevBuildCommandIntent> BindBuildIntent(
        ParseResult parseResult)
    {
        if (parseResult.GetResult(BuildSourceSnapshotOption) is { Implicit: false })
        {
            var sourceSnapshotDirectory = parseResult.GetValue(BuildSourceSnapshotOption);
            var outputWorkbook = parseResult.GetValue(BuildOutputOption);
            return sourceSnapshotDirectory is null || outputWorkbook is null
                ? VbaDevGrammarIntentBindResult<VbaDevBuildCommandIntent>.Unbound
                : VbaDevGrammarIntentBindResult<VbaDevBuildCommandIntent>.Bound(
                    new VbaDevBuildCommandIntent.SourceSnapshotBuild(
                        parseResult.GetValue(BuildProjectOption),
                        parseResult.GetValue(BuildDocumentOption),
                        sourceSnapshotDirectory,
                        outputWorkbook));
        }

        return VbaDevGrammarIntentBindResult<VbaDevBuildCommandIntent>.Bound(
            new VbaDevBuildCommandIntent.PersistentBuild(
                parseResult.GetValue(BuildProjectOption),
                parseResult.GetValue(BuildDocumentOption)));
    }

    private VbaDevGrammarIntentBindResult<VbaDevPublishCommandIntent> BindPublishIntent(
        ParseResult parseResult)
        => VbaDevGrammarIntentBindResult<VbaDevPublishCommandIntent>.Bound(
            new VbaDevPublishCommandIntent(
                parseResult.GetValue(PublishProjectOption),
                parseResult.GetValue(PublishDocumentOption)));

    private Task<AppCommandResult> RunBuildAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = BuildIntentBinding.GetRequiredIntent(parseResult);
        return intent switch
        {
            VbaDevBuildCommandIntent.PersistentBuild persistentBuild =>
                VbaDevCommandGrammar.ResolveDocumentContextAsync(
                    composition,
                    persistentBuild.ProjectRoot,
                    persistentBuild.DocumentName,
                    composition.BuildCommand.RunAsync,
                    cancellationToken),
            VbaDevBuildCommandIntent.SourceSnapshotBuild sourceSnapshotBuild =>
                VbaDevCommandGrammar.ResolveDocumentContextAsync(
                    composition,
                    sourceSnapshotBuild.ProjectRoot,
                    sourceSnapshotBuild.DocumentName,
                    (context, operationCancellationToken) =>
                        composition.BuildCommand.RunSnapshotAsync(
                            context,
                            new SourceSnapshotBuildCommandRequest(
                                sourceSnapshotBuild.SourceSnapshotDirectory,
                                sourceSnapshotBuild.OutputWorkbook,
                                composition.WorkingDirectory),
                            operationCancellationToken),
                    cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported build intent '{intent.GetType().FullName}'.")
        };
    }

    private Task<AppCommandResult> RunPublishAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = PublishIntentBinding.GetRequiredIntent(parseResult);
        return VbaDevCommandGrammar.ResolveDocumentContextAsync(
            composition,
            intent.ProjectRoot,
            intent.DocumentName,
            composition.PublishCommand.RunAsync,
            cancellationToken);
    }
}

internal abstract record VbaDevBuildCommandIntent
{
    private VbaDevBuildCommandIntent()
    {
    }

    internal sealed record PersistentBuild(
        string? ProjectRoot,
        string? DocumentName) : VbaDevBuildCommandIntent;

    internal sealed record SourceSnapshotBuild(
        string? ProjectRoot,
        string? DocumentName,
        string SourceSnapshotDirectory,
        string OutputWorkbook) : VbaDevBuildCommandIntent;
}

internal sealed record VbaDevPublishCommandIntent(
    string? ProjectRoot,
    string? DocumentName);
