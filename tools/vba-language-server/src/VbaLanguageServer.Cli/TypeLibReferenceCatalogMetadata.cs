using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

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

/// <summary>
/// Reads TypeLib metadata from a resolved catalog identity.
/// </summary>
public interface ITypeLibCatalogMetadataReader
{
    /// <summary>
    /// Reads TypeLib metadata for a resolved catalog identity.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <returns>The TypeLib metadata.</returns>
    TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity);
}

/// <summary>
/// Builds a VBA project reference catalog from TypeLib metadata.
/// </summary>
public static class TypeLibReferenceCatalogBuilder
{
    /// <summary>
    /// Builds catalog definitions from TypeLib metadata.
    /// </summary>
    /// <param name="referenceName">The manifest reference name.</param>
    /// <param name="metadata">The TypeLib metadata.</param>
    /// <returns>The generated reference catalog.</returns>
    public static VbaProjectReferenceCatalog Build(string referenceName, TypeLibCatalogMetadata metadata)
    {
        var aliases = new[] { metadata.QualifierAlias, CreateQualifierAlias(referenceName) }
            .Where(alias => IsSingleLineForeignIdentifier(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var definitions = new List<VbaProjectReferenceDefinition>();

        foreach (var type in metadata.Types.Where(type => !string.IsNullOrEmpty(type.Name)))
        {
            var isExplicitlyResolvableCoClass =
                type.Metadata?.RawTypeKind == TypeLibCatalogRawTypeKind.CoClass;
            if (!type.IsBrowsable
                && !type.IsApplicationObject
                && !type.IsCreatable
                && !isExplicitlyResolvableCoClass)
            {
                continue;
            }

            if (type.IsBrowsable || type.IsCreatable || isExplicitlyResolvableCoClass)
            {
                definitions.Add(new VbaProjectReferenceDefinition(
                    referenceName,
                    type.Name,
                    type.Kind,
                    type.Documentation,
                    IsCreatable: type.IsCreatable,
                    IsAuthoringAvailable: type.IsBrowsable));
            }

            foreach (var member in type.Members.Where(member =>
                !string.IsNullOrEmpty(member.Name)
                && (TypeLibCatalogMemberFacts.IsBrowsableForNameAuthoring(member)
                    || member.Kind == VbaSourceDefinitionKind.Event)))
            {
                var isCallableMetadataComplete =
                    member.Metadata?.IsComplete ?? true;
                definitions.Add(new VbaProjectReferenceDefinition(
                    referenceName,
                    member.Name,
                    member.Kind,
                    member.Documentation,
                    member.Signature is null || !isCallableMetadataComplete
                        ? null
                        : member.Signature with { SupportsNamedArguments = true },
                    ParentTypeName: type.Name,
                    TypeReference: member.TypeReference,
                    PropertyAccess: member.PropertyAccess,
                    GlobalExposure: GetGlobalExposure(type),
                    IsAuthoringAvailable:
                        member.Kind == VbaSourceDefinitionKind.Event
                            ? TypeLibCatalogMemberFacts.IsAuthoringAvailable(member)
                            : TypeLibCatalogMemberFacts
                                .IsBrowsableForNameAuthoring(member),
                    IsCallableMetadataComplete:
                        isCallableMetadataComplete)
                {
                    PropertyAccessorKind = member.Kind
                        == VbaSourceDefinitionKind.Property
                            ? member.Metadata?.PropertyAccessorKind
                            : null,
                    IsReturnArray = member.Metadata?.IsReturnArray,
                    CallableKind = member.Signature?.CallableKind
                });
            }
        }

        return new VbaProjectReferenceCatalog(
            referenceName,
            aliases,
            DeduplicateDefinitions(definitions),
            metadata.Types
                .Where(type => type.Metadata is not null)
                .ToArray())
        {
            ReferencedVbaProjectName = string.IsNullOrEmpty(
                metadata.ReferencedVbaProjectName)
                    ? null
                    : metadata.ReferencedVbaProjectName
        };
    }

    private static ReferenceDefinitionGlobalExposure GetGlobalExposure(TypeLibCatalogType type)
        => type.IsApplicationObject
            ? ReferenceDefinitionGlobalExposure.MainHostGlobal
            : type.Kind is VbaSourceDefinitionKind.Module or VbaSourceDefinitionKind.Enum
                ? ReferenceDefinitionGlobalExposure.LibraryGlobal
                : ReferenceDefinitionGlobalExposure.None;

    private static bool IsSingleLineForeignIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
            && !value.Contains('\r')
            && !value.Contains('\n');

    private static IReadOnlyList<VbaProjectReferenceDefinition> DeduplicateDefinitions(
        IReadOnlyList<VbaProjectReferenceDefinition> definitions)
    {
        return definitions
            .GroupBy(
                definition => string.Join(
                    "\u001f",
                    definition.ReferenceName,
                    definition.Name,
                    definition.Kind.ToString(),
                    definition.ParentTypeName ?? "",
                    definition.PropertyAccessorKind?.ToString() ?? ""),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group
                    .OrderByDescending(definition => definition.TypeReference is not null)
                    .ThenByDescending(definition => definition.Signature is not null)
                    .First();
                return selected with
                {
                    PropertyAccess = selected.Kind == VbaSourceDefinitionKind.Property
                        ? group.Aggregate(
                            VbaPropertyAccess.Unknown,
                            (access, definition) => access | definition.PropertyAccess)
                        : VbaPropertyAccess.Unknown,
                    IsCreatable = group.Any(definition => definition.IsCreatable),
                    GlobalExposure = MergeGlobalExposure(group)
                };
            })
            .ToArray();
    }

    private static ReferenceDefinitionGlobalExposure MergeGlobalExposure(
        IEnumerable<VbaProjectReferenceDefinition> definitions)
    {
        var exposures = definitions
            .Select(definition => definition.GlobalExposure)
            .ToArray();
        if (exposures.Contains(ReferenceDefinitionGlobalExposure.LibraryGlobal))
        {
            return ReferenceDefinitionGlobalExposure.LibraryGlobal;
        }

        return exposures.Contains(ReferenceDefinitionGlobalExposure.MainHostGlobal)
            ? ReferenceDefinitionGlobalExposure.MainHostGlobal
            : ReferenceDefinitionGlobalExposure.None;
    }

    internal static string CreateQualifierAlias(string referenceName)
    {
        if (VbaIdentifier.IsIdentifier(referenceName))
        {
            return referenceName;
        }

        var alias = new StringBuilder(referenceName.Length);
        foreach (var rune in referenceName.EnumerateRunes())
        {
            var candidate = string.Concat(alias.ToString(), rune.ToString());
            if (VbaIdentifier.IsLexIdentifier(candidate))
            {
                alias.Append(rune.ToString());
            }
        }

        var value = alias.Length == 0 ? "Library" : alias.ToString();
        return VbaIdentifier.IsReservedIdentifier(value) ? $"Library_{value}" : value;
    }
}

internal static class TypeLibCatalogMemberFacts
{
    public static bool IsBrowsableForNameAuthoring(TypeLibCatalogMember member)
        => member.Metadata is null
            ? member.Kind != VbaSourceDefinitionKind.Event
            : ComTypeLibCatalogMetadataReader.IsBrowsableFunction(
                (FUNCFLAGS)member.Metadata.FunctionFlags);

