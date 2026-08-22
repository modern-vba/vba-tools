using VbaTools.TypeLibRegistry;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Contains reference-name resolutions produced from one TypeLib catalog snapshot.
/// </summary>
/// <param name="Complete">Whether the complete registry catalog was observed.</param>
/// <param name="Warnings">Non-fatal catalog warnings.</param>
/// <param name="Diagnostic">The catalog-level failure diagnostic, when incomplete.</param>
/// <param name="References">The requested reference-name resolutions in input order.</param>
public sealed record VbaProjectReferenceResolutionBatch(
    bool Complete,
    IReadOnlyList<TypeLibRegistryCatalogWarning> Warnings,
    TypeLibRegistryCatalogDiagnostic? Diagnostic,
    IReadOnlyList<VbaProjectReferenceNameResolution> References,
    IReadOnlyList<TypeLibRegistryCatalogDiagnostic>? AdditionalDiagnostics = null)
{
    /// <summary>
    /// Gets every catalog- or probe-level diagnostic in stable occurrence order.
    /// </summary>
    public IReadOnlyList<TypeLibRegistryCatalogDiagnostic> Diagnostics
    {
        get
        {
            var diagnostics = new List<TypeLibRegistryCatalogDiagnostic>();
            if (Diagnostic is not null)
            {
                diagnostics.Add(Diagnostic);
            }

            if (AdditionalDiagnostics is not null)
            {
                diagnostics.AddRange(AdditionalDiagnostics);
            }

            return diagnostics;
        }
    }
}

/// <summary>
/// Describes the registry identities available for one requested reference name.
/// </summary>
/// <param name="RequestedName">The trimmed spelling supplied by the caller.</param>
/// <param name="RegisteredName">The deterministic registered spelling, when the name is registered.</param>
/// <param name="IsRegistered">Whether a readable matching registry description exists.</param>
/// <param name="Matches">The usable registry identities for the name.</param>
public sealed record VbaProjectReferenceNameResolution(
    string RequestedName,
    string? RegisteredName,
    bool IsRegistered,
    IReadOnlyList<ResolvedVbaProjectReference> Matches,
    IReadOnlyList<VbaProjectReferenceCandidateLineage> CandidateLineages,
    IReadOnlyList<ResolvedVbaProjectReference> Candidates,
    string? UnverifiedReasonCode,
    string? Message)
{
    /// <summary>
    /// Creates a resolution whose public matches are also its only probe versions.
    /// </summary>
    public VbaProjectReferenceNameResolution(
        string requestedName,
        string? registeredName,
        bool isRegistered,
        IReadOnlyList<ResolvedVbaProjectReference> matches)
        : this(
            requestedName,
            registeredName,
            isRegistered,
            matches,
            matches
                .Select(match => new VbaProjectReferenceCandidateLineage(match.Guid, [match]))
                .ToArray(),
            matches,
            null,
            null)
    {
    }

    /// <summary>
    /// Creates a resolution with explicit full GUID lineages and public registry candidates.
    /// </summary>
    public VbaProjectReferenceNameResolution(
        string requestedName,
        string? registeredName,
        bool isRegistered,
        IReadOnlyList<ResolvedVbaProjectReference> matches,
        IReadOnlyList<VbaProjectReferenceCandidateLineage> candidateLineages)
        : this(
            requestedName,
            registeredName,
            isRegistered,
            matches,
            candidateLineages,
            matches,
            null,
            null)
    {
    }
}

/// <summary>
/// Retains every registered version that may be attempted for one TypeLib GUID lineage.
/// </summary>
/// <param name="Guid">The lineage TypeLib GUID.</param>
/// <param name="Versions">The registered identities in descending fallback order.</param>
public sealed record VbaProjectReferenceCandidateLineage(
    string Guid,
    IReadOnlyList<ResolvedVbaProjectReference> Versions);
