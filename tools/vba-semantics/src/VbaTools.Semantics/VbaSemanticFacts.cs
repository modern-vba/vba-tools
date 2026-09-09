using System.Collections.Immutable;

namespace VbaTools.Semantics;

internal static class VbaSemanticFacts
{
    internal static VbaSourceDefinition CaptureDefinition(VbaSourceDefinition definition)
        => definition.Signature is null
            ? definition
            : definition with
            {
                Signature = CaptureSignature(definition.Signature)
            };

    internal static VbaProjectReferenceCatalog CaptureCatalog(VbaProjectReferenceCatalog catalog)
        => catalog with
        {
            QualifierAliases = catalog.QualifierAliases.ToImmutableArray(),
            Definitions = catalog.Definitions.Select(definition => definition with
            {
                Signature = CaptureSignature(definition.Signature)
            }).ToImmutableArray(),
            TypeLibTypes = Freeze(catalog.TypeLibTypes, CaptureType)
        };

    private static VbaCallableSignature? CaptureSignature(VbaCallableSignature? signature)
        => signature is null ? null : signature with
        {
            Parameters = Freeze(signature.Parameters, parameter => parameter)!
        };

    internal static VbaIntrinsicHostEventCatalog? CaptureHostEvents(VbaIntrinsicHostEventCatalog? catalog)
        => catalog is null ? null : catalog with
        {
            Events = Freeze(catalog.Events, hostEvent => hostEvent is null ? null! : hostEvent with
            {
                Signature = hostEvent.Signature with
                {
                    Parameters = Freeze(hostEvent.Signature.Parameters, parameter => parameter)!
                }
            })!
        };

    private static TypeLibCatalogType CaptureType(TypeLibCatalogType type)
        => type is null ? null! : type with
        {
            Members = Freeze(type.Members, CaptureMember)!,
            Metadata = type.Metadata is null ? null : type.Metadata with
            {
                ImplementedInterfaces = Freeze(type.Metadata.ImplementedInterfaces, implemented =>
                    implemented is null ? null! : implemented with
                    {
                        CallableMembers = Freeze(implemented.CallableMembers, CaptureMember)!
                    })!
            }
        };

    private static TypeLibCatalogMember CaptureMember(TypeLibCatalogMember member)
        => member is null ? null! : member with { Signature = CaptureSignature(member.Signature) };

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items, Func<T, T> capture)
        => items?.Select(capture).ToImmutableArray();
}
