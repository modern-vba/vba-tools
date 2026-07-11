using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.References;

public sealed class VbaProjectReferencePlanner
{
    private readonly IVbaProjectReferenceResolver referenceResolver;

    public VbaProjectReferencePlanner(IVbaProjectReferenceResolver referenceResolver)
    {
        this.referenceResolver = referenceResolver;
    }

    public IReadOnlyList<ResolvedVbaProjectReference> ResolveManifestInputReferences(IReadOnlyList<string> referenceNames)
        => referenceNames
            .Select(ResolveManifestInputReference)
            .ToArray();

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

    public DiagnosticResult? CreateManifestReferenceConsistencyDiagnostic(string documentName, ProjectDocument document)
    {
        var selection = VbaProjectReferenceSelection.Create(document.Kind, document.References);
        return selection.MissingExpectedMainReference is null
            ? null
            : DiagnosticResult.Warn(
                $"VbaProjectReferences ({documentName})",
                $"Manifest/reference consistency warning: document kind '{document.Kind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly.");
    }

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