    public static bool IsAuthoringAvailable(TypeLibCatalogMember member)
        => IsBrowsableForNameAuthoring(member)
            && (member.Metadata?.IsComplete ?? true);
}

/// <summary>
/// Reads TypeLib metadata through the Windows COM TypeLib APIs.
/// </summary>
public sealed class ComTypeLibCatalogMetadataReader : ITypeLibCatalogMetadataReader
{
    private const int TypeDocumentationMemberId = -1;
    private readonly Func<VbaProjectReferenceCatalogIdentity, ITypeLib>? typeLibLoader;

    /// <summary>
    /// Creates a reader backed by the Windows COM TypeLib loader.
    /// </summary>
    public ComTypeLibCatalogMetadataReader()
    {
    }

    internal ComTypeLibCatalogMetadataReader(
        Func<VbaProjectReferenceCatalogIdentity, ITypeLib> typeLibLoader)
    {
        this.typeLibLoader = typeLibLoader
            ?? throw new ArgumentNullException(nameof(typeLibLoader));
    }

    /// <summary>
    /// Reads TypeLib metadata for a resolved catalog identity.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <returns>The TypeLib metadata.</returns>
    public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
    {
        if (typeLibLoader is not null)
        {
            return ReadLoadedMetadata(identity, typeLibLoader(identity));
        }

        if (!OperatingSystem.IsWindows())
        {
            return new TypeLibCatalogMetadata(CreateFallbackQualifier(identity.ReferenceName), []);
        }

        return ReadWindowsMetadata(identity);
    }

    [SupportedOSPlatform("windows")]
    private static TypeLibCatalogMetadata ReadWindowsMetadata(VbaProjectReferenceCatalogIdentity identity)
    {
        var typeLib = LoadWindowsTypeLib(identity);
        return ReadLoadedMetadata(identity, typeLib);
    }

