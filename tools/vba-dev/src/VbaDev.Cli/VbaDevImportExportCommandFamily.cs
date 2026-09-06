using System.CommandLine;
using System.CommandLine.Parsing;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.Composition;
using AppCommandResult = VbaDev.App.Cli.CommandResult;

namespace VbaDev.Cli;

/// <summary>
/// Owns the import and export leaves on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevImportExportCommandFamily
{
    private readonly ToolingApplicationComposition composition;

    private VbaDevImportExportCommandFamily(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        this.composition = composition;

        ExportCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "export",
            "Export modules from a workbook into source.",
            "export",
            "1.0",
            capabilityRegistrations);
        var exportProjectOptions = VbaDevCommandGrammar.AddProjectDocumentOptions(ExportCommand);
        ExportProjectOption = exportProjectOptions.Project;
        ExportDocumentOption = exportProjectOptions.Document;
        ExportFromOption = VbaDevCommandGrammar.CreateStringOption(
            "--from",
            "Workbook to export from; skips project resolution when supplied.",
            "path");
        ExportToOption = VbaDevCommandGrammar.CreateStringOption(
            "--to",
            "Directory to export to; defaults to the selected document source set, or the current directory with --from.",
            "dir");
        ExportCommand.Add(ExportFromOption);
        ExportCommand.Add(ExportToOption);
        grammarFailureRules.RequireNonEmpty(ExportProjectOption);
        grammarFailureRules.RequireNonEmpty(ExportDocumentOption);
        grammarFailureRules.RequireNonEmpty(ExportFromOption);
        grammarFailureRules.RequireNonEmpty(ExportToOption);
        grammarFailureRules.Conflicts(
            ExportCommand,
            ExportFromOption,
            ExportProjectOption);
        grammarFailureRules.Conflicts(
            ExportCommand,
            ExportFromOption,
            ExportDocumentOption);
        ExportIntentBinding = grammarFailureRules.BindIntent<VbaDevExportCommandIntent>(
            ExportCommand,
            BindExportIntent);
        ExportCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunExportAsync(parseResult, cancellationToken).ConfigureAwait(false)));

        ImportCommand = VbaDevCommandGrammar.AddCapabilityCommand(
            rootCommand,
            "import",
            "Run a path-only import of VBA sources into an existing workbook; unlike build, it does not use vba-project.json.",
            "import",
            "1.0",
            capabilityRegistrations);
        ImportFromOption = VbaDevCommandGrammar.CreateStringOption(
            "--from",
            "Source directory containing .bas, .cls, and .frm files.",
            "dir");
        ImportFromOption.Required = true;
        ImportToOption = VbaDevCommandGrammar.CreateStringOption(
            "--to",
            "Existing workbook file to update in place.",
            "path");
        ImportToOption.Required = true;
        ImportCommand.Add(ImportFromOption);
        ImportCommand.Add(ImportToOption);
        grammarFailureRules.RequireNonEmpty(ImportFromOption);
        grammarFailureRules.RequireNonEmpty(ImportToOption);
        ImportIntentBinding = grammarFailureRules.BindIntent<VbaDevImportCommandIntent>(
            ImportCommand,
            BindImportIntent);
        ImportCommand.SetAction(async (parseResult, cancellationToken) =>
            VbaDevCommandGrammar.WriteCommandResult(
                parseResult,
                await RunImportAsync(parseResult, cancellationToken).ConfigureAwait(false)));
    }

    internal Command ExportCommand { get; }

    internal Option<string> ExportProjectOption { get; }

    internal Option<string> ExportDocumentOption { get; }

    internal Option<string> ExportFromOption { get; }

    internal Option<string> ExportToOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevExportCommandIntent> ExportIntentBinding { get; }

    internal Command ImportCommand { get; }

    internal Option<string> ImportFromOption { get; }

    internal Option<string> ImportToOption { get; }

    internal VbaDevGrammarIntentBinding<VbaDevImportCommandIntent> ImportIntentBinding { get; }

    internal static VbaDevImportExportCommandFamily Register(
        RootCommand rootCommand,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammarFailureRules,
        ICollection<VbaDevCommandCapabilityRegistration> capabilityRegistrations)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(grammarFailureRules);
        ArgumentNullException.ThrowIfNull(capabilityRegistrations);
        return new VbaDevImportExportCommandFamily(
            rootCommand,
            composition,
            grammarFailureRules,
            capabilityRegistrations);
    }

    private VbaDevGrammarIntentBindResult<VbaDevImportCommandIntent> BindImportIntent(
        ParseResult parseResult)
    {
        var sourceDirectory = parseResult.GetValue(ImportFromOption);
        var targetWorkbook = parseResult.GetValue(ImportToOption);
        return sourceDirectory is null || targetWorkbook is null
            ? VbaDevGrammarIntentBindResult<VbaDevImportCommandIntent>.Unbound
            : VbaDevGrammarIntentBindResult<VbaDevImportCommandIntent>.Bound(
                new VbaDevImportCommandIntent(sourceDirectory, targetWorkbook));
    }

    private VbaDevGrammarIntentBindResult<VbaDevExportCommandIntent> BindExportIntent(
        ParseResult parseResult)
    {
        if (parseResult.GetResult(ExportFromOption) is { Implicit: false })
        {
            var sourceWorkbook = parseResult.GetValue(ExportFromOption);
            return sourceWorkbook is null
                ? VbaDevGrammarIntentBindResult<VbaDevExportCommandIntent>.Unbound
                : VbaDevGrammarIntentBindResult<VbaDevExportCommandIntent>.Bound(
                    new VbaDevExportCommandIntent.ExplicitWorkbook(
                        sourceWorkbook,
                        parseResult.GetValue(ExportToOption)));
        }

        return VbaDevGrammarIntentBindResult<VbaDevExportCommandIntent>.Bound(
            new VbaDevExportCommandIntent.ProjectDocument(
                parseResult.GetValue(ExportProjectOption),
                parseResult.GetValue(ExportDocumentOption),
                parseResult.GetValue(ExportToOption)));
    }

    private async Task<AppCommandResult> RunImportAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = ImportIntentBinding.GetRequiredIntent(parseResult);
        return await composition.ImportCommand.RunAsync(
                new ImportCommandRequest(
                    intent.SourceDirectory,
                    intent.TargetWorkbook,
                    composition.WorkingDirectory),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<AppCommandResult> RunExportAsync(
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        var intent = ExportIntentBinding.GetRequiredIntent(parseResult);
        return intent switch
        {
            VbaDevExportCommandIntent.ExplicitWorkbook explicitWorkbook =>
                composition.ExportCommand.RunExplicitAsync(
                    new ExplicitWorkbookExportCommandRequest(
                        explicitWorkbook.SourceWorkbook,
                        explicitWorkbook.DestinationDirectory,
                        composition.WorkingDirectory),
                    cancellationToken),
            VbaDevExportCommandIntent.ProjectDocument projectDocument =>
                VbaDevCommandGrammar.ResolveDocumentContextAsync(
                    composition,
                    projectDocument.ProjectRoot,
                    projectDocument.DocumentName,
                    (context, operationCancellationToken) =>
                        composition.ExportCommand.RunAsync(
                            context,
                            new ProjectExportCommandRequest(
                                projectDocument.DestinationDirectory,
                                composition.WorkingDirectory),
                            operationCancellationToken),
                    cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported export intent '{intent.GetType().FullName}'.")
        };
    }
}

internal sealed record VbaDevImportCommandIntent(
    string SourceDirectory,
    string TargetWorkbook);

internal abstract record VbaDevExportCommandIntent
{
    private VbaDevExportCommandIntent()
    {
    }

    internal sealed record ProjectDocument(
        string? ProjectRoot,
        string? DocumentName,
        string? DestinationDirectory) : VbaDevExportCommandIntent;

    internal sealed record ExplicitWorkbook(
        string SourceWorkbook,
        string? DestinationDirectory) : VbaDevExportCommandIntent;
}
