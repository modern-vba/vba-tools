using Catalog = VbaDev.App.HostEvents;

namespace VbaDev.Infrastructure.Workbooks;

internal static class ExcelComUserFormEventInspector
{
    public static Catalog.IntrinsicHostEventCatalog Inspect(
        ExcelComWorkbookSession.ExcelComHostObjects host,
        object workbook,
        UserFormEventComponentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var observation = ExcelComIntrinsicUserFormEventInspector.Inspect(
            host,
            workbook,
            descriptor);
        return observation is ResolvedUserFormEventInspection resolved
            ? CreateCatalog(resolved)
            : throw new InvalidOperationException(
                "The empty UserForm Event catalog was not authoritative: " +
                ((UnverifiedUserFormEventInspection)observation).Message);
    }

    internal static Catalog.IntrinsicHostEventCatalog CreateCatalog(
        ResolvedUserFormEventInspection observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new Catalog.IntrinsicHostEventCatalog(
            observation.IntrinsicEventSourceName,
            observation.Events.Select(inspectedEvent => new Catalog.HostEvent(
                new Catalog.HostEventIdentity(
                    observation.IntrinsicEventSourceName,
                    inspectedEvent.Name),
                new Catalog.HostEventSignature(
                    inspectedEvent.Parameters.Select(CreateParameter).ToArray(),
                    inspectedEvent.Documentation),
                inspectedEvent.AuthoringAvailable,
                inspectedEvent.ExistingHandlerRecognizable)).ToArray(),
            observation.BaseTypeProvenance is null
                ? null
                : new Catalog.HostEventBaseTypeProvenance(
                    observation.BaseTypeProvenance.Name,
                    observation.BaseTypeProvenance.LibraryGuid,
                    observation.BaseTypeProvenance.MajorVersion,
                    observation.BaseTypeProvenance.MinorVersion,
                    observation.BaseTypeProvenance.Lcid));
    }

    private static Catalog.HostEventParameter CreateParameter(ObservedHostEventParameter parameter)
        => new(
            parameter.Name,
            parameter.Type switch
            {
                ObservedIntrinsicHostEventTypeReference intrinsic =>
                    new Catalog.IntrinsicHostEventTypeReference(intrinsic.Name),
                ObservedTypeLibHostEventTypeReference typeLib =>
                    new Catalog.TypeLibHostEventTypeReference(
                        typeLib.Name,
                        typeLib.LibraryGuid,
                        typeLib.MajorVersion,
                        typeLib.MinorVersion,
                        typeLib.Lcid),
                ObservedUnresolvedHostEventTypeReference unresolved =>
                    new Catalog.UnresolvedHostEventTypeReference(unresolved.DisplayName),
                _ => throw new InvalidOperationException(
                    $"Unsupported Host Event type reference '{parameter.Type.GetType().Name}'.")
            },
            parameter.Passing == ObservedHostEventPassingMechanism.ByVal
                ? Catalog.HostEventPassingMechanism.ByVal
                : Catalog.HostEventPassingMechanism.ByRef,
            parameter.ArrayShape == ObservedHostEventArrayShape.Scalar
                ? Catalog.HostEventArrayShape.Scalar
                : Catalog.HostEventArrayShape.Array,
            parameter.Optional,
            parameter.ParamArray);
}
