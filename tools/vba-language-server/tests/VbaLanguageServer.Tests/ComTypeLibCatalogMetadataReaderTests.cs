using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ComTypeLibCatalogMetadataReaderTests
{
    [Fact]
    public void ReadMetadataPreservesAnExactCodePageLibraryQualifier()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib("\u00A0"));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Fallback Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Fallback.tlb"));

        Assert.Equal("\u00A0", metadata.QualifierAlias);
        Assert.Empty(metadata.Types);
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageTypeName()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo("\u00A0", TYPEKIND.TKIND_DISPATCH)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        Assert.Equal("\u00A0", Assert.Single(metadata.Types).Name);
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageVariableName()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo("Values", TYPEKIND.TKIND_ENUM, variableName: "\u00A0")));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var type = Assert.Single(metadata.Types);
        Assert.Equal("\u00A0", Assert.Single(type.Members).Name);
    }

    [Fact]
    public void ReadMetadataPreservesUnnamedParameterSlots()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Runner",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Run", "", "日本"])));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var signature = Assert.Single(Assert.Single(metadata.Types).Members).Signature;
        Assert.NotNull(signature);
        Assert.Equal(["Arg1", "日本"], signature.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageFunctionName()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Runner",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["\u00A0"])));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        Assert.Equal("\u00A0", Assert.Single(Assert.Single(metadata.Types).Members).Name);
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageParameterName()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Runner",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Run", "\u00A0"])));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var signature = Assert.Single(Assert.Single(metadata.Types).Members).Signature;
        Assert.NotNull(signature);
        Assert.Equal("\u00A0", Assert.Single(signature.Parameters).Name);
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageUserDefinedTypeName()
    {
        var referencedType = CreateTypeInfo("\u00A0", TYPEKIND.TKIND_RECORD);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Container",
                    TYPEKIND.TKIND_RECORD,
                    variableName: "Value",
                    variableTypeInfo: referencedType)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.NotNull(member.TypeReference);
        Assert.Equal("\u00A0", member.TypeReference.Name);
    }

    [Fact]
    public void ReadMetadataPreservesAnExactCodePageForwardedCoClassName()
    {
        var implementedType = CreateTypeInfo(
            "Events",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Run"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "\u00A0",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: implementedType)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var forwarded = Assert.Single(metadata.Types, type => type.Members.Count > 0);
        Assert.Equal("\u00A0", forwarded.Name);
        Assert.Equal("Run", Assert.Single(forwarded.Members).Name);
    }

    private static ITypeLib CreateTypeLib(
        string libraryName,
        params ITypeInfo[] typeInfos)
    {
        var typeLib = DispatchProxy.Create<ITypeLib, TypeLibProxy>();
        var proxy = (TypeLibProxy)(object)typeLib;
        proxy.LibraryName = libraryName;
        proxy.TypeInfos = typeInfos;
        return typeLib;
    }

    private static ITypeInfo CreateTypeInfo(
        string typeName,
        TYPEKIND typeKind,
        string? variableName = null,
        string[]? functionNames = null,
        ITypeInfo? variableTypeInfo = null,
        ITypeInfo? implementedTypeInfo = null)
    {
        var typeInfo = DispatchProxy.Create<ITypeInfo, TypeInfoProxy>();
        var proxy = (TypeInfoProxy)(object)typeInfo;
        proxy.TypeName = typeName;
        proxy.TypeKind = typeKind;
        proxy.VariableName = variableName;
        proxy.FunctionNames = functionNames;
        proxy.VariableTypeInfo = variableTypeInfo;
        proxy.ImplementedTypeInfo = implementedTypeInfo;
        return typeInfo;
    }

    private class TypeLibProxy : DispatchProxy
    {
        public string LibraryName { get; set; } = string.Empty;

        public IReadOnlyList<ITypeInfo> TypeInfos { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(ITypeLib.GetDocumentation):
                    args[1] = LibraryName;
                    args[2] = string.Empty;
                    args[3] = 0;
                    args[4] = string.Empty;
                    return null;
                case nameof(ITypeLib.GetTypeInfoCount):
                    return TypeInfos.Count;
                case nameof(ITypeLib.GetTypeInfo):
                    args[1] = TypeInfos[(int)args[0]!];
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    private class TypeInfoProxy : DispatchProxy
    {
        private const int VariableMemberId = 42;
        private const int FunctionMemberId = 84;

        public string TypeName { get; set; } = string.Empty;

        public TYPEKIND TypeKind { get; set; }

        public string? VariableName { get; set; }

        public string[]? FunctionNames { get; set; }

        public ITypeInfo? VariableTypeInfo { get; set; }

        public ITypeInfo? ImplementedTypeInfo { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(ITypeInfo.GetTypeAttr):
                    var attributes = new TYPEATTR
                    {
                        typekind = TypeKind,
                        cVars = unchecked((short)(VariableName is null ? 0 : 1)),
                        cFuncs = unchecked((short)(FunctionNames is null ? 0 : 1)),
                        cImplTypes = unchecked((short)(ImplementedTypeInfo is null ? 0 : 1))
                    };
                    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TYPEATTR>());
                    Marshal.StructureToPtr(attributes, pointer, fDeleteOld: false);
                    args[0] = pointer;
                    return null;
                case nameof(ITypeInfo.ReleaseTypeAttr):
                    Marshal.FreeHGlobal((IntPtr)args[0]!);
                    return null;
                case nameof(ITypeInfo.GetDocumentation):
                    args[1] = (int)args[0]! switch
                    {
                        VariableMemberId => VariableName,
                        FunctionMemberId => FunctionNames?[0],
                        _ => TypeName
                    };
                    args[2] = string.Empty;
                    args[3] = 0;
                    args[4] = string.Empty;
                    return null;
                case nameof(ITypeInfo.GetVarDesc):
                    var variable = new VARDESC
                    {
                        memid = VariableMemberId,
                        elemdescVar = new ELEMDESC
                        {
                            tdesc = new TYPEDESC
                            {
                                vt = unchecked((short)(VariableTypeInfo is null
                                    ? VarEnum.VT_I4
                                    : VarEnum.VT_USERDEFINED)),
                                lpValue = VariableTypeInfo is null ? IntPtr.Zero : new IntPtr(7)
                            }
                        }
                    };
                    var variablePointer = Marshal.AllocHGlobal(Marshal.SizeOf<VARDESC>());
                    Marshal.StructureToPtr(variable, variablePointer, fDeleteOld: false);
                    args[1] = variablePointer;
                    return null;
                case nameof(ITypeInfo.ReleaseVarDesc):
                    Marshal.FreeHGlobal((IntPtr)args[0]!);
                    return null;
                case nameof(ITypeInfo.GetFuncDesc):
                    var parameterCount = Math.Max(0, (FunctionNames?.Length ?? 1) - 1);
                    var elementSize = Marshal.SizeOf<ELEMDESC>();
                    var parameterPointer = parameterCount == 0
                        ? IntPtr.Zero
                        : Marshal.AllocHGlobal(elementSize * parameterCount);
                    for (var index = 0; index < parameterCount; index++)
                    {
                        var element = new ELEMDESC
                        {
                            tdesc = new TYPEDESC
                            {
                                vt = unchecked((short)VarEnum.VT_I4)
                            },
                            desc = new ELEMDESC.DESCUNION
                            {
                                paramdesc = new PARAMDESC
                                {
                                    wParamFlags = PARAMFLAG.PARAMFLAG_FIN
                                }
                            }
                        };
                        Marshal.StructureToPtr(
                            element,
                            IntPtr.Add(parameterPointer, index * elementSize),
                            fDeleteOld: false);
                    }

                    var function = new FUNCDESC
                    {
                        memid = FunctionMemberId,
                        lprgelemdescParam = parameterPointer,
                        funckind = FUNCKIND.FUNC_DISPATCH,
                        invkind = INVOKEKIND.INVOKE_FUNC,
                        cParams = unchecked((short)parameterCount),
                        elemdescFunc = new ELEMDESC
                        {
                            tdesc = new TYPEDESC
                            {
                                vt = unchecked((short)VarEnum.VT_VOID)
                            }
                        }
                    };
                    var functionPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FUNCDESC>());
                    Marshal.StructureToPtr(function, functionPointer, fDeleteOld: false);
                    args[1] = functionPointer;
                    return null;
                case nameof(ITypeInfo.ReleaseFuncDesc):
                    var releasedFunction = Marshal.PtrToStructure<FUNCDESC>((IntPtr)args[0]!);
                    if (releasedFunction.lprgelemdescParam != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(releasedFunction.lprgelemdescParam);
                    }

                    Marshal.FreeHGlobal((IntPtr)args[0]!);
                    return null;
                case nameof(ITypeInfo.GetNames):
                    var destination = (string[])args[1]!;
                    var names = FunctionNames ?? [];
                    var count = Math.Min((int)args[2]!, names.Length);
                    Array.Copy(names, destination, count);
                    args[3] = count;
                    return null;
                case nameof(ITypeInfo.GetRefTypeInfo):
                    args[1] = VariableTypeInfo ?? ImplementedTypeInfo
                        ?? throw new InvalidOperationException("No referenced type was configured.");
                    return null;
                case nameof(ITypeInfo.GetImplTypeFlags):
                    args[1] = IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT;
                    return null;
                case nameof(ITypeInfo.GetRefTypeOfImplType):
                    args[1] = 9;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
