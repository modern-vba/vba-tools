namespace VbaDev.App.HostEvents;

/// <summary>
/// Reads the intrinsic UserForm Event surface from an isolated owned Excel environment.
/// </summary>
public interface IHostEventCatalogAutomation
{
    /// <summary>
    /// Returns an authoritative catalog only after exact owned-process release
    /// and STA dispatcher retirement have been proved.
    /// </summary>
    Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Contains the generic intrinsic Event surface for a generated empty UserForm.
/// </summary>
public sealed record IntrinsicHostEventCatalog(
    string IntrinsicEventSourceName,
    IReadOnlyList<HostEvent> Events,
    HostEventBaseTypeProvenance? BaseTypeProvenance = null);

/// <summary>
/// Contains one complete intrinsic Event observation.
/// </summary>
public sealed record HostEvent(
    HostEventIdentity Identity,
    HostEventSignature Signature,
    bool AuthoringAvailable,
    bool ExistingHandlerRecognizable);

/// <summary>
/// Identifies an Event within one generic intrinsic source.
/// </summary>
public sealed record HostEventIdentity(string SourceName, string Name);

/// <summary>
/// Contains one ordered intrinsic Event signature.
/// </summary>
public sealed record HostEventSignature(
    IReadOnlyList<HostEventParameter> Parameters,
    string? Documentation);

/// <summary>
/// Contains one ordered parameter in an intrinsic Event signature.
/// </summary>
public sealed record HostEventParameter(
    string Name,
    HostEventTypeReference Type,
    HostEventPassingMechanism Passing,
    HostEventArrayShape ArrayShape,
    bool Optional,
    bool ParamArray);

/// <summary>
/// Identifies how an Event parameter is passed.
/// </summary>
public enum HostEventPassingMechanism
{
    ByVal,
    ByRef
}

/// <summary>
/// Identifies whether an Event parameter is a scalar or array.
/// </summary>
public enum HostEventArrayShape
{
    Scalar,
    Array
}

/// <summary>
/// Carries portable type evidence for one Event parameter.
/// </summary>
public abstract record HostEventTypeReference;

/// <summary>
/// Carries one canonical intrinsic VBA type name.
/// </summary>
public sealed record IntrinsicHostEventTypeReference(string Name) : HostEventTypeReference;

/// <summary>
/// Carries a portable TypeLib type identity without registry-path coupling.
/// </summary>
public sealed record TypeLibHostEventTypeReference(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
    : HostEventTypeReference;

/// <summary>
/// Retains opaque type display text without establishing canonical equality.
/// </summary>
public sealed record UnresolvedHostEventTypeReference(string DisplayName) : HostEventTypeReference;

/// <summary>
/// Carries optional catalog-resolvable base host type provenance.
/// </summary>
public sealed record HostEventBaseTypeProvenance(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid);
