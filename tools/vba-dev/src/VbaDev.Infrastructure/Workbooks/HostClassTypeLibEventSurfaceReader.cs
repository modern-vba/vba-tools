using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VbaDev.App.HostClasses;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record HostClassTypeLibEventSurface(
    HostClassBaseTypeProvenance? BaseType,
    IReadOnlyDictionary<string, HostClassTypeLibEvent> Events)
{
    public static HostClassTypeLibEventSurface Empty { get; } = new(
        null,
        new Dictionary<string, HostClassTypeLibEvent>(StringComparer.OrdinalIgnoreCase));
}

internal sealed record HostClassTypeLibEvent(
    string Name,
    IReadOnlyList<HostEventParameter> Parameters,
    string? Documentation);

internal sealed class HostClassEventObservationConflictException(string eventName)
    : InvalidOperationException(
        $"Generated and TypeLib observations disagree on the callable contract for Event '{eventName}'.")
{
    public HostClassInspectionFailureReason Reason { get; } =
        HostClassInspectionFailureReason.EventEnumerationFailure;
}

internal static class HostClassTypeLibEventEvidenceMerger
{
    public static HostEventSignature Merge(
        HostEventSignature generated,
        HostClassTypeLibEventSurface typeLibSurface)
    {
        if (!typeLibSurface.Events.TryGetValue(generated.Name, out var structuralEvent))
        {
            return generated;
        }

        if (structuralEvent.Parameters.Count != generated.Parameters.Count)
        {
            throw new HostClassEventObservationConflictException(generated.Name);
        }

        var parameters = new HostEventParameter[generated.Parameters.Count];
        for (var index = 0; index < generated.Parameters.Count; index++)
        {
            var generatedParameter = generated.Parameters[index];
            var structuralParameter = structuralEvent.Parameters[index];
            if (generatedParameter.Passing != structuralParameter.Passing ||
                generatedParameter.ArrayShape != structuralParameter.ArrayShape ||
                generatedParameter.Optional != structuralParameter.Optional ||
                generatedParameter.ParamArray != structuralParameter.ParamArray ||
                !TryMergeTypeEvidence(
                    generatedParameter.Type,
                    structuralParameter.Type,
                    out var mergedType))
            {
                throw new HostClassEventObservationConflictException(generated.Name);
            }

            parameters[index] = generatedParameter with { Type = mergedType };
        }
        var documentation = string.IsNullOrWhiteSpace(generated.Documentation)
            ? structuralEvent.Documentation
            : generated.Documentation;
        if (parameters.SequenceEqual(generated.Parameters) &&
            string.Equals(documentation, generated.Documentation, StringComparison.Ordinal))
        {
            return generated;
        }

        return generated with
        {
            Parameters = parameters,
            Documentation = documentation
        };
    }

    private static bool TryMergeTypeEvidence(
        HostEventTypeReference generated,
        HostEventTypeReference structural,
        out HostEventTypeReference merged)
    {
        merged = generated;
        if (generated is IntrinsicHostEventTypeReference generatedIntrinsic &&
            structural is IntrinsicHostEventTypeReference structuralIntrinsic)
        {
            return generatedIntrinsic.Name.Equals(
                structuralIntrinsic.Name,
                StringComparison.OrdinalIgnoreCase);
        }

        if (generated is UnresolvedHostEventTypeReference unresolved &&
            GetTypeName(structural) is { } structuralName &&
            GetUnqualifiedName(unresolved.DisplayName).Equals(
                structuralName,
                StringComparison.OrdinalIgnoreCase))
        {
            merged = structural;
            return true;
        }

        if (generated is TypeLibHostEventTypeReference generatedTypeLib &&
            structural is TypeLibHostEventTypeReference structuralTypeLib)
        {
            return generatedTypeLib.Name.Equals(
                       structuralTypeLib.Name,
                       StringComparison.OrdinalIgnoreCase) &&
                   generatedTypeLib.LibraryGuid == structuralTypeLib.LibraryGuid &&
                   generatedTypeLib.MajorVersion == structuralTypeLib.MajorVersion &&
                   generatedTypeLib.MinorVersion == structuralTypeLib.MinorVersion &&
                   generatedTypeLib.Lcid == structuralTypeLib.Lcid;
        }

        return false;
    }

