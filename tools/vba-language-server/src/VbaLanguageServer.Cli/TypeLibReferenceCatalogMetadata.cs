using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Represents TypeLib metadata in a COM-independent shape used to build reference catalogs.
/// </summary>
/// <param name="QualifierAlias">The preferred VBA qualifier alias for the library.</param>
/// <param name="Types">The public types exposed by the library.</param>
public sealed record TypeLibCatalogMetadata(
    string QualifierAlias,
    IReadOnlyList<TypeLibCatalogType> Types);

/// <summary>
/// Represents one TypeLib type and its members.
/// </summary>
/// <param name="Name">The type name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Documentation">The type documentation.</param>
/// <param name="Members">The members exposed by the type.</param>
public sealed record TypeLibCatalogType(
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation,
    IReadOnlyList<TypeLibCatalogMember> Members);

/// <summary>
/// Represents one TypeLib member.
/// </summary>
/// <param name="Name">The member name.</param>
/// <param name="Kind">The editor-facing definition kind.</param>
/// <param name="Documentation">The member documentation.</param>
/// <param name="Signature">The callable signature, when the member is callable.</param>
/// <param name="TypeReference">The member result type, when known.</param>
public sealed record TypeLibCatalogMember(
    string Name,
    VbaSourceDefinitionKind Kind,
    string? Documentation,
    VbaCallableSignature? Signature = null,
    VbaTypeReference? TypeReference = null);

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
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var definitions = new List<VbaProjectReferenceDefinition>();

        foreach (var type in metadata.Types.Where(type => !string.IsNullOrWhiteSpace(type.Name)))
        {
            definitions.Add(new VbaProjectReferenceDefinition(
                referenceName,
                type.Name,
                type.Kind,
                type.Documentation));

            foreach (var member in type.Members.Where(member => !string.IsNullOrWhiteSpace(member.Name)))
            {
                definitions.Add(new VbaProjectReferenceDefinition(
                    referenceName,
                    member.Name,
                    member.Kind,
                    member.Documentation,
                    member.Signature,
                    ParentTypeName: type.Name,
                    TypeReference: member.TypeReference));
            }
        }

        return new VbaProjectReferenceCatalog(referenceName, aliases, DeduplicateDefinitions(definitions));
    }

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
                    definition.ParentTypeName ?? ""),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(definition => definition.TypeReference is not null)
                .ThenByDescending(definition => definition.Signature is not null)
                .First())
            .ToArray();
    }

    private static string CreateQualifierAlias(string referenceName)
    {
        var chars = referenceName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .ToArray();
        return chars.Length == 0 ? referenceName : new string(chars);
    }
}

/// <summary>
/// Reads TypeLib metadata through the Windows COM TypeLib APIs.
/// </summary>
public sealed class ComTypeLibCatalogMetadataReader : ITypeLibCatalogMetadataReader
{
    private const int TypeDocumentationMemberId = -1;

