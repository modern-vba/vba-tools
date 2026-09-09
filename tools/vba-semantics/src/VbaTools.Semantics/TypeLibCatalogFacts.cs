using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Represents TypeLib metadata in a COM-independent shape used to build reference catalogs.
/// </summary>
/// <param name="QualifierAlias">The preferred VBA qualifier alias for the library.</param>
/// <param name="Types">The public types exposed by the library.</param>
/// <param name="ReferencedVbaProjectName">The exact project name exported by the loaded TypeLib, without fallback.</param>
public sealed record TypeLibCatalogMetadata(
    string QualifierAlias,
    IReadOnlyList<TypeLibCatalogType> Types,
    string? ReferencedVbaProjectName = null);

/// <summary>
/// Identifies the raw COM type category retained for TypeLib Event analysis.
/// </summary>
public enum TypeLibCatalogRawTypeKind
{
    Other,
    CoClass,
    Interface,
    Dispatch
}

/// <summary>
/// Retains one callable member's raw TypeLib identity and flags.
/// </summary>
public sealed record TypeLibCatalogCallableMetadata(
    int MemberId,
    int FunctionFlags,
    bool IsComplete = true)
{
    /// <summary>
    /// Gets the physical TypeLib Property invoke kind, when known.
    /// </summary>
    public VbaPropertyAccessorKind? PropertyAccessorKind { get; init; }

    /// <summary>
    /// Gets whether a Function or Property Get result is an array, or null when unavailable.
    /// </summary>
    public bool? IsReturnArray { get; init; }
}

/// <summary>
/// Retains one coclass implemented-interface association, raw type category, and callable surface.
/// A complete association may still carry an incomplete callable surface.
/// </summary>
public sealed record TypeLibCatalogImplementedInterface(
    string Name,
    int TypeFlags,
    int ImplementationFlags,
    IReadOnlyList<TypeLibCatalogMember> CallableMembers,
    TypeLibCatalogRawTypeKind? RawTypeKind = null,
    bool IsComplete = true);

/// <summary>
/// Retains the complete raw type identity and implemented-interface association set
/// required to derive one class's Event surface.
/// </summary>
public sealed record TypeLibCatalogTypeMetadata(
    TypeLibCatalogRawTypeKind RawTypeKind,
    int TypeFlags,
    IReadOnlyList<TypeLibCatalogImplementedInterface> ImplementedInterfaces,
    bool IsComplete = true);

/// <summary>
/// Represents one TypeLib type and its members.
/// </summary>
/// <param name="Name">The type name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Documentation">The type documentation.</param>
/// <param name="Members">The members exposed by the type.</param>
/// <param name="IsCreatable">Whether the TypeLib type is a coclass that can be used with New.</param>
/// <param name="IsApplicationObject">Whether TypeLib metadata marks the type as an application object.</param>
/// <param name="IsBrowsable">Whether the type itself belongs to the public browsable surface.</param>
public sealed record TypeLibCatalogType(
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation,
    IReadOnlyList<TypeLibCatalogMember> Members,
    bool IsCreatable = false,
    bool IsApplicationObject = false,
    bool IsBrowsable = true,
    TypeLibCatalogTypeMetadata? Metadata = null);

/// <summary>
/// Represents one TypeLib member.
/// </summary>
/// <param name="Name">The member name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Documentation">The member documentation.</param>
/// <param name="Signature">The callable signature, when the member is callable.</param>
/// <param name="TypeReference">The member result type, when known.</param>
/// <param name="PropertyAccess">The property operations represented by the TypeLib member.</param>
public sealed record TypeLibCatalogMember(
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation,
    VbaCallableSignature? Signature = null,
    VbaTypeReference? TypeReference = null,
    VbaPropertyAccess PropertyAccess = VbaPropertyAccess.Unknown,
    TypeLibCatalogCallableMetadata? Metadata = null);
