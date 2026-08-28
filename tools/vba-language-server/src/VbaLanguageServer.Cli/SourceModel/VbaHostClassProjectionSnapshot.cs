namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies the exact manifest document and source template that produced a host-class projection.
/// </summary>
public sealed record VbaHostClassProjectionContext(
    string Project,
    string Document,
    string SourceTemplate);

/// <summary>
/// Represents one immutable consumer-owned host-class projection snapshot.
/// </summary>
public sealed record VbaHostClassProjectionSnapshot(
    long Revision,
    VbaHostClassProjectionContext Context,
    bool ClassEnumerationComplete,
    IReadOnlyList<VbaHostClassProjectionEntry> Classes,
    string? VbaProjectName = null,
    string? SourceTemplateFingerprint = null);

/// <summary>
/// Identifies the supported host-class component kinds.
/// </summary>
public enum VbaHostClassKind
{
    Form,
    Document
}

/// <summary>
/// Identifies one host class by exported VBA name and component kind.
/// </summary>
public sealed record VbaHostClassIdentity(
    string Name,
    VbaHostClassKind Kind);

/// <summary>
/// Represents one host class in a projection snapshot.
/// </summary>
public abstract record VbaHostClassProjectionEntry(
    VbaHostClassIdentity Identity);

/// <summary>
/// Represents authoritative projection evidence from the current inspection.
/// </summary>
public sealed record VbaCurrentHostClassProjectionEntry(
    VbaHostClassIdentity Identity,
    VbaHostClassProjection Projection)
    : VbaHostClassProjectionEntry(Identity);

/// <summary>
/// Represents advisory projection evidence retained from a prior successful inspection.
/// </summary>
public sealed record VbaLastKnownGoodHostClassProjectionEntry(
    VbaHostClassIdentity Identity,
    VbaHostClassProjection Projection)
    : VbaHostClassProjectionEntry(Identity);

/// <summary>
/// Represents a host class for which no usable projection evidence is available.
/// </summary>
public sealed record VbaIndeterminateHostClassProjectionEntry(
    VbaHostClassIdentity Identity)
    : VbaHostClassProjectionEntry(Identity);

/// <summary>
/// Represents the immutable intrinsic Event surface for one host class.
/// </summary>
public sealed record VbaHostClassProjection(
    string IntrinsicEventSourceName,
    IReadOnlyList<VbaHostEventSignature> Events,
    VbaHostClassBaseTypeProvenance? BaseTypeProvenance = null);

/// <summary>
/// Identifies the type-library source of an inspected host class base type.
/// </summary>
public sealed record VbaHostClassBaseTypeProvenance(
    string Name,
    string LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid);

/// <summary>
/// Represents one complete host Event signature.
/// </summary>
public sealed record VbaHostEventSignature(
    string Name,
    IReadOnlyList<VbaHostEventParameter> Parameters,
    string? Documentation,
    bool AuthoringAvailable,
    bool ExistingHandlerRecognizable);

public enum VbaHostEventParameterPassing
{
    ByVal,
    ByRef
}

public enum VbaHostEventParameterArrayShape
{
    Scalar,
    Array
}

/// <summary>
/// Represents one parameter in a projected host Event signature.
/// </summary>
public sealed record VbaHostEventParameter(
    string Name,
    VbaHostEventParameterType Type,
    VbaHostEventParameterPassing Passing,
    VbaHostEventParameterArrayShape ArrayShape,
    bool Optional,
    bool ParamArray);

public abstract record VbaHostEventParameterType;

public sealed record VbaIntrinsicHostEventParameterType(string Name)
    : VbaHostEventParameterType;

public sealed record VbaTypeLibraryHostEventParameterType(
    string Name,
    string LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
    : VbaHostEventParameterType;

public sealed record VbaUnresolvedHostEventParameterType(string DisplayName)
    : VbaHostEventParameterType;
