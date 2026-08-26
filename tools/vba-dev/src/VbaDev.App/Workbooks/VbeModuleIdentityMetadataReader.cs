using VbaLanguageServer.Syntax;

namespace VbaDev.App.Workbooks;

internal sealed record VbeModuleIdentityAuthority(string? Name, string? Failure)
{
    public bool IsAuthoritative => Name is not null && Failure is null;

    public static VbeModuleIdentityAuthority Authoritative(string name) => new(name, null);

    public static VbeModuleIdentityAuthority Invalid(string failure) => new(null, failure);
}

/// <summary>
/// Adapts the shared exported-source ModuleIdentity reader to vba-dev import authority.
/// </summary>
internal static class VbeModuleIdentityMetadataReader
{
    public static VbeModuleIdentityAuthority Read(string text, VbaSourceKind sourceKind)
    {
        var metadata = VbaModuleIdentityMetadataReader.Read(
            text,
            sourceKind == VbaSourceKind.StandardModule
                ? VbaModuleIdentitySourceKind.StandardModule
                : VbaModuleIdentitySourceKind.ObjectModule);
        return metadata.IsAuthoritative
            ? VbeModuleIdentityAuthority.Authoritative(metadata.Name!)
            : VbeModuleIdentityAuthority.Invalid(metadata.Failure!);
    }
}
