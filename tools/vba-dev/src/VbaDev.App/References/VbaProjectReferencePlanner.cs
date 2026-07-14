using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.References;

/// <summary>
/// Resolves manifest reference names and creates diagnostics for VBA project reference state.
/// </summary>
public sealed class VbaProjectReferencePlanner
{
    private readonly IVbaProjectReferenceResolver referenceResolver;

    /// <summary>
    /// Creates the reference planner.
    /// </summary>
    /// <param name="referenceResolver">The resolver that maps manifest reference names to concrete catalog identities.</param>
    public VbaProjectReferencePlanner(IVbaProjectReferenceResolver referenceResolver)
    {
        this.referenceResolver = referenceResolver;
    }

    /// <summary>
    /// Resolves user-supplied manifest reference names for storage in vba-project.json.
    /// </summary>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <returns>The unique resolved reference identities.</returns>
    public IReadOnlyList<ResolvedVbaProjectReference> ResolveManifestInputReferences(IReadOnlyList<string> referenceNames)
        => referenceNames
            .Select(ResolveManifestInputReference)
            .ToArray();

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
            .Where(reference => !VbaProjectReferenceCatalogAvailability.HasUsableCatalog(reference.Name))
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reference => DiagnosticResult.Warn(
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
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                "Reference is already present in the source template.");
        }

        var resolution = Resolve(reference.Name);
        if (resolution.Matches.Count == 0)
        {
            return DiagnosticResult.Fail(
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                $"Reference was not found: {reference.Name}.");
        }

        if (resolution.Matches.Count > 1)
        {
            return DiagnosticResult.Fail(
                $"VbaProjectReferences ({documentName}/{reference.Name})",
                $"Reference is ambiguous: {reference.Name}.");
        }

        return DiagnosticResult.Pass(
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

    private ResolvedVbaProjectReference ResolveManifestInputReference(string referenceName)
    {
        var resolution = Resolve(referenceName);
        if (resolution.Matches.Count == 0)
        {
            throw new InvalidOperationException($"VbaProjectReference '{referenceName}' was not found.");
        }

        if (resolution.Matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"VbaProjectReference '{referenceName}' is ambiguous: {FormatNamedCandidates(resolution.Matches)}.");
        }

        return resolution.Matches[0];
    }

    private VbaProjectReferenceResolution Resolve(string referenceName)
        => new(referenceName, referenceResolver.Resolve(referenceName));

    private static string FormatNamedCandidates(IReadOnlyList<ResolvedVbaProjectReference> matches)
        => string.Join(
            ", ",
            matches.Select(match => $"{match.Name} ({match.Guid} {match.Major}.{match.Minor})"));

    private static string FormatCatalogIdentities(IReadOnlyList<ResolvedVbaProjectReference> matches)
        => string.Join(
            ", ",
            matches.Select(match => $"{match.Guid} {match.Major}.{match.Minor}"));

    private sealed record VbaProjectReferenceResolution(
        string ReferenceName,
        IReadOnlyList<ResolvedVbaProjectReference> Matches);
}
