using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using VbaTools.Syntax;
using VbaTools.Semantics;
using VbaDev.App.References;

namespace VbaDev.Infrastructure.References;

/// <summary>
/// Reads TypeLib metadata through the Windows COM TypeLib APIs.
/// </summary>
public sealed class ComTypeLibCatalogMetadataReader : ITypeLibCatalogMetadataReader
{
    private const int TypeDocumentationMemberId = -1;
    private readonly Func<VbaProjectReferenceCatalogIdentity, ITypeLib>? typeLibLoader;
    private readonly Func<string, ITypeLib>? observedPathTypeLibLoader;
    private readonly Action<object> comObjectReleaser;

    /// <summary>
    /// Creates a reader backed by the Windows COM TypeLib loader.
    /// </summary>
    public ComTypeLibCatalogMetadataReader()
    {
        comObjectReleaser = ReleaseComObject;
    }

    internal ComTypeLibCatalogMetadataReader(
        Func<VbaProjectReferenceCatalogIdentity, ITypeLib> typeLibLoader)
        : this(
            typeLibLoader ?? throw new ArgumentNullException(nameof(typeLibLoader)),
            observedPathTypeLibLoader: null,
            ReleaseComObject)
    {
    }

    internal ComTypeLibCatalogMetadataReader(
        Func<VbaProjectReferenceCatalogIdentity, ITypeLib> typeLibLoader,
        Action<object> comObjectReleaser)
        : this(
            typeLibLoader ?? throw new ArgumentNullException(nameof(typeLibLoader)),
            observedPathTypeLibLoader: null,
            comObjectReleaser)
    {
    }

    internal ComTypeLibCatalogMetadataReader(
        Func<VbaProjectReferenceCatalogIdentity, ITypeLib>? typeLibLoader,
        Func<string, ITypeLib>? observedPathTypeLibLoader,
        Action<object> comObjectReleaser)
    {
        this.typeLibLoader = typeLibLoader;
        this.observedPathTypeLibLoader = observedPathTypeLibLoader;
        this.comObjectReleaser = comObjectReleaser
            ?? throw new ArgumentNullException(nameof(comObjectReleaser));
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
            var typeLib = typeLibLoader(identity);
            try
            {
                return ReadLoadedMetadata(identity, typeLib);
            }
            finally
            {
                comObjectReleaser(typeLib);
            }
        }

        if (!OperatingSystem.IsWindows() && observedPathTypeLibLoader is null)
        {
            return new TypeLibCatalogMetadata(CreateFallbackQualifier(identity.ReferenceName), []);
        }

