using VbaTools.Semantics;

namespace VbaDev.App.HostEvents;

/// <summary>Admits one observed host catalog for command output and semantic analysis.</summary>
internal static class IntrinsicHostEventCatalogAdmission
{
    internal static bool TryCanonicalize(
        IntrinsicHostEventCatalog catalog,
        out IntrinsicHostEventCatalog canonical,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!string.Equals(catalog.IntrinsicEventSourceName, "UserForm", StringComparison.Ordinal))
        {
            canonical = catalog;
            error = "The generated intrinsic Event source must be exactly 'UserForm'.";
            return false;
        }

        if (catalog.Events.Count == 0)
        {
            canonical = catalog;
            error = "The generated UserForm exposed no Events, so the environment catalog is not authoritative.";
            return false;
        }

        var duplicate = catalog.Events
            .GroupBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            canonical = catalog;
            error = $"Duplicate intrinsic Event identity '{duplicate.Key}' was observed.";
            return false;
        }

        foreach (var inspectedEvent in catalog.Events)
        {
            if (!string.Equals(
                    inspectedEvent.Identity.SourceName,
                    catalog.IntrinsicEventSourceName,
                    StringComparison.Ordinal))
            {
                canonical = catalog;
                error = $"Event '{inspectedEvent.Identity.Name}' does not belong to source 'UserForm'.";
                return false;
            }
        }

        canonical = catalog with
        {
            Events = catalog.Events
                .OrderBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(inspectedEvent => inspectedEvent.Identity.Name, StringComparer.Ordinal)
                .ToArray()
        };
        error = string.Empty;
        return true;
    }

    internal static VbaIntrinsicHostEventCatalog CaptureForAnalysis(IntrinsicHostEventCatalog catalog)
    {
        if (!TryCanonicalize(catalog, out var canonical, out var error))
        {
            throw new InvalidOperationException(error);
        }
        return new(VbaIntrinsicHostEventSourceKind.UserForm, canonical.IntrinsicEventSourceName,
            canonical.Events.Select(hostEvent => new VbaIntrinsicHostEvent(
                new(hostEvent.Identity.SourceName, hostEvent.Identity.Name),
                new(hostEvent.Signature.Parameters.Select(parameter => new VbaHostEventParameter(
                    parameter.Name, CaptureType(parameter.Type),
                    parameter.Passing == HostEventPassingMechanism.ByVal ? VbaHostEventParameterPassing.ByVal : VbaHostEventParameterPassing.ByRef,
                    parameter.ArrayShape == HostEventArrayShape.Scalar ? VbaHostEventParameterArrayShape.Scalar : VbaHostEventParameterArrayShape.Array,
                    parameter.Optional, parameter.ParamArray)).ToArray(), hostEvent.Signature.Documentation),
                hostEvent.AuthoringAvailable, hostEvent.ExistingHandlerRecognizable)).ToArray(),
            canonical.BaseTypeProvenance is { } provenance ? new(provenance.Name, provenance.LibraryGuid.ToString("D"),
                provenance.MajorVersion, provenance.MinorVersion, provenance.Lcid) : null);
    }

    private static VbaHostEventParameterType CaptureType(HostEventTypeReference type) => type switch
    {
        IntrinsicHostEventTypeReference intrinsic => new VbaIntrinsicHostEventParameterType(intrinsic.Name),
        TypeLibHostEventTypeReference typeLib => new VbaTypeLibraryHostEventParameterType(typeLib.Name,
            typeLib.LibraryGuid.ToString("D"), typeLib.MajorVersion, typeLib.MinorVersion, typeLib.Lcid),
        UnresolvedHostEventTypeReference unresolved => new VbaUnresolvedHostEventParameterType(unresolved.DisplayName),
        _ => throw new InvalidOperationException($"Unsupported host Event type reference: {type.GetType().Name}")
    };
}
