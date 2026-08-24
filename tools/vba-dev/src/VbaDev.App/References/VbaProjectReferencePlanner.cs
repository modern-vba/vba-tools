using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.References;

/// <summary>
/// Resolves manifest reference names and creates diagnostics for VBA project reference state.
/// </summary>
public sealed class VbaProjectReferencePlanner
{
    private const string VisualBasicForApplications = "Visual Basic For Applications";
    private readonly IVbaProjectReferenceResolver referenceResolver;
    private readonly IVbaProjectReferenceAmbiguityProbe? ambiguityProbe;

    /// <summary>
    /// Creates the reference planner.
    /// </summary>
    /// <param name="referenceResolver">The resolver that maps manifest reference names to concrete catalog identities.</param>
    public VbaProjectReferencePlanner(
        IVbaProjectReferenceResolver referenceResolver,
        IVbaProjectReferenceAmbiguityProbe? ambiguityProbe = null)
    {
        this.referenceResolver = referenceResolver;
        this.ambiguityProbe = ambiguityProbe;
    }

    /// <summary>
    /// Resolves user-supplied manifest reference names for storage in vba-project.json.
    /// </summary>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <returns>The unique resolved reference identities.</returns>
    public IReadOnlyList<ResolvedVbaProjectReference> ResolveManifestInputReferences(IReadOnlyList<string> referenceNames)
    {
        var batch = ResolveReferences(referenceNames);
        return SelectManifestInputReferences(batch, referenceNames);
    }

    /// <summary>
    /// Resolves reference names from one catalog snapshot without applying command policy.
    /// </summary>
    /// <param name="referenceNames">The ordered human-visible reference names.</param>
    /// <returns>The complete batch result.</returns>
    public VbaProjectReferenceResolutionBatch ResolveReferences(IReadOnlyList<string> referenceNames)
        => referenceResolver.Resolve(referenceNames);

    /// <summary>
    /// Resolves and filters the registered available-reference inventory without probing ambiguity.
    /// </summary>
    /// <param name="excludedReferenceNames">Manifest names to remove using trimmed ordinal-ignore-case comparison.</param>
    /// <returns>The ordered available-reference registry batch.</returns>
    public VbaProjectReferenceResolutionBatch ResolveAvailableReferences(
        IReadOnlyList<string> excludedReferenceNames)
    {
        var excludedNames = excludedReferenceNames
            .Select(name => name.Trim())
            .Append(VisualBasicForApplications)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batch = referenceResolver.ResolveAvailable();
        return batch with
        {
            References = batch.References
                .Where(reference => !excludedNames.Contains(
                    (reference.RegisteredName ?? reference.RequestedName).Trim()))
                .ToArray()
        };
    }

