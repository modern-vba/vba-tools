namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies the source-unit kind that can consume an intrinsic host Event catalog.
/// </summary>
public enum VbaIntrinsicHostEventSourceKind
{
    UserForm
}

/// <summary>
/// Identifies one Event in an intrinsic host Event catalog.
/// </summary>
public sealed record VbaIntrinsicHostEventIdentity(
    string SourceName,
    string Name);

/// <summary>
/// Represents the structured signature of one intrinsic host Event.
/// </summary>
public sealed record VbaIntrinsicHostEventSignature(
    IReadOnlyList<VbaHostEventParameter> Parameters,
    string? Documentation);

/// <summary>
/// Represents one complete intrinsic host Event contract.
/// </summary>
public sealed record VbaIntrinsicHostEvent(
    VbaIntrinsicHostEventIdentity Identity,
    VbaIntrinsicHostEventSignature Signature,
    bool AuthoringAvailable,
    bool ExistingHandlerRecognizable)
{
    public string Name => Identity.Name;

    public IReadOnlyList<VbaHostEventParameter> Parameters
        => Signature.Parameters;

    public string? Documentation => Signature.Documentation;
}

/// <summary>
/// Represents one immutable environment-scoped catalog of intrinsic host Events.
/// </summary>
public sealed record VbaIntrinsicHostEventCatalog(
    VbaIntrinsicHostEventSourceKind SourceKind,
    string IntrinsicEventSourceName,
    IReadOnlyList<VbaIntrinsicHostEvent> Events,
    VbaIntrinsicHostBaseTypeProvenance? BaseTypeProvenance = null);

/// <summary>
/// Identifies the type-library source of the catalog's intrinsic Event base type.
/// </summary>
public sealed record VbaIntrinsicHostBaseTypeProvenance(
    string Name,
    string LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid);
