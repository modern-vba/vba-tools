using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VbaDev.App.HostClasses;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class HostClassTypeLibEventSurfaceReaderTests
{
    [Fact]
    public void PreservesAnExactCodePageEventNameFromTypeInfo()
    {
        var sourceTypeInfo = CreateSourceTypeInfo(["\u00A0"]);

        var surface = HostClassTypeLibEventSurfaceReader.ReadResolvedTypeInfos(
            sourceTypeInfo,
            defaultInterfaceTypeInfo: null);

        var observed = Assert.Single(surface.Events);
        Assert.Equal("\u00A0", observed.Key);
        Assert.Equal("\u00A0", observed.Value.Name);
    }

    [Fact]
    public void PreservesAnExactCodePageParameterNameFromTypeInfo()
    {
        var sourceTypeInfo = CreateSourceTypeInfo(["Run", "\u00A0"]);

        var surface = HostClassTypeLibEventSurfaceReader.ReadResolvedTypeInfos(
            sourceTypeInfo,
            defaultInterfaceTypeInfo: null);

        var parameter = Assert.Single(Assert.Single(surface.Events).Value.Parameters);
        Assert.Equal("\u00A0", parameter.Name);
    }

    [Fact]
    public void PreservesAnExactCodePageUserDefinedTypeNameFromTypeInfo()
    {
        var referencedTypeInfo = CreateSourceTypeInfo(["\u00A0"]);
        var sourceTypeInfo = CreateSourceTypeInfo(
            ["Run", "value"],
            referencedTypeInfo);

        var surface = HostClassTypeLibEventSurfaceReader.ReadResolvedTypeInfos(
            sourceTypeInfo,
            defaultInterfaceTypeInfo: null);

        var parameter = Assert.Single(Assert.Single(surface.Events).Value.Parameters);
        var type = Assert.IsType<UnresolvedHostEventTypeReference>(parameter.Type);
        Assert.Equal("\u00A0", type.DisplayName);
    }

    [Fact]
    public void PreservesAnExactCodePageBaseTypeNameFromTypeInfo()
    {
        var sourceTypeInfo = CreateSourceTypeInfo(["Run"]);
        var defaultInterfaceTypeInfo = CreateProvenanceTypeInfo("\u00A0");

        var surface = HostClassTypeLibEventSurfaceReader.ReadResolvedTypeInfos(
            sourceTypeInfo,
            defaultInterfaceTypeInfo);

        Assert.NotNull(surface.BaseType);
        Assert.Equal("\u00A0", surface.BaseType.Name);
    }

    private static ITypeInfo CreateSourceTypeInfo(
        string[] names,
        ITypeInfo? referencedTypeInfo = null)
    {
        var typeInfo = DispatchProxy.Create<ITypeInfo, SourceTypeInfoProxy>();
        var proxy = (SourceTypeInfoProxy)(object)typeInfo;
        proxy.Names = names;
        proxy.ReferencedTypeInfo = referencedTypeInfo;
        return typeInfo;
    }

    private static ITypeInfo CreateProvenanceTypeInfo(string name)
    {
        var typeLibrary = DispatchProxy.Create<ITypeLib, TypeLibraryProxy>();
        var typeInfo = DispatchProxy.Create<ITypeInfo, ProvenanceTypeInfoProxy>();
        var proxy = (ProvenanceTypeInfoProxy)(object)typeInfo;
        proxy.Name = name;
        proxy.TypeLibrary = typeLibrary;
        return typeInfo;
    }

    private class SourceTypeInfoProxy : DispatchProxy
    {
        private const int EventMemberId = 42;

        public string[] Names { get; set; } = [];

        public ITypeInfo? ReferencedTypeInfo { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(ITypeInfo.GetTypeAttr):
                    var attributes = new TYPEATTR
                    {
                        typekind = TYPEKIND.TKIND_DISPATCH,
                        cFuncs = 1
                    };
                    var attributePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TYPEATTR>());
                    Marshal.StructureToPtr(attributes, attributePointer, fDeleteOld: false);
                    args[0] = attributePointer;
                    return null;
                case nameof(ITypeInfo.ReleaseTypeAttr):
                    Marshal.FreeHGlobal((IntPtr)args[0]!);
                    return null;
                case nameof(ITypeInfo.GetFuncDesc):
                    var parameterCount = Math.Max(0, Names.Length - 1);
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
                                vt = unchecked((short)(ReferencedTypeInfo is null
                                    ? VarEnum.VT_I4
                                    : VarEnum.VT_USERDEFINED)),
                                lpValue = ReferencedTypeInfo is null ? IntPtr.Zero : new IntPtr(7)
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
                        memid = EventMemberId,
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
                    var count = Math.Min((int)args[2]!, Names.Length);
                    Array.Copy(Names, destination, count);
                    args[3] = count;
                    return null;
                case nameof(ITypeInfo.GetDocumentation):
                    args[1] = Names[0];
                    args[2] = string.Empty;
                    args[3] = 0;
                    args[4] = string.Empty;
                    return null;
                case nameof(ITypeInfo.GetRefTypeInfo):
                    args[1] = ReferencedTypeInfo
                        ?? throw new InvalidOperationException("No referenced type was configured.");
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    private class ProvenanceTypeInfoProxy : DispatchProxy
    {
        public string Name { get; set; } = string.Empty;

        public ITypeLib? TypeLibrary { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(ITypeInfo.GetContainingTypeLib):
                    args[0] = TypeLibrary
                        ?? throw new InvalidOperationException("No TypeLib was configured.");
                    args[1] = 0;
                    return null;
                case nameof(ITypeInfo.GetDocumentation):
                    args[1] = Name;
                    args[2] = string.Empty;
                    args[3] = 0;
                    args[4] = string.Empty;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    private class TypeLibraryProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            switch (targetMethod.Name)
            {
                case nameof(ITypeLib.GetLibAttr):
                    var attributes = new TYPELIBATTR
                    {
                        guid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        wMajorVerNum = 1,
                        wMinorVerNum = 0,
                        lcid = 0
                    };
                    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TYPELIBATTR>());
                    Marshal.StructureToPtr(attributes, pointer, fDeleteOld: false);
                    args[0] = pointer;
                    return null;
                case nameof(ITypeLib.ReleaseTLibAttr):
                    Marshal.FreeHGlobal((IntPtr)args[0]!);
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