    private static string? GetTypeName(HostEventTypeReference type)
        => type switch
        {
            IntrinsicHostEventTypeReference intrinsic => intrinsic.Name,
            TypeLibHostEventTypeReference typeLib => typeLib.Name,
            UnresolvedHostEventTypeReference unresolved => unresolved.DisplayName,
            _ => null
        };

    private static string GetUnqualifiedName(string displayName)
    {
        var separator = displayName.LastIndexOf('.');
        return separator < 0 ? displayName : displayName[(separator + 1)..];
    }
}

internal static class HostClassTypeLibEventSurfaceReader
{
    private const int TypeDocumentationMemberId = -1;
    private static readonly IReadOnlyDictionary<int, string> ComInfrastructureMembers =
        new Dictionary<int, string>
        {
            [unchecked((int)0x60000000)] = "QueryInterface",
            [unchecked((int)0x60000001)] = "AddRef",
            [unchecked((int)0x60000002)] = "Release",
            [unchecked((int)0x60010000)] = "GetTypeInfoCount",
            [unchecked((int)0x60010001)] = "GetTypeInfo",
            [unchecked((int)0x60010002)] = "GetIDsOfNames",
            [unchecked((int)0x60010003)] = "Invoke"
        };

    public static bool TryRead(
        object runtimeHostObject,
        out HostClassTypeLibEventSurface surface)
    {
        try
        {
            surface = Read(runtimeHostObject);
            return true;
        }
        catch (Exception exception) when (IsSupplementalMetadataFailure(exception))
        {
            surface = HostClassTypeLibEventSurface.Empty;
            return false;
        }
    }

    public static HostClassTypeLibEventSurface Read(object runtimeHostObject)
    {
        ArgumentNullException.ThrowIfNull(runtimeHostObject);
        ITypeInfo? classTypeInfo = null;
        ITypeInfo? defaultInterfaceTypeInfo = null;
        ITypeInfo? sourceTypeInfo = null;
        try
        {
            classTypeInfo = ReadClassTypeInfo(runtimeHostObject);
            var classAttribute = ReadTypeAttribute(classTypeInfo, out var classAttributePointer);
            try
            {
                if (classAttribute.typekind != TYPEKIND.TKIND_COCLASS)
                {
                    throw new InvalidOperationException(
                        "IProvideClassInfo did not return coclass metadata for the intrinsic host object.");
                }

                sourceTypeInfo = ReadDefaultSourceTypeInfo(classTypeInfo, classAttribute);
                defaultInterfaceTypeInfo = ReadDefaultInterfaceTypeInfo(
                    classTypeInfo,
                    classAttribute);
            }
            finally
            {
                classTypeInfo.ReleaseTypeAttr(classAttributePointer);
            }

            var baseType = TryReadTypeProvenance(defaultInterfaceTypeInfo);
            var events = ReadEvents(sourceTypeInfo);
            return new HostClassTypeLibEventSurface(baseType, events);
        }
        finally
        {
            ReleaseComReference(sourceTypeInfo);
            ReleaseComReference(defaultInterfaceTypeInfo);
            ReleaseComReference(classTypeInfo);
        }
    }

    private static ITypeInfo ReadClassTypeInfo(object runtimeHostObject)
    {
        if (runtimeHostObject is IProvideClassInfo classInfoProvider)
        {
            ITypeInfo? classTypeInfo = null;
            var result = classInfoProvider.GetClassInfo(out classTypeInfo);
            if (result < 0)
            {
                ReleaseComReference(classTypeInfo);
                Marshal.ThrowExceptionForHR(result);
            }

            return classTypeInfo ?? throw new InvalidOperationException(
                "IProvideClassInfo succeeded without returning type information.");
        }

        if (runtimeHostObject is not IDispatch dispatch)
        {
            throw new InvalidOperationException(
                "The intrinsic host object exposes neither IProvideClassInfo nor IDispatch.");
        }

        var countResult = dispatch.GetTypeInfoCount(out var typeInfoCount);
        Marshal.ThrowExceptionForHR(countResult);
        if (typeInfoCount == 0)
        {
            throw new InvalidOperationException(
                "The intrinsic host object's IDispatch interface exposes no type information.");
        }

        ITypeInfo? dispatchTypeInfo = null;
        var typeInfoResult = dispatch.GetTypeInfo(0, 0, out dispatchTypeInfo);
        if (typeInfoResult < 0)
        {
            ReleaseComReference(dispatchTypeInfo);
            Marshal.ThrowExceptionForHR(typeInfoResult);
        }
        if (dispatchTypeInfo is null)
        {
            throw new InvalidOperationException(
                "IDispatch succeeded without returning type information.");
        }

        try
        {
            return ResolveCoClassTypeInfo(dispatchTypeInfo);
        }
        finally
        {
            ReleaseComReference(dispatchTypeInfo);
        }
    }

