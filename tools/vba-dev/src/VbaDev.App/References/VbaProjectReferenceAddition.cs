using System.Collections.Immutable;
using VbaDev.Domain;

namespace VbaDev.App.References;

/// <summary>Prepares conclusive evidence outside the lease and plans against latest selections inside it.</summary>
internal sealed class VbaProjectReferenceAddition
{
    private readonly string documentName;
    private readonly ImmutableHashSet<string> selectedNames;
    private readonly ImmutableDictionary<string, Workbooks.ResolvedVbaProjectReference> resolutions;
    private readonly FileSystemPathIdentity? templateIdentity;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

    private VbaProjectReferenceAddition(
        string documentName,
        ImmutableHashSet<string> selectedNames,
        ImmutableArray<string> requirements,
        ImmutableDictionary<string, Workbooks.ResolvedVbaProjectReference> resolutions,
        FileSystemPathIdentity? templateIdentity,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        ImmutableArray<Warning> warnings)
    {
        this.documentName = documentName;
        this.selectedNames = selectedNames;
        Requirements = requirements;
        this.resolutions = resolutions;
        this.templateIdentity = templateIdentity;
        this.pathIdentityResolver = pathIdentityResolver;
        Warnings = warnings;
    }

    internal ImmutableArray<string> Requirements { get; }
    internal ImmutableArray<Warning> Warnings { get; }

    internal static async Task<VbaProjectReferenceAddition> PrepareEvidenceAsync(
        string documentName,
        ProjectDocument document,
        string templatePath,
        IReadOnlyList<string> requirements,
        VbaProjectReferencePlanner? planner,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        CancellationToken cancellationToken)
    {
        // Copy caller-owned collections before the first external call or await.
        var selected = document.References.Select(reference => reference.Name.Trim())
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = Normalize(requirements);
        var missing = ordered.Where(name => !selected.Contains(name)).ToImmutableArray();
        var identity = missing.Length == 0 ? null : pathIdentityResolver.Resolve(templatePath);
        var resolved = ImmutableDictionary.CreateBuilder<string, Workbooks.ResolvedVbaProjectReference>(
            StringComparer.OrdinalIgnoreCase);
        var warnings = ImmutableArray<Warning>.Empty;
        if (missing.Length > 0)
        {
            if (planner is null)
            {
                throw new InvalidOperationException("CommonModules required-reference resolution is not configured.");
            }

            var batch = await planner.ResolveReferencesAsync(templatePath, missing, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var references = planner.SelectManifestInputReferences(batch with
            {
                References = batch.References.Select(reference => reference with
                {
                    RequestedName = reference.RequestedName.Trim()
                }).ToArray()
            }, missing);
            for (var index = 0; index < missing.Length; index++)
            {
                var reference = references[index];
                if (!SameName(reference.Name, missing[index]))
                {
                    throw new InvalidOperationException(
                        $"Reference resolver returned '{reference.Name}' for requested name '{missing[index]}'.");
                }

                resolved.Add(missing[index], reference with { Name = reference.Name.Trim() });
            }

            warnings = batch.Warnings.Select(warning => new Warning(warning.Code, warning.Message))
                .ToImmutableArray();
        }

        return new(documentName, selected, ordered, resolved.ToImmutable(), identity,
            pathIdentityResolver, warnings);
    }

    internal Plan PlanAddition(
        string latestDocumentName,
        ProjectDocument document,
        string projectRoot,
        IReadOnlyList<string> requirements,
        Intent intent)
    {
        if (!documentName.Equals(latestDocumentName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PlanException(Reason.DocumentChanged,
                $"Reference evidence for document '{documentName}' cannot be used for '{latestDocumentName}'.");
        }

        var selection = document.References.ToList();
        var changes = ImmutableArray.CreateBuilder<Change>();
        foreach (var name in Normalize(requirements))
        {
            var index = selection.FindIndex(reference => SameName(reference.Name, name));
            if (index >= 0)
            {
                var stored = selection[index];
                var promote = intent == Intent.Explicit && !stored.Requested;
                if (promote)
                {
                    selection[index] = stored with { Requested = true };
                }

                changes.Add(new(name, stored.Name, promote ? "promoted" : "alreadyPresent"));
                continue;
            }

            if (selectedNames.Contains(name))
            {
                throw new PlanException(Reason.SelectionChanged,
                    $"Reference '{name}' was removed after reference add began.");
            }

            if (!resolutions.TryGetValue(name, out var resolved))
            {
                throw new PlanException(Reason.MissingEvidence,
                    $"Document '{latestDocumentName}' requires reference '{name}' without prepared resolution evidence.");
            }

            selection.Add(new(resolved.Name, requested: intent == Intent.Explicit));
            changes.Add(new(name, resolved.Name, "added"));
        }

        if (changes.Any(change => change.Status == "added"))
        {
            var latestTemplate = Path.GetFullPath(document.TemplatePath, projectRoot);
            if (templateIdentity is null
                || !FileSystemPathIdentityRelations.Same(templateIdentity, pathIdentityResolver.Resolve(latestTemplate)))
            {
                throw new PlanException(Reason.TemplateChanged,
                    $"Document '{latestDocumentName}' selected a different source template after reference add began. " +
                    $"Prepared: '{templateIdentity?.OperationPath}'; latest: '{latestTemplate}'.");
            }
        }

        return new(selection.ToImmutableArray(), changes.ToImmutable());
    }

    private static ImmutableArray<string> Normalize(IReadOnlyList<string> names)
        => names.Select(name => name.Trim()).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    private static bool SameName(string left, string right)
        => left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    internal enum Intent { Explicit, CommonModules }
    internal enum Reason { DocumentChanged, SelectionChanged, MissingEvidence, TemplateChanged }
    internal sealed class PlanException(Reason reason, string message) : InvalidOperationException(message)
    {
        internal Reason FailureReason { get; } = reason;
    }
    internal sealed record Warning(string Code, string Message);
    internal sealed record Change(string RequestedName, string StoredName, string Status);
    internal sealed record Plan(ImmutableArray<VbaProjectReference> Selection, ImmutableArray<Change> Changes)
    {
        internal bool Changed => Changes.Any(change => change.Status != "alreadyPresent");
    }
}
