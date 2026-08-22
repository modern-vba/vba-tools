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
    IReadOnlyList<VbaProjectReferenceNameResolution> References);

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
    IReadOnlyList<ResolvedVbaProjectReference> Matches);
