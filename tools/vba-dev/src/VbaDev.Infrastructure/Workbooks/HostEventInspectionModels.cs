using VbaLanguageServer.Syntax;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record UserFormEventComponentDescriptor(
    int Ordinal,
    UserFormEventComponentIdentity Identity);

internal sealed record UserFormEventComponentIdentity
{
    public UserFormEventComponentIdentity(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!VbaIdentifier.IsIdentifier(name))
        {
            throw new InvalidOperationException(
                "The intrinsic component name must be an exact VBA identifier.");
        }

        Name = name;
    }

    public string Name { get; }
}

internal abstract record UserFormEventInspectionResult(UserFormEventComponentIdentity Identity);

internal sealed record ResolvedUserFormEventInspection(
    UserFormEventComponentIdentity Identity,
    string IntrinsicEventSourceName,
    IReadOnlyList<UserFormEventObservation> Events,
    UserFormEventBaseTypeProvenance? BaseTypeProvenance = null)
    : UserFormEventInspectionResult(Identity);

internal sealed record UnverifiedUserFormEventInspection(
    UserFormEventComponentIdentity Identity,
    UserFormEventInspectionFailureReason Reason,
    string Message)
    : UserFormEventInspectionResult(Identity);

internal enum UserFormEventInspectionFailureReason
{
    EventEnumerationFailure,
    IntrinsicEventSourceNameReadFailure,
    SignatureReadFailure,
    AvailabilityReadFailure,
    InspectionFailure
}

internal sealed record UserFormEventObservation(
    string Name,
    IReadOnlyList<ObservedHostEventParameter> Parameters,
    string? Documentation,
    bool AuthoringAvailable,
    bool ExistingHandlerRecognizable);

internal sealed record ObservedHostEventParameter(
    string Name,
    ObservedHostEventTypeReference Type,
    ObservedHostEventPassingMechanism Passing,
    ObservedHostEventArrayShape ArrayShape,
    bool Optional,
    bool ParamArray);

internal enum ObservedHostEventPassingMechanism
{
    ByVal,
    ByRef
}

internal enum ObservedHostEventArrayShape
{
    Scalar,
    Array
}

internal abstract record ObservedHostEventTypeReference;

internal sealed record ObservedIntrinsicHostEventTypeReference(string Name)
    : ObservedHostEventTypeReference;

internal sealed record ObservedTypeLibHostEventTypeReference(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid)
    : ObservedHostEventTypeReference;

internal sealed record ObservedUnresolvedHostEventTypeReference(string DisplayName)
    : ObservedHostEventTypeReference;

internal sealed record UserFormEventBaseTypeProvenance(
    string Name,
    Guid LibraryGuid,
    int MajorVersion,
    int MinorVersion,
    int Lcid);