    private static ITypeInfo ResolveCoClassTypeInfo(ITypeInfo dispatchTypeInfo)
    {
        var dispatchAttribute = ReadTypeAttribute(
            dispatchTypeInfo,
            out var dispatchAttributePointer);
        Guid dispatchInterfaceId;
        try
        {
            if (dispatchAttribute.typekind is not (
                    TYPEKIND.TKIND_DISPATCH or TYPEKIND.TKIND_INTERFACE))
            {
                throw new InvalidOperationException(
                    "IDispatch returned metadata that is not a dispatch or interface type.");
            }

            dispatchInterfaceId = dispatchAttribute.guid;
        }
        finally
        {
            dispatchTypeInfo.ReleaseTypeAttr(dispatchAttributePointer);
        }

        ITypeLib? typeLibrary = null;
        ITypeInfo? match = null;
        try
        {
            dispatchTypeInfo.GetContainingTypeLib(out typeLibrary, out var dispatchTypeIndex);
            var count = typeLibrary.GetTypeInfoCount();
            for (var index = 0; index < count; index++)
            {
                if (index == dispatchTypeIndex)
                {
                    continue;
                }

                ITypeInfo? candidate = null;
                try
                {
                    typeLibrary.GetTypeInfo(index, out candidate);
                    if (!CoClassHasDefaultInterface(candidate, dispatchInterfaceId))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        throw new InvalidOperationException(
                            "More than one coclass exposes the intrinsic host object's dispatch interface as its default interface.");
                    }

                    match = candidate;
                    candidate = null;
                }
                finally
                {
                    ReleaseComReference(candidate);
                }
            }

            return match ?? throw new InvalidOperationException(
                "No coclass exposes the intrinsic host object's dispatch interface as its default interface.");
        }
        catch
        {
            ReleaseComReference(match);
            throw;
        }
        finally
        {
            ReleaseComReference(typeLibrary);
        }
    }

    private static bool CoClassHasDefaultInterface(
        ITypeInfo candidate,
        Guid dispatchInterfaceId)
    {
        var attribute = ReadTypeAttribute(candidate, out var attributePointer);
        try
        {
            if (attribute.typekind != TYPEKIND.TKIND_COCLASS)
            {
                return false;
            }

            for (var index = 0; index < attribute.cImplTypes; index++)
            {
                candidate.GetImplTypeFlags(index, out var flags);
                if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) == 0 ||
                    (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0)
                {
                    continue;
                }

                ITypeInfo? implementedTypeInfo = null;
                try
                {
                    candidate.GetRefTypeOfImplType(index, out var implementedReference);
                    candidate.GetRefTypeInfo(implementedReference, out implementedTypeInfo);
                    var implementedAttribute = ReadTypeAttribute(
                        implementedTypeInfo,
                        out var implementedAttributePointer);
                    try
                    {
                        if (implementedAttribute.guid == dispatchInterfaceId)
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        implementedTypeInfo.ReleaseTypeAttr(implementedAttributePointer);
                    }
                }
                finally
                {
                    ReleaseComReference(implementedTypeInfo);
                }
            }

            return false;
        }
        finally
        {
            candidate.ReleaseTypeAttr(attributePointer);
        }
    }

    private static ITypeInfo ReadDefaultSourceTypeInfo(
        ITypeInfo classTypeInfo,
        TYPEATTR classAttribute)
    {
        ITypeInfo? sourceTypeInfo = null;
        for (var index = 0; index < classAttribute.cImplTypes; index++)
        {
            classTypeInfo.GetImplTypeFlags(index, out var flags);
            var required = IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT |
                IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE;
            if ((flags & required) != required)
            {
                continue;
            }

            if (sourceTypeInfo is not null)
            {
                ReleaseComReference(sourceTypeInfo);
                throw new InvalidOperationException(
                    "The intrinsic host coclass exposes more than one default source interface.");
            }

            classTypeInfo.GetRefTypeOfImplType(index, out var sourceReference);
            classTypeInfo.GetRefTypeInfo(sourceReference, out sourceTypeInfo);
        }

        return sourceTypeInfo ?? throw new InvalidOperationException(
            "The intrinsic host coclass does not expose a default source interface.");
    }

    private static ITypeInfo? ReadDefaultInterfaceTypeInfo(
        ITypeInfo classTypeInfo,
        TYPEATTR classAttribute)
    {
        ITypeInfo? defaultInterfaceTypeInfo = null;
        for (var index = 0; index < classAttribute.cImplTypes; index++)
        {
            classTypeInfo.GetImplTypeFlags(index, out var flags);
            if ((flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) == 0 ||
                (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0)
            {
                continue;
            }

            if (defaultInterfaceTypeInfo is not null)
            {
                ReleaseComReference(defaultInterfaceTypeInfo);
                throw new InvalidOperationException(
                    "The intrinsic host coclass exposes more than one default interface.");
            }

            classTypeInfo.GetRefTypeOfImplType(index, out var defaultReference);
            classTypeInfo.GetRefTypeInfo(defaultReference, out defaultInterfaceTypeInfo);
        }

        return defaultInterfaceTypeInfo;
    }

    private static IReadOnlyDictionary<string, HostClassTypeLibEvent> ReadEvents(
        ITypeInfo sourceTypeInfo)
    {
        var attribute = ReadTypeAttribute(sourceTypeInfo, out var attributePointer);
        try
        {
            var events = new Dictionary<string, HostClassTypeLibEvent>(
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < attribute.cFuncs; index++)
            {
                var functionPointer = nint.Zero;
                try
                {
                    sourceTypeInfo.GetFuncDesc(index, out functionPointer);
                    var function = Marshal.PtrToStructure<FUNCDESC>(functionPointer);
                    var names = ReadNames(
                        sourceTypeInfo,
                        function.memid,
                        function.cParams + 1);
                    var eventName = names.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(eventName))
                    {
                        throw new InvalidOperationException(
                            $"The default source interface member at index {index} has no name.");
                    }

                    if (IsComInfrastructureMember(function.memid, eventName))
                    {
                        continue;
                    }

                    var returnType = (VarEnum)function.elemdescFunc.tdesc.vt;
                    if (function.invkind != INVOKEKIND.INVOKE_FUNC ||
                        returnType is not (VarEnum.VT_VOID or VarEnum.VT_HRESULT))
                    {
                        throw new InvalidOperationException(
                            $"Default source member '{eventName}' is not a void Event procedure.");
                    }

                    sourceTypeInfo.GetDocumentation(
                        function.memid,
                        out _,
                        out var documentation,
                        out _,
                        out _);
                    var parameters = ReadParameters(
                        sourceTypeInfo,
                        function,
                        names.Skip(1).ToArray());
                    if (!events.TryAdd(
                            eventName,
                            new HostClassTypeLibEvent(
                                eventName,
                                parameters,
                                EmptyToNull(documentation))))
                    {
                        throw new InvalidOperationException(
                            $"The default source interface exposes Event '{eventName}' more than once.");
                    }
                }
                finally
                {
                    if (functionPointer != nint.Zero)
                    {
                        sourceTypeInfo.ReleaseFuncDesc(functionPointer);
                    }
                }
            }

            return events;
        }
        finally
        {
            sourceTypeInfo.ReleaseTypeAttr(attributePointer);
        }
    }

    private static IReadOnlyList<HostEventParameter> ReadParameters(
        ITypeInfo sourceTypeInfo,
        FUNCDESC function,
        IReadOnlyList<string> names)
    {
        if (function.cParams <= 0)
        {
            return [];
        }

        if (function.lprgelemdescParam == nint.Zero)
        {
            throw new InvalidOperationException(
                "The TypeLib Event declares parameters without an ELEMDESC array.");
        }

        var result = new List<HostEventParameter>(function.cParams);
        var elementSize = Marshal.SizeOf<ELEMDESC>();
        for (var index = 0; index < function.cParams; index++)
        {
            var pointer = nint.Add(function.lprgelemdescParam, index * elementSize);
            var element = Marshal.PtrToStructure<ELEMDESC>(pointer);
            var flags = element.desc.paramdesc.wParamFlags;
            if ((flags & (PARAMFLAG.PARAMFLAG_FRETVAL | PARAMFLAG.PARAMFLAG_FLCID)) != 0)
            {
                continue;
            }

            var parameterName = index < names.Count
                ? names[index]
                : null;
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                throw new InvalidOperationException(
                    $"The TypeLib Event parameter at ordinal {index} has no name.");
            }

            var type = ReadTypeReference(sourceTypeInfo, element.tdesc)
                ?? throw new InvalidOperationException(
                    $"The TypeLib Event parameter '{parameterName}' has no representable VBA type.");
            var optional = (flags & (
                PARAMFLAG.PARAMFLAG_FOPT |
                PARAMFLAG.PARAMFLAG_FHASDEFAULT)) != 0;
            var paramArray = function.cParamsOpt == -1 &&
                index == function.cParams - 1;
            var isArray = paramArray || IsArrayType(element.tdesc);
            result.Add(new HostEventParameter(
                parameterName,
                type,
                ReadPassingMechanism(
                    sourceTypeInfo,
                    flags,
                    element.tdesc,
                    isArray),
                isArray
                    ? HostEventArrayShape.Array
                    : HostEventArrayShape.Scalar,
                optional,
                paramArray));
        }

        return result;
    }

    private static HostEventPassingMechanism ReadPassingMechanism(
        ITypeInfo declaringTypeInfo,
        PARAMFLAG flags,
        TYPEDESC typeDescription,
        bool isArray)
    {
        if ((flags & PARAMFLAG.PARAMFLAG_FOUT) != 0 || isArray)
        {
            return HostEventPassingMechanism.ByRef;
        }

        if ((flags & PARAMFLAG.PARAMFLAG_FIN) == 0)
        {
            throw new InvalidOperationException(
                "The TypeLib Event parameter declares neither input nor output direction metadata.");
        }

        if ((VarEnum)typeDescription.vt != VarEnum.VT_PTR)
        {
            return HostEventPassingMechanism.ByVal;
        }

        if (typeDescription.lpValue == nint.Zero)
        {
            throw new InvalidOperationException(
                "The TypeLib Event parameter has a null pointer type descriptor.");
        }

        var pointedType = Marshal.PtrToStructure<TYPEDESC>(typeDescription.lpValue);
        var pointedVariableType = (VarEnum)pointedType.vt;
        if (pointedVariableType is VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN ||
            pointedVariableType == VarEnum.VT_USERDEFINED &&
            IsObjectTypeReference(declaringTypeInfo, pointedType))
        {
            return HostEventPassingMechanism.ByVal;
        }

        return HostEventPassingMechanism.ByRef;
    }

    private static bool IsObjectTypeReference(
        ITypeInfo declaringTypeInfo,
        TYPEDESC typeDescription)
    {
        ITypeInfo? referencedTypeInfo = null;
        try
        {
            var typeReference = unchecked((int)typeDescription.lpValue.ToInt64());
            declaringTypeInfo.GetRefTypeInfo(typeReference, out referencedTypeInfo);
            var attribute = ReadTypeAttribute(
                referencedTypeInfo,
                out var attributePointer);
            try
            {
                return attribute.typekind is
                    TYPEKIND.TKIND_COCLASS or
                    TYPEKIND.TKIND_DISPATCH or
                    TYPEKIND.TKIND_INTERFACE;
            }
            finally
            {
                referencedTypeInfo.ReleaseTypeAttr(attributePointer);
            }
        }
        finally
        {
            ReleaseComReference(referencedTypeInfo);
        }
    }

    private static bool IsArrayType(TYPEDESC typeDescription)
    {
        var variableType = (VarEnum)typeDescription.vt;
        if (variableType is VarEnum.VT_SAFEARRAY or VarEnum.VT_CARRAY)
        {
            return true;
        }

        if (variableType != VarEnum.VT_PTR || typeDescription.lpValue == nint.Zero)
        {
            return false;
        }

        return IsArrayType(Marshal.PtrToStructure<TYPEDESC>(typeDescription.lpValue));
    }

    private static bool IsComInfrastructureMember(int memberId, string memberName)
    {
        if (!ComInfrastructureMembers.TryGetValue(memberId, out var expectedName))
        {
            return false;
        }

        if (!memberName.Equals(expectedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"COM infrastructure member ID 0x{memberId:X8} was named '{memberName}' instead of '{expectedName}'.");
        }

        return true;
    }

    private static HostEventTypeReference? ReadTypeReference(
        ITypeInfo declaringTypeInfo,
        TYPEDESC typeDescription)
    {
        var variableType = (VarEnum)typeDescription.vt;
        var primitiveType = CreatePrimitiveTypeReference(variableType);
        if (primitiveType is not null)
        {
            return primitiveType;
        }

        return variableType switch
        {
            VarEnum.VT_VOID or VarEnum.VT_EMPTY or VarEnum.VT_HRESULT => null,
            VarEnum.VT_PTR or VarEnum.VT_SAFEARRAY =>
                ReadNestedTypeReference(declaringTypeInfo, typeDescription),
            VarEnum.VT_CARRAY =>
                ReadCArrayElementTypeReference(declaringTypeInfo, typeDescription),
            VarEnum.VT_USERDEFINED =>
                ReadUserDefinedTypeReference(declaringTypeInfo, typeDescription),
            _ => null
        };
    }

    internal static HostEventTypeReference? CreatePrimitiveTypeReference(
        VarEnum variableType)
        => variableType switch
        {
            VarEnum.VT_BSTR => new IntrinsicHostEventTypeReference("String"),
            VarEnum.VT_BOOL => new IntrinsicHostEventTypeReference("Boolean"),
            VarEnum.VT_I1 => new UnresolvedHostEventTypeReference("VT_I1"),
            VarEnum.VT_UI1 => new IntrinsicHostEventTypeReference("Byte"),
            VarEnum.VT_I2 => new IntrinsicHostEventTypeReference("Integer"),
            VarEnum.VT_UI2 => new UnresolvedHostEventTypeReference("VT_UI2"),
            VarEnum.VT_I4 or VarEnum.VT_INT => new IntrinsicHostEventTypeReference("Long"),
            VarEnum.VT_UI4 => new UnresolvedHostEventTypeReference("VT_UI4"),
            VarEnum.VT_UINT => new UnresolvedHostEventTypeReference("VT_UINT"),
            VarEnum.VT_I8 => new IntrinsicHostEventTypeReference("LongLong"),
            VarEnum.VT_UI8 => new UnresolvedHostEventTypeReference("VT_UI8"),
            VarEnum.VT_R4 => new IntrinsicHostEventTypeReference("Single"),
            VarEnum.VT_R8 => new IntrinsicHostEventTypeReference("Double"),
            VarEnum.VT_CY => new IntrinsicHostEventTypeReference("Currency"),
            VarEnum.VT_DATE => new IntrinsicHostEventTypeReference("Date"),
            VarEnum.VT_VARIANT => new IntrinsicHostEventTypeReference("Variant"),
            VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN =>
                new IntrinsicHostEventTypeReference("Object"),
            _ => null
        };

    private static HostEventTypeReference? ReadNestedTypeReference(
        ITypeInfo declaringTypeInfo,
        TYPEDESC typeDescription)
    {
        if (typeDescription.lpValue == nint.Zero)
        {
            return null;
        }

        var nested = Marshal.PtrToStructure<TYPEDESC>(typeDescription.lpValue);
        return ReadTypeReference(declaringTypeInfo, nested);
    }

    private static HostEventTypeReference? ReadCArrayElementTypeReference(
        ITypeInfo declaringTypeInfo,
        TYPEDESC typeDescription)
    {
        if (typeDescription.lpValue == nint.Zero)
        {
            return null;
        }

        var array = Marshal.PtrToStructure<ArrayDescription>(typeDescription.lpValue);
        return ReadTypeReference(declaringTypeInfo, array.ElementType);
    }

    private static HostEventTypeReference? ReadUserDefinedTypeReference(
        ITypeInfo declaringTypeInfo,
        TYPEDESC typeDescription)
    {
        ITypeInfo? referencedTypeInfo = null;
        try
        {
            var typeReference = unchecked((int)typeDescription.lpValue.ToInt64());
            declaringTypeInfo.GetRefTypeInfo(typeReference, out referencedTypeInfo);
            referencedTypeInfo.GetDocumentation(
                TypeDocumentationMemberId,
                out var name,
                out _,
                out _,
                out _);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            try
            {
                var provenance = ReadTypeProvenance(referencedTypeInfo);
                return new TypeLibHostEventTypeReference(
                    name,
                    provenance.LibraryGuid,
                    provenance.MajorVersion,
                    provenance.MinorVersion,
                    provenance.Lcid);
            }
            catch (Exception exception) when (IsSupplementalMetadataFailure(exception))
            {
                return new UnresolvedHostEventTypeReference(name);
            }
        }
        finally
        {
            ReleaseComReference(referencedTypeInfo);
        }
    }

    private static HostClassBaseTypeProvenance ReadTypeProvenance(ITypeInfo typeInfo)
    {
        ITypeLib? typeLibrary = null;
        var libraryAttributePointer = nint.Zero;
        try
        {
            typeInfo.GetContainingTypeLib(out typeLibrary, out _);
            typeLibrary.GetLibAttr(out libraryAttributePointer);
            var attribute = Marshal.PtrToStructure<TYPELIBATTR>(libraryAttributePointer);
            typeInfo.GetDocumentation(
                TypeDocumentationMemberId,
                out var name,
                out _,
                out _,
                out _);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("TypeLib metadata has no type name.");
            }

            return new HostClassBaseTypeProvenance(
                name,
                attribute.guid,
                unchecked((ushort)attribute.wMajorVerNum),
                unchecked((ushort)attribute.wMinorVerNum),
                attribute.lcid);
        }
        finally
        {
            if (libraryAttributePointer != nint.Zero && typeLibrary is not null)
            {
                typeLibrary.ReleaseTLibAttr(libraryAttributePointer);
            }

            ReleaseComReference(typeLibrary);
        }
    }

    private static HostClassBaseTypeProvenance? TryReadTypeProvenance(
        ITypeInfo? typeInfo)
    {
        if (typeInfo is null)
        {
            return null;
        }

        try
        {
            return ReadTypeProvenance(typeInfo);
        }
        catch (Exception exception) when (IsSupplementalMetadataFailure(exception))
        {
            return null;
        }
    }

    private static TYPEATTR ReadTypeAttribute(ITypeInfo typeInfo, out nint pointer)
    {
        typeInfo.GetTypeAttr(out pointer);
        try
        {
            return Marshal.PtrToStructure<TYPEATTR>(pointer);
        }
        catch
        {
            typeInfo.ReleaseTypeAttr(pointer);
            throw;
        }
    }

    private static string[] ReadNames(ITypeInfo typeInfo, int memberId, int maximum)
    {
        var names = new string[Math.Max(1, maximum)];
        typeInfo.GetNames(memberId, names, names.Length, out var count);
        return names.Take(count).ToArray();
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ReleaseComReference(object? value)
    {
        if (OperatingSystem.IsWindows() &&
            value is not null &&
            Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static bool IsSupplementalMetadataFailure(Exception exception)
        => exception is COMException or InvalidOperationException or InvalidCastException or
            ArgumentException or OverflowException or NotSupportedException;

    [ComImport]
    [Guid("B196B283-BAB4-101A-B69C-00AA00341D07")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IProvideClassInfo
    {
        [PreserveSig]
        int GetClassInfo([MarshalAs(UnmanagedType.Interface)] out ITypeInfo typeInfo);
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatch
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(
            uint typeInfoIndex,
            uint lcid,
            [MarshalAs(UnmanagedType.Interface)] out ITypeInfo typeInfo);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ArrayDescription
    {
        public TYPEDESC ElementType;
        public ushort Dimensions;
    }
}