    /// <summary>
    /// Reads TypeLib metadata for a resolved catalog identity.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <returns>The TypeLib metadata.</returns>
    public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
    {
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
            string.IsNullOrWhiteSpace(libraryName) ? CreateFallbackQualifier(identity.ReferenceName) : libraryName,
            types);
    }

    [SupportedOSPlatform("windows")]
    private static ITypeLib LoadWindowsTypeLib(VbaProjectReferenceCatalogIdentity identity)
    {
        if (Guid.TryParse(identity.Guid, out var guid))
        {
            try
            {
                LoadRegTypeLib(ref guid, (ushort)identity.MajorVersion, (ushort)identity.MinorVersion, identity.Lcid, out var registeredTypeLib);
                return registeredTypeLib;
            }
            catch (COMException)
            {
                // Some registry entries point at resource-indexed files that are still loadable by path.
            }
        }

        LoadTypeLibEx(identity.Path, REGKIND.REGKIND_NONE, out var pathTypeLib);
        return pathTypeLib;
    }

    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
    private static TypeLibCatalogType? ReadType(ITypeInfo typeInfo, bool allowHiddenType = false)
    {
        var attrPointer = IntPtr.Zero;
        try
        {
            typeInfo.GetTypeAttr(out attrPointer);
            var attr = Marshal.PtrToStructure<TYPEATTR>(attrPointer);
            if (!allowHiddenType && HasHiddenOrRestrictedTypeFlags(attr))
            {
                return null;
            }

            typeInfo.GetDocumentation(TypeDocumentationMemberId, out var typeName, out var documentation, out _, out _);
            if (string.IsNullOrWhiteSpace(typeName) || !TryMapTypeKind(attr.typekind, out var definitionKind))
            {
                return null;
            }

            var members = new List<TypeLibCatalogMember>();
            members.AddRange(ReadVariableMembers(typeInfo, attr, typeName, definitionKind));
            members.AddRange(ReadFunctionMembers(typeInfo, attr, typeName));
            return new TypeLibCatalogType(typeName, definitionKind, EmptyToNull(documentation), members);
        }
        finally
        {
            if (attrPointer != IntPtr.Zero)
            {
                typeInfo.ReleaseTypeAttr(attrPointer);
            }
        }
    }

    [SupportedOSPlatform("windows")]
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
                if (attr.typekind != TYPEKIND.TKIND_COCLASS || HasHiddenOrRestrictedTypeFlags(attr))
                {
                    continue;
                }

                coClassInfo.GetDocumentation(TypeDocumentationMemberId, out var coClassName, out _, out _, out _);
                if (string.IsNullOrWhiteSpace(coClassName))
                {
                    continue;
                }

                var members = new List<TypeLibCatalogMember>();
                for (var index = 0; index < attr.cImplTypes; index++)
                {
                    coClassInfo.GetImplTypeFlags(index, out var implFlags);
                    coClassInfo.GetRefTypeOfImplType(index, out var href);
                    coClassInfo.GetRefTypeInfo(href, out var implementedInfo);
                    var implementedType = ReadType(implementedInfo, allowHiddenType: true);
                    if (implementedType is null)
                    {
                        continue;
                    }

                    var forceEvent = (implFlags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0;
                    members.AddRange(implementedType.Members.Select(member => forceEvent
                        ? member with { Kind = VbaSourceDefinitionKind.Event, Signature = member.Signature }
                        : member));
                }

                if (members.Count > 0)
                {
                    forwardedTypes.Add(new TypeLibCatalogType(coClassName, VbaSourceDefinitionKind.Class, null, members));
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

    [SupportedOSPlatform("windows")]
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
                if (string.IsNullOrWhiteSpace(memberName))
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
                    TypeReference: ToTypeReference(typeInfo, varDesc.elemdescVar.tdesc)));
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<TypeLibCatalogMember> ReadFunctionMembers(
        ITypeInfo typeInfo,
        TYPEATTR attr,
        string typeName)
    {
        var members = new List<TypeLibCatalogMember>();
        for (var index = 0; index < attr.cFuncs; index++)
        {
            var funcPointer = IntPtr.Zero;
            try
            {
                typeInfo.GetFuncDesc(index, out funcPointer);
                var funcDesc = Marshal.PtrToStructure<FUNCDESC>(funcPointer);
                if (HasHiddenOrRestrictedFuncFlags(funcDesc))
                {
                    continue;
                }

                var names = GetNames(typeInfo, funcDesc.memid, funcDesc.cParams + 1);
                var memberName = names.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    continue;
                }

                typeInfo.GetDocumentation(funcDesc.memid, out _, out var documentation, out _, out _);
                var parameters = ReadParameters(typeInfo, funcDesc, names.Skip(1).ToArray(), out var returnType);
                returnType ??= ToTypeReference(typeInfo, funcDesc.elemdescFunc.tdesc);
                var memberKind = IsPropertyInvokeKind(funcDesc.invkind)
                    ? VbaSourceDefinitionKind.Property
                    : VbaSourceDefinitionKind.Procedure;
                var signature = memberKind == VbaSourceDefinitionKind.Procedure || parameters.Count > 0
                    ? CreateSignature(memberName, parameters, returnType, EmptyToNull(documentation))
                    : null;

                members.Add(new TypeLibCatalogMember(
                    memberName,
                    memberKind,
                    EmptyToNull(documentation),
                    signature,
                    returnType));
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VbaCallableParameter> ReadParameters(
        ITypeInfo typeInfo,
        FUNCDESC funcDesc,
        IReadOnlyList<string> names,
        out VbaTypeReference? returnType)
    {
        returnType = null;
        if (funcDesc.cParams <= 0 || funcDesc.lprgelemdescParam == IntPtr.Zero)
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
                returnType = ToTypeReference(typeInfo, element.tdesc);
                continue;
            }

            if ((element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FLCID) != 0)
            {
                continue;
            }

            var parameterName = index < names.Count && !string.IsNullOrWhiteSpace(names[index])
                ? names[index]
                : $"Arg{parameters.Count + 1}";
            var isOptional = (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FOPT) != 0
                || (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FHASDEFAULT) != 0;
            parameters.Add(new VbaCallableParameter(parameterName, IsOptional: isOptional));
        }

        return parameters;
    }

    [SupportedOSPlatform("windows")]
    private static string[] GetNames(ITypeInfo typeInfo, int memberId, int maxNames)
    {
        var names = new string[Math.Max(1, maxNames)];
        typeInfo.GetNames(memberId, names, names.Length, out var count);
        return names.Take(count).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    private static VbaCallableSignature CreateSignature(
        string memberName,
        IReadOnlyList<VbaCallableParameter> parameters,
        VbaTypeReference? returnType,
        string? documentation)
    {
        var label = $"{memberName}({string.Join(", ", parameters.Select(CreateParameterLabel))})";
        if (returnType is not null)
        {
            label = $"{label} As {returnType.Name}";
        }

        return new VbaCallableSignature(label, parameters, documentation);
    }

    private static string CreateParameterLabel(VbaCallableParameter parameter)
        => parameter.IsOptional ? $"[{parameter.Name}]" : parameter.Name;

    [SupportedOSPlatform("windows")]
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
            _ => new VbaTypeReference("Variant")
        };
    }

    [SupportedOSPlatform("windows")]
    private static VbaTypeReference? ToNestedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        if (typeDesc.lpValue == IntPtr.Zero)
        {
            return null;
        }

        var nested = Marshal.PtrToStructure<TYPEDESC>(typeDesc.lpValue);
        return ToTypeReference(typeInfo, nested);
    }

    [SupportedOSPlatform("windows")]
    private static VbaTypeReference? ToUserDefinedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        try
        {
            var hrefType = unchecked((int)typeDesc.lpValue.ToInt64());
            typeInfo.GetRefTypeInfo(hrefType, out var referencedTypeInfo);
            referencedTypeInfo.GetDocumentation(TypeDocumentationMemberId, out var name, out _, out _, out _);
            return string.IsNullOrWhiteSpace(name) ? null : new VbaTypeReference(name);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool TryMapTypeKind(TYPEKIND typeKind, out VbaSourceDefinitionKind definitionKind)
    {
        definitionKind = typeKind switch
        {
            TYPEKIND.TKIND_ENUM => VbaSourceDefinitionKind.Enum,
            TYPEKIND.TKIND_RECORD => VbaSourceDefinitionKind.Type,
            TYPEKIND.TKIND_UNION => VbaSourceDefinitionKind.Type,
            TYPEKIND.TKIND_DISPATCH => VbaSourceDefinitionKind.Class,
            TYPEKIND.TKIND_INTERFACE => VbaSourceDefinitionKind.Class,
            TYPEKIND.TKIND_COCLASS => VbaSourceDefinitionKind.Class,
            _ => VbaSourceDefinitionKind.Variable
        };
        return definitionKind != VbaSourceDefinitionKind.Variable;
    }

    private static bool IsPropertyInvokeKind(INVOKEKIND invokeKind)
        => invokeKind is INVOKEKIND.INVOKE_PROPERTYGET
            or INVOKEKIND.INVOKE_PROPERTYPUT
            or INVOKEKIND.INVOKE_PROPERTYPUTREF;

    private static bool HasHiddenOrRestrictedTypeFlags(TYPEATTR attr)
        => ((TYPEFLAGS)attr.wTypeFlags & (TYPEFLAGS.TYPEFLAG_FHIDDEN | TYPEFLAGS.TYPEFLAG_FRESTRICTED)) != 0;

    private static bool HasHiddenOrRestrictedFuncFlags(FUNCDESC funcDesc)
        => (funcDesc.wFuncFlags & (short)(FUNCFLAGS.FUNCFLAG_FHIDDEN | FUNCFLAGS.FUNCFLAG_FRESTRICTED | FUNCFLAGS.FUNCFLAG_FNONBROWSABLE)) != 0;

    private static bool HasHiddenOrRestrictedVarFlags(VARDESC varDesc)
        => (varDesc.wVarFlags & (short)(VARFLAGS.VARFLAG_FHIDDEN | VARFLAGS.VARFLAG_FRESTRICTED | VARFLAGS.VARFLAG_FNONBROWSABLE)) != 0;

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string CreateFallbackQualifier(string referenceName)
    {
        var chars = referenceName
            .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            .ToArray();
        return chars.Length == 0 ? referenceName : new string(chars);
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(
        string szFile,
        REGKIND regkind,
        [MarshalAs(UnmanagedType.Interface)] out ITypeLib pptlib);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void LoadRegTypeLib(
        ref Guid rguid,
        ushort wVerMajor,
        ushort wVerMinor,
        int lcid,
        [MarshalAs(UnmanagedType.Interface)] out ITypeLib pptlib);

    private enum REGKIND
    {
        REGKIND_DEFAULT = 0,
        REGKIND_REGISTER = 1,
        REGKIND_NONE = 2
    }
}