    private static TypeLibCatalogMetadata ReadLoadedMetadata(
        VbaProjectReferenceCatalogIdentity identity,
        ITypeLib typeLib)
    {
        typeLib.GetDocumentation(TypeDocumentationMemberId, out var libraryName, out _, out _, out _);

        var typeInfos = ReadTypeInfos(typeLib);
        var types = new List<TypeLibCatalogType>();
        foreach (var typeInfo in typeInfos)
        {
            var type = ReadType(typeInfo);
            if (type is not null)
            {
                types.Add(type);
            }
        }

        types.AddRange(ReadCoClassForwardedMembers(typeInfos));
        return new TypeLibCatalogMetadata(
            string.IsNullOrEmpty(libraryName) ? CreateFallbackQualifier(identity.ReferenceName) : libraryName,
            types,
            string.IsNullOrEmpty(libraryName) ? null : libraryName);
    }

    [SupportedOSPlatform("windows")]
    private static ITypeLib LoadWindowsTypeLib(VbaProjectReferenceCatalogIdentity identity)
    {
        LoadTypeLibEx(identity.Path, REGKIND.REGKIND_NONE, out var pathTypeLib);
        ValidateWindowsTypeLibIdentity(pathTypeLib, identity);
        return pathTypeLib;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsTypeLibIdentity(
        ITypeLib typeLib,
        VbaProjectReferenceCatalogIdentity identity)
    {
        var attrPointer = IntPtr.Zero;
        try
        {
            typeLib.GetLibAttr(out attrPointer);
            var attributes = Marshal.PtrToStructure<TYPELIBATTR>(attrPointer);
            var loadedMajorVersion = unchecked((ushort)attributes.wMajorVerNum);
            var loadedMinorVersion = unchecked((ushort)attributes.wMinorVerNum);
            if (!Guid.TryParse(identity.Guid, out var expectedGuid)
                || attributes.guid != expectedGuid
                || loadedMajorVersion != identity.MajorVersion
                || loadedMinorVersion != identity.MinorVersion)
            {
                throw new InvalidDataException(
                    $"The TypeLib at '{identity.Path}' has identity "
                    + $"{attributes.guid:D} {loadedMajorVersion}.{loadedMinorVersion}; "
                    + $"expected {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion}.");
            }
        }
        finally
        {
            if (attrPointer != IntPtr.Zero)
            {
                typeLib.ReleaseTLibAttr(attrPointer);
            }
        }
    }

    private static IReadOnlyList<ITypeInfo> ReadTypeInfos(ITypeLib typeLib)
    {
        var typeInfos = new List<ITypeInfo>();
        var count = typeLib.GetTypeInfoCount();
        for (var index = 0; index < count; index++)
        {
            typeLib.GetTypeInfo(index, out var typeInfo);
            typeInfos.Add(typeInfo);
        }

        return typeInfos;
    }

    private static TypeLibCatalogType? ReadType(ITypeInfo typeInfo, bool allowHiddenType = false)
    {
        var attrPointer = IntPtr.Zero;
        try
        {
            typeInfo.GetTypeAttr(out attrPointer);
            var attr = Marshal.PtrToStructure<TYPEATTR>(attrPointer);
            var typeFlags = (TYPEFLAGS)attr.wTypeFlags;
            var isApplicationObject = IsApplicationObjectType(typeFlags);
            var isBrowsable = IsBrowsableType(typeFlags);
            if (!allowHiddenType
                && !isBrowsable
                && !isApplicationObject
                && attr.typekind != TYPEKIND.TKIND_COCLASS)
            {
                return null;
            }

            typeInfo.GetDocumentation(TypeDocumentationMemberId, out var typeName, out var documentation, out _, out _);
            if (string.IsNullOrEmpty(typeName) || !TryMapTypeKind(attr.typekind, out var definitionKind))
            {
                return null;
            }

            var members = new List<TypeLibCatalogMember>();
            members.AddRange(ReadVariableMembers(typeInfo, attr, typeName, definitionKind));
            members.AddRange(ReadFunctionMembers(
                typeInfo,
                attr,
                typeName,
                out _));
            var implementedInterfaces = ReadImplementedInterfaces(
                typeInfo,
                attr,
                out var areImplementedInterfacesComplete);
            return new TypeLibCatalogType(
                typeName,
                definitionKind,
                EmptyToNull(documentation),
                members,
                IsCreatableTypeKind(attr.typekind),
                IsApplicationObject: isApplicationObject,
                IsBrowsable: isBrowsable,
                Metadata: new TypeLibCatalogTypeMetadata(
                    GetRawTypeKind(attr.typekind),
                    (int)attr.wTypeFlags,
                    implementedInterfaces,
                    IsComplete: areImplementedInterfacesComplete));
        }
        finally
        {
            if (attrPointer != IntPtr.Zero)
            {
                typeInfo.ReleaseTypeAttr(attrPointer);
            }
        }
    }

    private static IReadOnlyList<TypeLibCatalogImplementedInterface> ReadImplementedInterfaces(
        ITypeInfo typeInfo,
        TYPEATTR attr,
        out bool isComplete)
    {
        isComplete = true;
        if (attr.typekind != TYPEKIND.TKIND_COCLASS || attr.cImplTypes <= 0)
        {
            return [];
        }

        var implementedInterfaces = new List<TypeLibCatalogImplementedInterface>();
        for (var index = 0; index < attr.cImplTypes; index++)
        {
            typeInfo.GetImplTypeFlags(index, out var implementationFlags);
            typeInfo.GetRefTypeOfImplType(index, out var href);
            typeInfo.GetRefTypeInfo(href, out var implementedTypeInfo);

            var implementedAttrPointer = IntPtr.Zero;
            try
            {
                implementedTypeInfo.GetTypeAttr(out implementedAttrPointer);
                var implementedAttr = Marshal.PtrToStructure<TYPEATTR>(implementedAttrPointer);
                implementedTypeInfo.GetDocumentation(
                    TypeDocumentationMemberId,
                    out var implementedTypeName,
                    out _,
                    out _,
                    out _);
                if (string.IsNullOrEmpty(implementedTypeName))
                {
                    isComplete = false;
                    continue;
                }

                var callableMembers = ReadFunctionMembers(
                    implementedTypeInfo,
                    implementedAttr,
                    implementedTypeName,
                    out var isCallableSurfaceComplete);
                implementedInterfaces.Add(new TypeLibCatalogImplementedInterface(
                    implementedTypeName,
                    (int)implementedAttr.wTypeFlags,
                    (int)implementationFlags,
                    callableMembers,
                    RawTypeKind: GetRawTypeKind(implementedAttr.typekind),
                    IsComplete: isCallableSurfaceComplete));
            }
            finally
            {
                if (implementedAttrPointer != IntPtr.Zero)
                {
                    implementedTypeInfo.ReleaseTypeAttr(implementedAttrPointer);
                }
            }
        }

        return implementedInterfaces;
    }

    private static IReadOnlyList<TypeLibCatalogType> ReadCoClassForwardedMembers(IReadOnlyList<ITypeInfo> typeInfos)
    {
        var forwardedTypes = new List<TypeLibCatalogType>();
        foreach (var coClassInfo in typeInfos)
        {
            var attrPointer = IntPtr.Zero;
            try
            {
                coClassInfo.GetTypeAttr(out attrPointer);
                var attr = Marshal.PtrToStructure<TYPEATTR>(attrPointer);
                var typeFlags = (TYPEFLAGS)attr.wTypeFlags;
                var isApplicationObject = IsApplicationObjectType(typeFlags);
                var isBrowsable = IsBrowsableType(typeFlags);
                if (attr.typekind != TYPEKIND.TKIND_COCLASS)
                {
                    continue;
                }

                coClassInfo.GetDocumentation(TypeDocumentationMemberId, out var coClassName, out _, out _, out _);
                if (string.IsNullOrEmpty(coClassName))
                {
                    continue;
                }

                var members = new List<TypeLibCatalogMember>();
                var implementationFlags = new IMPLTYPEFLAGS[attr.cImplTypes];
                for (var index = 0; index < attr.cImplTypes; index++)
                {
                    coClassInfo.GetImplTypeFlags(index, out implementationFlags[index]);
                }

                var defaultSourceCount = implementationFlags.Count(flags =>
                    (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0
                    && (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) != 0);
                for (var index = 0; index < attr.cImplTypes; index++)
                {
                    var implFlags = implementationFlags[index];
                    var isSource = (implFlags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0;
                    var isDefaultSource = isSource
                        && (implFlags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) != 0;
                    if (isSource && (!isDefaultSource || defaultSourceCount != 1))
                    {
                        continue;
                    }

                    coClassInfo.GetRefTypeOfImplType(index, out var href);
                    coClassInfo.GetRefTypeInfo(href, out var implementedInfo);
                    var implementedType = ReadType(implementedInfo, allowHiddenType: true);
                    if (implementedType is null)
                    {
                        continue;
                    }

                    if (isDefaultSource
                        && implementedType.Metadata?.RawTypeKind is not (
                            TypeLibCatalogRawTypeKind.Interface
                            or TypeLibCatalogRawTypeKind.Dispatch))
                    {
                        continue;
                    }

                    members.AddRange(implementedType.Members.Select(member => isDefaultSource
                        ? member with
                        {
                            Kind = VbaSourceDefinitionKind.Event,
                            Signature = member.Signature is null
                                ? null
                                : member.Signature with { CallableKind = VbaCallableKind.Event },
                            PropertyAccess = VbaPropertyAccess.Unknown
                        }
                        : member));
                }

                if (members.Count > 0)
                {
                    forwardedTypes.Add(new TypeLibCatalogType(
                        coClassName,
                        VbaSourceDefinitionKind.Class,
                        null,
                        members,
                        IsCreatable: true,
                        IsApplicationObject: isApplicationObject,
                        IsBrowsable: isBrowsable));
                }
            }
            finally
            {
                if (attrPointer != IntPtr.Zero)
                {
                    coClassInfo.ReleaseTypeAttr(attrPointer);
                }
            }
        }

        return forwardedTypes;
    }

    private static IReadOnlyList<TypeLibCatalogMember> ReadVariableMembers(
        ITypeInfo typeInfo,
        TYPEATTR attr,
        string typeName,
        VbaSourceDefinitionKind typeKind)
    {
        var members = new List<TypeLibCatalogMember>();
        for (var index = 0; index < attr.cVars; index++)
        {
            var varPointer = IntPtr.Zero;
            try
            {
                typeInfo.GetVarDesc(index, out varPointer);
                var varDesc = Marshal.PtrToStructure<VARDESC>(varPointer);
                if (HasHiddenOrRestrictedVarFlags(varDesc))
                {
                    continue;
                }

                typeInfo.GetDocumentation(varDesc.memid, out var memberName, out var documentation, out _, out _);
                if (string.IsNullOrEmpty(memberName))
                {
                    continue;
                }

                var memberKind = typeKind switch
                {
                    VbaSourceDefinitionKind.Enum => VbaSourceDefinitionKind.EnumMember,
                    VbaSourceDefinitionKind.Type => VbaSourceDefinitionKind.TypeMember,
                    _ => VbaSourceDefinitionKind.Property
                };
                members.Add(new TypeLibCatalogMember(
                    memberName,
                    memberKind,
                    EmptyToNull(documentation),
                    TypeReference: ToTypeReference(typeInfo, varDesc.elemdescVar.tdesc),
                    PropertyAccess: GetVariablePropertyAccess(memberKind, varDesc)));
            }
            finally
            {
                if (varPointer != IntPtr.Zero)
                {
                    typeInfo.ReleaseVarDesc(varPointer);
                }
            }
        }

        return members;
    }

    private static IReadOnlyList<TypeLibCatalogMember> ReadFunctionMembers(
        ITypeInfo typeInfo,
        TYPEATTR attr,
        string typeName,
        out bool isComplete)
    {
        isComplete = true;
        var members = new List<TypeLibCatalogMember>();
        for (var index = 0; index < attr.cFuncs; index++)
        {
            var funcPointer = IntPtr.Zero;
            try
            {
                typeInfo.GetFuncDesc(index, out funcPointer);
                var funcDesc = Marshal.PtrToStructure<FUNCDESC>(funcPointer);
                var names = GetNames(typeInfo, funcDesc.memid, funcDesc.cParams + 1);
                var memberName = names.FirstOrDefault();
                if (string.IsNullOrEmpty(memberName))
                {
                    isComplete = false;
                    continue;
                }

                typeInfo.GetDocumentation(funcDesc.memid, out _, out var documentation, out _, out _);
                var parameters = ReadParameters(
                    typeInfo,
                    funcDesc,
                    names.Skip(1).ToArray(),
                    out var returnType,
                    out var isReturnArray,
                    out var hasReturnValueParameter,
                    out var areParametersComplete);
                if (!areParametersComplete)
                {
                    isComplete = false;
                }

                returnType ??= ToTypeReference(typeInfo, funcDesc.elemdescFunc.tdesc);
                var memberKind = IsPropertyInvokeKind(funcDesc.invkind)
                    ? VbaSourceDefinitionKind.Property
                    : VbaSourceDefinitionKind.Procedure;
                var propertyAccess = GetPropertyAccess(funcDesc.invkind);
                var callableKind = GetCallableKind(
                    funcDesc.invkind,
                    (VarEnum)funcDesc.elemdescFunc.tdesc.vt,
                    hasResolvedReturnType: returnType is not null,
                    hasReturnValueParameter);
                var signature = memberKind == VbaSourceDefinitionKind.Procedure || parameters.Count > 0
                    ? CreateSignature(memberName, parameters, returnType, EmptyToNull(documentation), callableKind)
                    : null;

                members.Add(new TypeLibCatalogMember(
                    memberName,
                    memberKind,
                    EmptyToNull(documentation),
                    signature,
                    returnType,
                    propertyAccess,
                    new TypeLibCatalogCallableMetadata(
                        funcDesc.memid,
                        funcDesc.wFuncFlags,
                        IsComplete: areParametersComplete)
                    {
                        PropertyAccessorKind = GetPropertyAccessorKind(
                            funcDesc.invkind),
                        IsReturnArray = callableKind is VbaCallableKind.Function
                                || (callableKind == VbaCallableKind.Property
                                    && GetPropertyAccessorKind(funcDesc.invkind)
                                        == VbaPropertyAccessorKind.Get)
                            ? isReturnArray
                            : null
                    }));
            }
            finally
            {
                if (funcPointer != IntPtr.Zero)
                {
                    typeInfo.ReleaseFuncDesc(funcPointer);
                }
            }
        }

        return members;
    }

    private static IReadOnlyList<VbaCallableParameter> ReadParameters(
        ITypeInfo typeInfo,
        FUNCDESC funcDesc,
        IReadOnlyList<string> names,
        out VbaTypeReference? returnType,
        out bool? isReturnArray,
        out bool hasReturnValueParameter,
        out bool isComplete)
    {
        returnType = null;
        isReturnArray = GetArrayTypeEvidence(funcDesc.elemdescFunc.tdesc);
        hasReturnValueParameter = false;
        isComplete = funcDesc.cParams <= 0
            || funcDesc.lprgelemdescParam != IntPtr.Zero;
        if (funcDesc.cParams <= 0
            || funcDesc.lprgelemdescParam == IntPtr.Zero)
        {
            return [];
        }

        var parameters = new List<VbaCallableParameter>();
        var elementSize = Marshal.SizeOf<ELEMDESC>();
        for (var index = 0; index < funcDesc.cParams; index++)
        {
            var elementPointer = IntPtr.Add(funcDesc.lprgelemdescParam, index * elementSize);
            var element = Marshal.PtrToStructure<ELEMDESC>(elementPointer);
            if ((element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FRETVAL) != 0)
            {
                hasReturnValueParameter = true;
                returnType = ToTypeReference(typeInfo, element.tdesc);
                isReturnArray = GetArrayTypeEvidence(element.tdesc);
                continue;
            }

            if ((element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FLCID) != 0)
            {
                continue;
            }

            var parameterName = index < names.Count && !string.IsNullOrEmpty(names[index])
                ? names[index]
                : $"Arg{parameters.Count + 1}";
            var isOptional = (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FOPT) != 0
                || (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FHASDEFAULT) != 0;
            var isParamArray = funcDesc.cParamsOpt == -1 && index == funcDesc.cParams - 1;
            var isArray = GetArrayTypeEvidence(element.tdesc);
            if (!isParamArray && isArray is null)
            {
                isComplete = false;
            }

            parameters.Add(new VbaCallableParameter(
                parameterName,
                IsOptional: isOptional,
                TypeReference: ToTypeReference(typeInfo, element.tdesc),
                IsByRef: GetParameterPassing(element),
                IsParamArray: isParamArray,
                IsArray: isParamArray || isArray == true));
        }

        return parameters;
    }

    private static string[] GetNames(ITypeInfo typeInfo, int memberId, int maxNames)
    {
        var names = new string[Math.Max(1, maxNames)];
        typeInfo.GetNames(memberId, names, names.Length, out var count);
        return names.Take(count).ToArray();
    }

    private static VbaCallableSignature CreateSignature(
        string memberName,
        IReadOnlyList<VbaCallableParameter> parameters,
        VbaTypeReference? returnType,
        string? documentation,
        VbaCallableKind callableKind)
    {
        var label = $"{memberName}({string.Join(", ", parameters.Select(CreateParameterLabel))})";
        if (returnType is not null)
        {
            label = $"{label} As {returnType.Name}";
        }

        return new VbaCallableSignature(
            label,
            parameters,
            documentation,
            CallableKind: callableKind,
            SupportsNamedArguments: true);
    }

    internal static VbaCallableKind GetCallableKind(
        INVOKEKIND invokeKind,
        VarEnum returnVarType,
        bool hasResolvedReturnType,
        bool hasReturnValueParameter)
    {
        if (IsPropertyInvokeKind(invokeKind))
        {
            return VbaCallableKind.Property;
        }

        if (hasResolvedReturnType || hasReturnValueParameter)
        {
            return VbaCallableKind.Function;
        }

        return returnVarType is VarEnum.VT_VOID or VarEnum.VT_EMPTY or VarEnum.VT_HRESULT
            ? VbaCallableKind.Sub
            : VbaCallableKind.Function;
    }

    internal static VbaPropertyAccess GetPropertyAccess(INVOKEKIND invokeKind)
    {
        var access = VbaPropertyAccess.Unknown;
        if ((invokeKind & INVOKEKIND.INVOKE_PROPERTYGET) != 0)
        {
            access |= VbaPropertyAccess.Readable;
        }

        if ((invokeKind & (INVOKEKIND.INVOKE_PROPERTYPUT | INVOKEKIND.INVOKE_PROPERTYPUTREF)) != 0)
        {
            access |= VbaPropertyAccess.Writable;
        }

        return access;
    }

    internal static VbaPropertyAccessorKind? GetPropertyAccessorKind(
        INVOKEKIND invokeKind)
        => invokeKind switch
        {
            INVOKEKIND.INVOKE_PROPERTYGET => VbaPropertyAccessorKind.Get,
            INVOKEKIND.INVOKE_PROPERTYPUT => VbaPropertyAccessorKind.Let,
            INVOKEKIND.INVOKE_PROPERTYPUTREF => VbaPropertyAccessorKind.Set,
            _ => null
        };

    internal static bool IsCreatableTypeKind(TYPEKIND typeKind)
        => typeKind == TYPEKIND.TKIND_COCLASS;

    internal static bool IsApplicationObjectType(TYPEFLAGS typeFlags)
        => (typeFlags & TYPEFLAGS.TYPEFLAG_FAPPOBJECT) != 0;

    internal static bool IsBrowsableType(TYPEFLAGS typeFlags)
        => (typeFlags & (TYPEFLAGS.TYPEFLAG_FHIDDEN | TYPEFLAGS.TYPEFLAG_FRESTRICTED)) == 0;

    internal static bool IsBrowsableFunction(FUNCFLAGS functionFlags)
        => (functionFlags & (
            FUNCFLAGS.FUNCFLAG_FHIDDEN
            | FUNCFLAGS.FUNCFLAG_FRESTRICTED
            | FUNCFLAGS.FUNCFLAG_FNONBROWSABLE)) == 0;

    internal static bool IsBrowsableVariable(VARFLAGS variableFlags)
        => (variableFlags & (
            VARFLAGS.VARFLAG_FHIDDEN
            | VARFLAGS.VARFLAG_FRESTRICTED
            | VARFLAGS.VARFLAG_FNONBROWSABLE)) == 0;

    private static string CreateParameterLabel(VbaCallableParameter parameter)
        => parameter.IsOptional ? $"[{parameter.Name}]" : parameter.Name;

    private static bool? GetParameterPassing(ELEMDESC element)
    {
        if ((VarEnum)element.tdesc.vt == VarEnum.VT_PTR)
        {
            return true;
        }

        var flags = element.desc.paramdesc.wParamFlags;
        if ((flags & PARAMFLAG.PARAMFLAG_FOUT) != 0)
        {
            return true;
        }

        if ((flags & PARAMFLAG.PARAMFLAG_FIN) != 0)
        {
            return false;
        }

        return null;
    }

    private static bool? GetArrayTypeEvidence(TYPEDESC typeDesc)
    {
        var varType = (VarEnum)typeDesc.vt;
        if (varType is VarEnum.VT_SAFEARRAY or VarEnum.VT_CARRAY)
        {
            return true;
        }

        if (varType != VarEnum.VT_PTR)
        {
            return false;
        }

        return TryGetNestedTypeDescription(typeDesc, out var nestedType)
            ? GetArrayTypeEvidence(nestedType)
            : null;
    }

    private static VbaTypeReference? ToTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        var varType = (VarEnum)typeDesc.vt;
        return varType switch
        {
            VarEnum.VT_VOID => null,
            VarEnum.VT_EMPTY => null,
            VarEnum.VT_HRESULT => null,
            VarEnum.VT_BSTR => new VbaTypeReference("String"),
            VarEnum.VT_BOOL => new VbaTypeReference("Boolean"),
            VarEnum.VT_I1 => new VbaTypeReference("Byte"),
            VarEnum.VT_UI1 => new VbaTypeReference("Byte"),
            VarEnum.VT_I2 => new VbaTypeReference("Integer"),
            VarEnum.VT_UI2 => new VbaTypeReference("Integer"),
            VarEnum.VT_I4 => new VbaTypeReference("Long"),
            VarEnum.VT_INT => new VbaTypeReference("Long"),
            VarEnum.VT_UI4 => new VbaTypeReference("Long"),
            VarEnum.VT_UINT => new VbaTypeReference("Long"),
            VarEnum.VT_I8 => new VbaTypeReference("LongLong"),
            VarEnum.VT_UI8 => new VbaTypeReference("LongLong"),
            VarEnum.VT_R4 => new VbaTypeReference("Single"),
            VarEnum.VT_R8 => new VbaTypeReference("Double"),
            VarEnum.VT_CY => new VbaTypeReference("Currency"),
            VarEnum.VT_DATE => new VbaTypeReference("Date"),
            VarEnum.VT_VARIANT => new VbaTypeReference("Variant"),
            VarEnum.VT_DISPATCH => new VbaTypeReference("Object"),
            VarEnum.VT_UNKNOWN => new VbaTypeReference("Object"),
            VarEnum.VT_PTR => ToNestedTypeReference(typeInfo, typeDesc),
            VarEnum.VT_SAFEARRAY => ToNestedTypeReference(typeInfo, typeDesc),
            VarEnum.VT_CARRAY => ToNestedTypeReference(typeInfo, typeDesc),
            VarEnum.VT_USERDEFINED => ToUserDefinedTypeReference(typeInfo, typeDesc),
            _ => null
        };
    }

    private static VbaTypeReference? ToNestedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        if (!TryGetNestedTypeDescription(typeDesc, out var nested))
        {
            return null;
        }

        return ToTypeReference(typeInfo, nested);
    }

    private static bool TryGetNestedTypeDescription(TYPEDESC typeDesc, out TYPEDESC nested)
    {
        nested = default;
        if (typeDesc.lpValue == IntPtr.Zero)
        {
            return false;
        }

        nested = Marshal.PtrToStructure<TYPEDESC>(typeDesc.lpValue);
        return true;
    }

    private static VbaTypeReference? ToUserDefinedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        try
        {
            var hrefType = unchecked((int)typeDesc.lpValue.ToInt64());
            typeInfo.GetRefTypeInfo(hrefType, out var referencedTypeInfo);
            referencedTypeInfo.GetDocumentation(TypeDocumentationMemberId, out var name, out _, out _, out _);
            return string.IsNullOrEmpty(name) ? null : new VbaTypeReference(name);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool TryMapTypeKind(TYPEKIND typeKind, out VbaSourceDefinitionKind definitionKind)
    {
        var mappedKind = GetTypeDefinitionKind(typeKind);
        definitionKind = mappedKind ?? VbaSourceDefinitionKind.Variable;
        return mappedKind is not null;
    }

    internal static VbaSourceDefinitionKind? GetTypeDefinitionKind(TYPEKIND typeKind)
        => typeKind switch
        {
            TYPEKIND.TKIND_ENUM => VbaSourceDefinitionKind.Enum,
            TYPEKIND.TKIND_RECORD => VbaSourceDefinitionKind.Type,
            TYPEKIND.TKIND_UNION => VbaSourceDefinitionKind.Type,
            TYPEKIND.TKIND_MODULE => VbaSourceDefinitionKind.Module,
            TYPEKIND.TKIND_DISPATCH => VbaSourceDefinitionKind.Class,
            TYPEKIND.TKIND_INTERFACE => VbaSourceDefinitionKind.Class,
            TYPEKIND.TKIND_COCLASS => VbaSourceDefinitionKind.Class,
            _ => null
        };

    private static TypeLibCatalogRawTypeKind GetRawTypeKind(TYPEKIND typeKind)
        => typeKind switch
        {
            TYPEKIND.TKIND_COCLASS => TypeLibCatalogRawTypeKind.CoClass,
            TYPEKIND.TKIND_INTERFACE => TypeLibCatalogRawTypeKind.Interface,
            TYPEKIND.TKIND_DISPATCH => TypeLibCatalogRawTypeKind.Dispatch,
            _ => TypeLibCatalogRawTypeKind.Other
        };

    private static VbaPropertyAccess GetVariablePropertyAccess(
        VbaSourceDefinitionKind memberKind,
        VARDESC varDesc)
    {
        if (memberKind != VbaSourceDefinitionKind.Property)
        {
            return VbaPropertyAccess.Unknown;
        }

        return (varDesc.wVarFlags & (short)VARFLAGS.VARFLAG_FREADONLY) != 0
            ? VbaPropertyAccess.Readable
            : VbaPropertyAccess.Readable | VbaPropertyAccess.Writable;
    }

    private static bool IsPropertyInvokeKind(INVOKEKIND invokeKind)
        => GetPropertyAccess(invokeKind) != VbaPropertyAccess.Unknown;

    private static bool HasHiddenOrRestrictedFuncFlags(FUNCDESC funcDesc)
        => !IsBrowsableFunction((FUNCFLAGS)funcDesc.wFuncFlags);

    private static bool HasHiddenOrRestrictedVarFlags(VARDESC varDesc)
        => !IsBrowsableVariable((VARFLAGS)varDesc.wVarFlags);

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string CreateFallbackQualifier(string referenceName)
        => TypeLibReferenceCatalogBuilder.CreateQualifierAlias(referenceName);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(
        string szFile,
        REGKIND regkind,
        [MarshalAs(UnmanagedType.Interface)] out ITypeLib pptlib);

    private enum REGKIND
    {
        REGKIND_DEFAULT = 0,
        REGKIND_REGISTER = 1,
        REGKIND_NONE = 2
    }
}
