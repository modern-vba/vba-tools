using System.Runtime.InteropServices.ComTypes;

namespace VbaTools.Semantics;

internal static class TypeLibCatalogMemberFacts
{
    public static bool IsBrowsableForNameAuthoring(TypeLibCatalogMember member)
        => member.Metadata is null
            ? member.Kind != VbaSourceDefinitionKind.Event
            : IsBrowsableFunction(
                (FUNCFLAGS)member.Metadata.FunctionFlags);

    public static bool IsAuthoringAvailable(TypeLibCatalogMember member)
        => IsBrowsableForNameAuthoring(member)
            && (member.Metadata?.IsComplete ?? true);

    internal static bool IsBrowsableFunction(FUNCFLAGS functionFlags)
        => (functionFlags & (
            FUNCFLAGS.FUNCFLAG_FHIDDEN
            | FUNCFLAGS.FUNCFLAG_FRESTRICTED
            | FUNCFLAGS.FUNCFLAG_FNONBROWSABLE)) == 0;

}