        return ReadWindowsMetadata(identity);
    }

    /// <summary>Reads an observed library's actual identity, including LCID, without requiring a registry entry.</summary>
    public AcquiredTypeLibCatalogMetadata ReadMetadataFromPath(string referenceName, string path)
    {
        if (!OperatingSystem.IsWindows() && observedPathTypeLibLoader is null)
            throw new PlatformNotSupportedException("Observed TypeLib metadata requires Windows COM.");
        return ReadObservedWindowsMetadata(referenceName, path);
    }

    private AcquiredTypeLibCatalogMetadata ReadObservedWindowsMetadata(string referenceName, string path)
    {
        var typeLib = LoadPathTypeLib(path);
        try
        {
            var identity = ReadLibraryIdentity(typeLib, referenceName, path);
            return new(identity, ReadLoadedMetadata(identity, typeLib));
        }
        finally
        {
            comObjectReleaser(typeLib);
        }
    }

    private static VbaProjectReferenceCatalogIdentity ReadLibraryIdentity(ITypeLib typeLib, string referenceName, string path)
    {
        var attrPointer = IntPtr.Zero;
        try
        {
            typeLib.GetLibAttr(out attrPointer);
            var attributes = Marshal.PtrToStructure<TYPELIBATTR>(attrPointer);
            return new(referenceName, attributes.guid.ToString("D"),
                unchecked((ushort)attributes.wMajorVerNum), unchecked((ushort)attributes.wMinorVerNum), attributes.lcid, path);
        }
        finally
        {
            if (attrPointer != IntPtr.Zero) typeLib.ReleaseTLibAttr(attrPointer);
        }
    }

    private TypeLibCatalogMetadata ReadWindowsMetadata(VbaProjectReferenceCatalogIdentity identity)
    {
        var typeLib = LoadPathTypeLib(identity.Path);
        try
        {
            ValidateWindowsTypeLibIdentity(typeLib, identity);
            return ReadLoadedMetadata(identity, typeLib);
        }
        finally
        {
            comObjectReleaser(typeLib);
        }
    }

    private TypeLibCatalogMetadata ReadLoadedMetadata(
        VbaProjectReferenceCatalogIdentity identity,
        ITypeLib typeLib)
    {
        typeLib.GetDocumentation(TypeDocumentationMemberId, out var libraryName, out _, out _, out _);

        var typeInfos = ReadTypeInfos(typeLib);
        try
        {
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
        finally
        {
            ReleaseTypeInfos(typeInfos);
        }
    }

    private ITypeLib LoadPathTypeLib(string path)
    {
        if (observedPathTypeLibLoader is not null)
        {
            return observedPathTypeLibLoader(path);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Observed TypeLib metadata requires Windows COM.");
        }

        LoadTypeLibEx(path, REGKIND.REGKIND_NONE, out var typeLib);
        return typeLib;
    }

    private static void ValidateWindowsTypeLibIdentity(
        ITypeLib typeLib,
        VbaProjectReferenceCatalogIdentity identity)
    {
        var loaded = ReadLibraryIdentity(typeLib, identity.ReferenceName, identity.Path);
        if (!Guid.TryParse(identity.Guid, out var expectedGuid)
            || Guid.Parse(loaded.Guid) != expectedGuid
            || loaded.MajorVersion != identity.MajorVersion
            || loaded.MinorVersion != identity.MinorVersion)
        {
            throw new InvalidDataException(
                $"The TypeLib at '{identity.Path}' has identity "
                + $"{loaded.Guid} {loaded.MajorVersion}.{loaded.MinorVersion}; "
                + $"expected {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion}.");
        }
    }

    private IReadOnlyList<ITypeInfo> ReadTypeInfos(ITypeLib typeLib)
    {
        var typeInfos = new List<ITypeInfo>();
        try
        {
            var count = typeLib.GetTypeInfoCount();
            for (var index = 0; index < count; index++)
            {
                typeLib.GetTypeInfo(index, out var typeInfo);
                typeInfos.Add(typeInfo);
            }

            return typeInfos;
        }
        catch
        {
            ReleaseTypeInfos(typeInfos);
            throw;
        }
    }

    private void ReleaseTypeInfos(IReadOnlyList<ITypeInfo> typeInfos)
    {
        for (var index = typeInfos.Count - 1; index >= 0; index--)
        {
            comObjectReleaser(typeInfos[index]);
        }
    }

    private TypeLibCatalogType? ReadType(ITypeInfo typeInfo, bool allowHiddenType = false)
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

    private IReadOnlyList<TypeLibCatalogImplementedInterface> ReadImplementedInterfaces(
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
            ITypeInfo? implementedTypeInfo = null;
            try
            {
                typeInfo.GetRefTypeInfo(href, out implementedTypeInfo);
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
            finally
            {
                if (implementedTypeInfo is not null)
                {
                    comObjectReleaser(implementedTypeInfo);
                }
            }
        }

        return implementedInterfaces;
    }

    private IReadOnlyList<TypeLibCatalogType> ReadCoClassForwardedMembers(IReadOnlyList<ITypeInfo> typeInfos)
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
                    ITypeInfo? implementedInfo = null;
                    try
                    {
                        coClassInfo.GetRefTypeInfo(href, out implementedInfo);
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
                    finally
                    {
                        if (implementedInfo is not null)
                        {
                            comObjectReleaser(implementedInfo);
                        }
                    }
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

    private IReadOnlyList<TypeLibCatalogMember> ReadVariableMembers(
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

    private IReadOnlyList<TypeLibCatalogMember> ReadFunctionMembers(
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
                    ? CreateSignature(
                        memberName, parameters, returnType, EmptyToNull(documentation), callableKind,
                        GetCallablePassingConvention(attr, funcDesc.funckind))
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

    private IReadOnlyList<VbaCallableParameter> ReadParameters(
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
        if (funcDesc.cParamsOpt == -1 && funcDesc.cParams <= 0)
        {
            isComplete = false;
        }
        if (funcDesc.cParams <= 0
            || funcDesc.lprgelemdescParam == IntPtr.Zero)
        {
            return [];
        }

        var parameters = new List<VbaCallableParameter>();
        var elementSize = Marshal.SizeOf<ELEMDESC>();
        var lastVisibleParameterIndex = -1;
        for (var index = funcDesc.cParams - 1; index >= 0; index--)
        {
            var elementPointer = IntPtr.Add(funcDesc.lprgelemdescParam, index * elementSize);
            var flags = Marshal.PtrToStructure<ELEMDESC>(elementPointer)
                .desc.paramdesc.wParamFlags;
            if ((flags & (PARAMFLAG.PARAMFLAG_FRETVAL | PARAMFLAG.PARAMFLAG_FLCID)) == 0)
            {
                lastVisibleParameterIndex = index;
                break;
            }
        }
        if (funcDesc.cParamsOpt == -1 && lastVisibleParameterIndex < 0)
        {
            isComplete = false;
        }

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
            var hasVariadicMetadata = funcDesc.cParamsOpt == -1
                && index == lastVisibleParameterIndex;
            var isParamArray = hasVariadicMetadata
                && (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FOUT) == 0
                && IsVariantSafeArrayParameter(element.tdesc);
            var isOptional = !isParamArray
                && ((element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FOPT) != 0
                    || (element.desc.paramdesc.wParamFlags & PARAMFLAG.PARAMFLAG_FHASDEFAULT) != 0);
            var isArray = GetArrayTypeEvidence(element.tdesc);
            if (!isParamArray && (hasVariadicMetadata || isArray is null))
            {
                isComplete = false;
            }

            parameters.Add(new VbaCallableParameter(
                parameterName,
                IsOptional: isOptional,
                TypeReference: ToTypeReference(typeInfo, element.tdesc),
                IsByRef: GetParameterPassing(element),
                IsParamArray: isParamArray,
                IsArray: isArray == true)
            {
                TypeLibPassing = new VbaTypeLibParameterPassing(
                    GetParameterDirection(element.desc.paramdesc.wParamFlags),
                    GetAbiPointerDepth(element.tdesc))
            });
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
        VbaCallableKind callableKind,
        VbaCallablePassingConvention passingConvention)
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
            SupportsNamedArguments: true)
        {
            PassingConvention = passingConvention
        };
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
        => TypeLibCatalogMemberFacts.IsBrowsableFunction(functionFlags);

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

    private static VbaCallablePassingConvention GetCallablePassingConvention(
        TYPEATTR type, FUNCKIND functionKind)
    {
        var supportsAutomation = functionKind == FUNCKIND.FUNC_DISPATCH
            || functionKind is FUNCKIND.FUNC_VIRTUAL or FUNCKIND.FUNC_PUREVIRTUAL
                && (type.typekind == TYPEKIND.TKIND_DISPATCH
                    || type.typekind == TYPEKIND.TKIND_INTERFACE
                        && (type.wTypeFlags & (TYPEFLAGS.TYPEFLAG_FOLEAUTOMATION | TYPEFLAGS.TYPEFLAG_FDUAL)) != 0);
        return supportsAutomation
            ? VbaCallablePassingConvention.AutomationDispatch
            : VbaCallablePassingConvention.OtherExternal;
    }

    private static VbaTypeLibParameterDirection GetParameterDirection(PARAMFLAG flags)
        => ((flags & PARAMFLAG.PARAMFLAG_FIN) != 0, (flags & PARAMFLAG.PARAMFLAG_FOUT) != 0) switch
        {
            (true, false) => VbaTypeLibParameterDirection.Input,
            (false, true) => VbaTypeLibParameterDirection.Output,
            (true, true) => VbaTypeLibParameterDirection.InputOutput,
            _ => VbaTypeLibParameterDirection.Unknown
        };

    private static int? GetAbiPointerDepth(TYPEDESC typeDesc)
    {
        var depth = 0;
        while ((VarEnum)typeDesc.vt == VarEnum.VT_PTR)
        {
            if (!TryGetNestedTypeDescription(typeDesc, out typeDesc))
            {
                return null;
            }
            depth++;
        }
        return depth;
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

    private static bool IsVariantSafeArrayParameter(TYPEDESC typeDesc)
    {
        var pointerDepth = 0;
        while ((VarEnum)typeDesc.vt == VarEnum.VT_PTR)
        {
            pointerDepth++;
            if (!TryGetNestedTypeDescription(typeDesc, out typeDesc))
            {
                return false;
            }
        }

        return pointerDepth > 0
            && (VarEnum)typeDesc.vt == VarEnum.VT_SAFEARRAY
            && TryGetNestedTypeDescription(typeDesc, out var elementType)
            && (VarEnum)elementType.vt == VarEnum.VT_VARIANT;
    }

    private VbaTypeReference? ToTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
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

    private VbaTypeReference? ToNestedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
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

    private VbaTypeReference? ToUserDefinedTypeReference(ITypeInfo typeInfo, TYPEDESC typeDesc)
    {
        ITypeInfo? referencedTypeInfo = null;
        try
        {
            var hrefType = unchecked((int)typeDesc.lpValue.ToInt64());
            typeInfo.GetRefTypeInfo(hrefType, out referencedTypeInfo);
            referencedTypeInfo.GetDocumentation(TypeDocumentationMemberId, out var name, out _, out _, out _);
            return string.IsNullOrEmpty(name) ? null : new VbaTypeReference(name);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (referencedTypeInfo is not null)
            {
                comObjectReleaser(referencedTypeInfo);
            }
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

    private static void ReleaseComObject(object value)
    {
        if (OperatingSystem.IsWindows() && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

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