    /// <summary>
    /// Resolves every registered description not already selected by the document.
    /// </summary>
    /// <param name="baselineWorkbookPath">The selected source-template ambiguity baseline.</param>
    /// <param name="excludedReferenceNames">Manifest names to remove using trimmed ordinal-ignore-case comparison.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The ordered available-reference resolution batch.</returns>
    public async Task<VbaProjectReferenceResolutionBatch> ResolveAvailableReferencesAsync(
        string baselineWorkbookPath,
        IReadOnlyList<string> excludedReferenceNames,
        CancellationToken cancellationToken)
        => await ResolveAvailableReferencesAsync(
                VbaProjectReferenceProbeBaseline.SourceTemplate(baselineWorkbookPath),
                excludedReferenceNames,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Resolves every registered description not already selected in the requested scope.
    /// </summary>
    /// <param name="baseline">The source-template or blank-workbook ambiguity baseline.</param>
    /// <param name="excludedReferenceNames">Names to remove using trimmed ordinal-ignore-case comparison.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The ordered available-reference resolution batch.</returns>
    public async Task<VbaProjectReferenceResolutionBatch> ResolveAvailableReferencesAsync(
        VbaProjectReferenceProbeBaseline baseline,
        IReadOnlyList<string> excludedReferenceNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var batch = ResolveAvailableReferences(excludedReferenceNames);
        if (!batch.Complete ||
            ambiguityProbe is null ||
            !batch.References.Any(reference => reference.Matches.Count > 1))
        {
            return batch;
        }

        return await ambiguityProbe.ResolveAsync(
                baseline,
                batch,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves reference names and probes registry ambiguity against the selected document template.
    /// </summary>
    /// <param name="context">The selected project document context.</param>
    /// <param name="referenceNames">The ordered human-visible reference names.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The complete or partial ordered resolution batch.</returns>
    public async Task<VbaProjectReferenceResolutionBatch> ResolveReferencesAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> referenceNames,
        CancellationToken cancellationToken)
        => await ResolveReferencesAsync(
                context.TemplateDocumentPath,
                referenceNames,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Resolves reference names and probes registry ambiguity against an explicit source-template baseline.
    /// </summary>
    /// <param name="baselineWorkbookPath">The caller-selected source-template workbook.</param>
    /// <param name="referenceNames">The ordered human-visible reference names.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The complete or partial ordered resolution batch.</returns>
    public async Task<VbaProjectReferenceResolutionBatch> ResolveReferencesAsync(
        string baselineWorkbookPath,
        IReadOnlyList<string> referenceNames,
        CancellationToken cancellationToken)
    {
        var batch = ResolveReferences(referenceNames);
        if (!batch.Complete ||
            ambiguityProbe is null ||
            !batch.References.Any(reference => reference.Matches.Count > 1))
        {
            return batch;
        }

        return await ambiguityProbe.ResolveAsync(
                VbaProjectReferenceProbeBaseline.SourceTemplate(baselineWorkbookPath),
                batch,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Selects unique identities for names from an existing complete batch.
    /// </summary>
    /// <param name="batch">The batch produced for the requested names.</param>
    /// <param name="referenceNames">The ordered names supplied to the resolver.</param>
    /// <returns>The unique identities in request order.</returns>
    public IReadOnlyList<ResolvedVbaProjectReference> SelectManifestInputReferences(
        VbaProjectReferenceResolutionBatch batch,
        IReadOnlyList<string> referenceNames)
    {
        EnsureComplete(batch);
        if (batch.References.Count != referenceNames.Count)
        {
            throw new InvalidOperationException(
                "Reference resolver did not return a complete ordered result batch.");
        }

        return referenceNames
            .Select((referenceName, index) =>
            {
                var resolution = batch.References[index];
                if (!resolution.RequestedName.Equals(
                        referenceName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Reference resolver did not return a complete ordered result batch.");
                }

                return ResolveManifestInputReference(resolution);
            })
            .ToArray();
    }

    /// <summary>
    /// Resolves a document manifest reference before adding it to a workbook.
    /// </summary>
    /// <param name="documentName">The document name used in error messages.</param>
    /// <param name="referenceName">The manifest reference name to resolve.</param>
    /// <returns>The concrete reference identity to add through VBIDE.</returns>
    public ResolvedVbaProjectReference ResolveDocumentReference(string documentName, string referenceName)
    {
        var resolution = Resolve(referenceName);
        if (resolution.Matches.Count == 0)
        {
            throw new InvalidOperationException($"VbaProjectReference '{referenceName}' for document '{documentName}' was not found.");
        }

        if (resolution.Matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"VbaProjectReference '{referenceName}' for document '{documentName}' is ambiguous: {FormatCatalogIdentities(resolution.Matches)}.");
        }

        return resolution.Matches[0];
    }

    /// <summary>
    /// Creates a diagnostic when a document is missing its expected main host object library reference.
    /// </summary>
    /// <param name="documentName">The document name used in the diagnostic name.</param>
    /// <param name="document">The document manifest entry to inspect.</param>
    /// <returns>A warning diagnostic, or null when no consistency issue is present.</returns>
    public DiagnosticResult? CreateManifestReferenceConsistencyDiagnostic(string documentName, ProjectDocument document)
    {
        var selection = VbaProjectReferenceSelection.Create(document.Kind, document.References);
        return selection.MissingExpectedMainReference is null
            ? null
            : DiagnosticResult.Warn(
                ReferenceCheckId(documentName, null, "manifestConsistency"),
                $"VbaProjectReferences ({documentName})",
                $"Manifest/reference consistency warning: document kind '{document.Kind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly.");
    }

    /// <summary>
    /// Creates diagnostics for manifest references that have no usable editor catalog metadata.
    /// </summary>
    /// <param name="documentName">The document name used in diagnostic names.</param>
    /// <param name="document">The document manifest entry to inspect.</param>
    /// <returns>Warning diagnostics for references without bundled or cached catalogs.</returns>
    public IReadOnlyList<DiagnosticResult> CreateReferenceCatalogAvailabilityDiagnostics(
        string documentName,
        ProjectDocument document)
        => document.References
            .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(reference => !VbaProjectReferenceCatalogAvailability.HasUsableCatalog(reference.Name))
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reference => DiagnosticResult.Warn(
                ReferenceCheckId(documentName, reference.Name, "catalogAvailability"),
                $"VbaProjectReferenceCatalog ({documentName}/{reference.Name})",
                "No bundled or cached VbaProjectReferenceCatalog metadata is available. The reference remains active, but external editor definitions are unavailable."))
            .ToArray();

    /// <summary>
    /// Creates a diagnostic that checks whether one manifest reference is available to build or already present.
    /// </summary>
    /// <param name="documentName">The document name used in the diagnostic name.</param>
    /// <param name="reference">The manifest reference to validate.</param>
    /// <param name="templateReferences">The reference names already present in the source template workbook.</param>
    /// <returns>A diagnostic describing reference availability.</returns>
    public DiagnosticResult CreateReferenceResolutionDiagnostic(
        string documentName,
        VbaProjectReference reference,
        IReadOnlySet<string> templateReferences)
    {
        if (templateReferences.Contains(reference.Name))
        {
            return DiagnosticResult.Pass(
                ReferenceCheckId(documentName, reference.Name, "resolution"),
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                "Reference is already present in the source template.");
        }

        var resolution = Resolve(reference.Name);
        return CreateReferenceResolutionDiagnostic(
            documentName,
            reference,
            resolution);
    }

    /// <summary>
    /// Creates a Doctor diagnostic from one already completed registry/VBE resolution entry.
    /// </summary>
    public DiagnosticResult CreateReferenceResolutionDiagnostic(
        string documentName,
        VbaProjectReference reference,
        VbaProjectReferenceNameResolution resolution)
    {
        if (resolution.UnverifiedReasonCode is not null)
        {
            return DiagnosticResult.Fail(
                ReferenceCheckId(documentName, reference.Name, "resolution"),
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                $"Reference verification did not complete ({resolution.UnverifiedReasonCode}): {resolution.Message}");
        }

        if (resolution.Matches.Count == 0)
        {
            return DiagnosticResult.Fail(
                ReferenceCheckId(documentName, reference.Name, "resolution"),
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                $"Reference was not found: {reference.Name}.");
        }

        if (resolution.Matches.Count > 1)
        {
            return DiagnosticResult.Fail(
                ReferenceCheckId(documentName, reference.Name, "resolution"),
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                $"Reference is ambiguous: {reference.Name}.");
        }

        return DiagnosticResult.Pass(
            ReferenceCheckId(documentName, reference.Name, "resolution"),
            $"VbaProjectReferences ({documentName}/{reference.Name})",
            "Reference resolved.");
    }

    /// <summary>
    /// Formats the warning emitted when a workbook keeps a protected reference not listed in the manifest.
    /// </summary>
    /// <param name="documentName">The document name used in the warning.</param>
    /// <param name="referenceName">The protected reference name.</param>
    /// <returns>The warning line to include in command output.</returns>
    public static string FormatProtectedReferenceWarning(string documentName, string referenceName)
        => $"[WARN] VbaProjectReferences ({documentName}/{referenceName}): Unlisted protected reference remains.";

    private static ResolvedVbaProjectReference ResolveManifestInputReference(
        VbaProjectReferenceNameResolution resolution)
    {
        if (resolution.Matches.Count == 0)
        {
            var detail = resolution.IsRegistered
                ? "has no usable registry identity"
                : "was not found";
            throw new InvalidOperationException(
                $"VbaProjectReference '{resolution.RequestedName}' {detail}.");
        }

        if (resolution.Matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"VbaProjectReference '{resolution.RequestedName}' is ambiguous: {FormatNamedCandidates(resolution.Matches)}.");
        }

        return resolution.Matches[0];
    }

    private static string ReferenceCheckId(
        string documentName,
        string? referenceName,
        string finding)
        => referenceName is null
            ? $"project.references.{Uri.EscapeDataString(documentName)}.{finding}"
            : $"project.references.{Uri.EscapeDataString(documentName)}." +
              $"{Uri.EscapeDataString(referenceName)}.{finding}";

    private VbaProjectReferenceNameResolution Resolve(string referenceName)
    {
        var batch = ResolveReferences([referenceName]);
        EnsureComplete(batch);
        return GetResolution(batch, referenceName);
    }

    private static void EnsureComplete(VbaProjectReferenceResolutionBatch batch)
    {
        if (batch.Complete)
        {
            return;
        }

        var diagnostics = batch.Diagnostics
            .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            .Concat(batch.References
                .Where(reference => reference.UnverifiedReasonCode is not null)
                .Select(reference =>
                    $"{reference.UnverifiedReasonCode} ({reference.RequestedName}): " +
                    (reference.Message ?? "Reference verification did not complete.")))
            .ToArray();
        throw new InvalidOperationException(
            diagnostics.Length == 0
                ? "referenceResolutionIncomplete: Reference resolution did not complete."
                : string.Join(Environment.NewLine, diagnostics));
    }

    private static VbaProjectReferenceNameResolution GetResolution(
        VbaProjectReferenceResolutionBatch batch,
        string referenceName)
        => batch.References.FirstOrDefault(reference =>
               reference.RequestedName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException(
               $"Reference resolver did not return a result for '{referenceName}'.");

    private static string FormatNamedCandidates(IReadOnlyList<ResolvedVbaProjectReference> matches)
        => string.Join(
            ", ",
            matches.Select(match => $"{match.Name} ({match.Guid} {match.Major}.{match.Minor})"));

    private static string FormatCatalogIdentities(IReadOnlyList<ResolvedVbaProjectReference> matches)
        => string.Join(
            ", ",
            matches.Select(match => $"{match.Guid} {match.Major}.{match.Minor}"));

}
