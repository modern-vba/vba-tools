using System.CommandLine;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.DebugPreparation;
using VbaDev.Composition;
using VbaDev.Domain;

namespace VbaDev.Cli;

/// <summary>Owns finite source-workbook preparation; it does not host a debugger.</summary>
internal sealed class VbaDevDebugPreparationCommandFamily
{
    internal Command Command { get; private set; } = null!;

    internal VbaDevGrammarIntentBinding<VbaDevDebugPreparationCommandIntent> IntentBinding
        { get; private set; } = null!;

    internal static VbaDevDebugPreparationCommandFamily Register(
        RootCommand root,
        ToolingApplicationComposition composition,
        VbaDevGrammarFailureRules grammar,
        ICollection<VbaDevCommandCapabilityRegistration> capabilities,
        VbaDevCommandFamilyOwnership ownership)
    {
        var family = new VbaDevDebugPreparationCommandFamily();
        var command = VbaDevCommandGrammar.AddCapabilityCommand(
            root, "prepare-debug",
            "Import an immutable source snapshot into the exact already-open source workbook without saving it.",
            "prepare-debug", "1.0", capabilities);
        family.Command = command;
        var selection = VbaDevCommandGrammar.AddProjectDocumentOptions(command, grammar);
        var describe = new Option<bool>("--describe")
        {
            Description = "Resolve the selected source workbook without starting Excel or preparing code."
        };
        var snapshot = VbaDevCommandGrammar.CreateStringOption(
            "--source-snapshot", "Complete caller-owned source snapshot directory.", "dir");
        var generation = VbaDevCommandGrammar.CreateStringOption(
            "--generation", "Invocation-bound lowercase hexadecimal preparation generation token.", "token");
        var processId = new Option<int>("--excel-process-id")
        {
            Description = "Exact existing Excel process ID."
        };
        var startTicks = new Option<long>("--excel-process-start-utc-ticks")
        {
            Description = "Exact Excel process start identity in UTC ticks."
        };
        var interactive = new Option<bool>("--interactive")
        {
            Description = "Allow explicit confirmation for unsaved workbook changes (default true).",
            DefaultValueFactory = _ => true
        };
        command.Add(snapshot);
        command.Add(describe);
        command.Add(generation);
        command.Add(processId);
        command.Add(startTicks);
        command.Add(interactive);
        grammar.RequireNonEmpty(snapshot);
        grammar.RequireNonEmpty(generation);
        family.IntentBinding = grammar.BindIntent<VbaDevDebugPreparationCommandIntent>(command, parseResult =>
        {
            if (parseResult.GetValue(describe))
            {
                if (parseResult.GetResult(snapshot) is { Implicit: false }
                    || parseResult.GetResult(generation) is { Implicit: false }
                    || parseResult.GetResult(processId) is { Implicit: false }
                    || parseResult.GetResult(startTicks) is { Implicit: false }
                    || parseResult.GetResult(interactive) is { Implicit: false })
                    return VbaDevGrammarIntentBindResult<VbaDevDebugPreparationCommandIntent>.Unbound;
                return VbaDevGrammarIntentBindResult<VbaDevDebugPreparationCommandIntent>.Bound(
                    new VbaDevDebugPreparationCommandIntent.Describe(
                        parseResult.GetValue(selection.Project), parseResult.GetValue(selection.Document)));
            }
            if (parseResult.GetResult(snapshot) is not { Implicit: false }
                || parseResult.GetResult(generation) is not { Implicit: false }
                || parseResult.GetResult(processId) is not { Implicit: false }
                || parseResult.GetResult(startTicks) is not { Implicit: false })
                return VbaDevGrammarIntentBindResult<VbaDevDebugPreparationCommandIntent>.Unbound;
            return VbaDevGrammarIntentBindResult<VbaDevDebugPreparationCommandIntent>.Bound(
                new VbaDevDebugPreparationCommandIntent.Prepare(
                    parseResult.GetValue(selection.Project), parseResult.GetValue(selection.Document),
                    parseResult.GetValue(snapshot)!, parseResult.GetValue(generation)!,
                    parseResult.GetValue(processId), parseResult.GetValue(startTicks),
                    parseResult.GetValue(interactive)));
        });
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var intent = family.IntentBinding.GetRequiredIntent(parseResult);
            if (intent is VbaDevDebugPreparationCommandIntent.Describe descriptionIntent)
            {
                var description = await VbaDevCommandGrammar.ResolveDocumentContextAsync(
                    composition, descriptionIntent.ProjectRoot, descriptionIntent.DocumentName,
                    (context, _) => Task.FromResult(
                        !context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase)
                            ? CommandResult.UsageError("Debug preparation supports only Excel documents.")
                            : CommandResult.Success(JsonSerializer.Serialize(new
                            {
                                type = "debugWorkbookDescription", schemaVersion = "1.0",
                                projectRoot = context.ProjectRoot, documentName = context.DocumentName,
                                workbookPath = Path.GetFullPath(context.TemplateDocumentPath)
                            }) + Environment.NewLine)), cancellationToken).ConfigureAwait(false);
                return VbaDevCommandGrammar.WriteCommandResult(parseResult, description);
            }
            var preparation = intent as VbaDevDebugPreparationCommandIntent.Prepare
                ?? throw new InvalidOperationException("Unsupported Debug preparation intent.");
            if (!VbaDevWorkbookConfirmationInput.SupportsDebugPreparation)
                return VbaDevCommandGrammar.WriteCommandResult(parseResult,
                    CommandResult.UsageError(
                        "prepare-debug requires --cancellation-transport stdin-v1 and a caller-owned, " +
                        "generation-bound preparation continuation. No workbook was changed."));
            var request = new DebugWorkbookPreparationRequest(
                preparation.SourceSnapshotDirectory, composition.WorkingDirectory,
                preparation.GenerationId, preparation.ExcelProcessId,
                preparation.ExcelProcessStartUtcTicks);
            var result = await VbaDevCommandGrammar.ResolveDocumentContextAsync(
                composition, preparation.ProjectRoot, preparation.DocumentName,
                (context, token) => composition.DebugWorkbookPreparationCommand.RunAsync(
                    context, request,
                    preparation.Interactive
                        ? VbaDevWorkbookConfirmationInput.ConfirmAsync : null,
                    (ready, continuationToken) => VbaDevWorkbookConfirmationInput.ContinueDebugPreparationAsync(
                        ready.GenerationId, ready.WorkbookPath, ready.ExcelProcessId,
                        ready.ExcelProcessStartUtcTicks, continuationToken), token),
                cancellationToken).ConfigureAwait(false);
            return VbaDevCommandGrammar.WriteCommandResult(parseResult, result);
        });
        ownership.Register(family, command);
        return family;
    }

}

internal abstract record VbaDevDebugPreparationCommandIntent
{
    private VbaDevDebugPreparationCommandIntent() { }

    internal sealed record Describe(string? ProjectRoot, string? DocumentName)
        : VbaDevDebugPreparationCommandIntent;

    internal sealed record Prepare(
        string? ProjectRoot,
        string? DocumentName,
        string SourceSnapshotDirectory,
        string GenerationId,
        int ExcelProcessId,
        long ExcelProcessStartUtcTicks,
        bool Interactive) : VbaDevDebugPreparationCommandIntent;
}
