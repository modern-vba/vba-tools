using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ComTypeLibCatalogMetadataReaderTests
{
    [Theory]
    [InlineData(TYPEKIND.TKIND_DISPATCH, (TYPEFLAGS)0, FUNCKIND.FUNC_DISPATCH, true)]
    [InlineData(TYPEKIND.TKIND_INTERFACE, TYPEFLAGS.TYPEFLAG_FOLEAUTOMATION,
        FUNCKIND.FUNC_PUREVIRTUAL, true)]
    [InlineData(TYPEKIND.TKIND_INTERFACE,
        TYPEFLAGS.TYPEFLAG_FDUAL | TYPEFLAGS.TYPEFLAG_FOLEAUTOMATION,
        FUNCKIND.FUNC_VIRTUAL, true)]
    [InlineData(TYPEKIND.TKIND_DISPATCH, TYPEFLAGS.TYPEFLAG_FDUAL,
        FUNCKIND.FUNC_PUREVIRTUAL, true)]
    [InlineData(TYPEKIND.TKIND_INTERFACE, (TYPEFLAGS)0, FUNCKIND.FUNC_PUREVIRTUAL, false)]
    [InlineData(TYPEKIND.TKIND_INTERFACE, TYPEFLAGS.TYPEFLAG_FDISPATCHABLE,
        FUNCKIND.FUNC_PUREVIRTUAL, false)]
    public void InputOnlyPointerVariantDispatchParameterAcceptsDirectStringStorage(
        TYPEKIND typeKind,
        TYPEFLAGS typeFlags,
        FUNCKIND functionKind,
        bool isApplicable)
    {
        const string referenceName = "Fixture";
        var reader = new ComTypeLibCatalogMetadataReader(_ => CreateTypeLib(
            referenceName,
            CreateTypeInfo(
                "Collection",
                typeKind,
                functionNames: ["Put", "key"],
                typeFlags: typeFlags,
                functionParameterVarType: VarEnum.VT_PTR,
                functionParameterElementVarType: VarEnum.VT_VARIANT,
                functionParameterFlags: PARAMFLAG.PARAMFLAG_FIN,
                functionKind: functionKind)));
        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            referenceName,
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1, 0, 0, @"C:\TypeLibs\Fixture.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build(referenceName, metadata);
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog);
        var selection = VbaProjectReferenceSelection.Create("word", [new VbaProjectReference(referenceName)]);
        const string uri = "file:///C:/work/Caller.bas";
        const string source = """
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim values As Fixture.Collection
                Dim key As String
                values.Put key
            End Sub
            """;
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, source);
        var document = VbaSourceDocumentProjector.Project(uri, syntaxTree);
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase) { [uri] = document },
            selection, catalogs);
        var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(
            inventory.ResolveSourceTarget(uri, 4, "    values.".Length));
        Assert.Equal("Put", target.CanonicalName);
        Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.SelectedDefinition.Identity.Origin);
        var nameResolution = new VbaNameResolutionService([document], selection.ToSemanticSelection(), catalogs);
        var callResolution = new VbaCallSiteResolution(
            nameResolution,
            new VbaMemberChainResolution(new VbaTypeResolution(nameResolution)),
            new VbaResolutionPolicy());
        var call = Assert.Single(syntaxTree.Module.ArgumentLists, candidate =>
            candidate.CalleeRange?.Start.Line == 4 && candidate.Form == VbaCallSyntaxForm.Statement);

        var compatibility = callResolution.AnalyzeCompleteCall(document, call, target);

        var variant = Assert.Single(compatibility.Variants);
        Assert.Equal(
            isApplicable
                ? VbaCallCompatibilityState.Applicable
                : VbaCallCompatibilityState.Indeterminate,
            variant.State);
        Assert.Empty(Assert.IsType<VbaCompleteCallArgumentMapping>(variant.Mapping).TypeMismatchReasons);
        Assert.DoesNotContain(inventory.GetProjectValidationDiagnostics(uri), diagnostic =>
            diagnostic.Code == "validation.incompatibleCallArgumentList"
            || diagnostic.Message.Contains("ByRef type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "result", true)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "result", true)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Integer", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Integer", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "(result)", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "(result)", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "1&", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "1&", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, VarEnum.VT_VARIANT, "Dim result As String", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_VARIANT, "Dim result As String", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_I4, null, "Dim result As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_I4, null, "Dim result As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT, VarEnum.VT_PTR, null, "Dim result As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, null, "Dim result As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_NONE, VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result() As Long", "result", false)]
    [InlineData(PARAMFLAG.PARAMFLAG_FIN | PARAMFLAG.PARAMFLAG_FOUT,
        VarEnum.VT_PTR, VarEnum.VT_I4, "Dim result() As Long", "result", false)]
    public void ExternalOutputCompatibilityRequiresProvenWritableStorage(
        PARAMFLAG direction,
        VarEnum parameterVarType,
        VarEnum? parameterElementType,
        string argumentDeclaration,
        string argumentText,
        bool isApplicable)
    {
        const string referenceName = "Fixture";
        var reader = new ComTypeLibCatalogMetadataReader(_ => CreateTypeLib(
            referenceName,
            CreateTypeInfo(
                "Collection",
                TYPEKIND.TKIND_DISPATCH,
                functionNames: ["ReadValue", "value"],
                functionParameterVarType: parameterVarType,
                functionParameterElementVarType: parameterElementType,
                functionParameterFlags: direction)));
        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            referenceName,
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1, 0, 0, @"C:\TypeLibs\Fixture.tlb"));
        var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            TypeLibReferenceCatalogBuilder.Build(referenceName, metadata));
        var selection = VbaProjectReferenceSelection.Create("word", [new VbaProjectReference(referenceName)]);
        const string uri = "file:///C:/work/Caller.bas";
        var source = $$"""
            Attribute VB_Name = "Caller"
            Public Sub Run()
                Dim values As Fixture.Collection
                {{argumentDeclaration}}
                values.ReadValue {{argumentText}}
            End Sub
            """;
        var syntaxTree = VbaSyntaxTree.ParseModule(uri, source);
        var document = VbaSourceDocumentProjector.Project(uri, syntaxTree);
        var inventory = VbaSemanticInventory.Create(
            new Dictionary<string, VbaSourceDocument>(StringComparer.OrdinalIgnoreCase) { [uri] = document },
            selection, catalogs);
        var target = Assert.IsAssignableFrom<VbaResolvedNameTarget>(
            inventory.ResolveSourceTarget(uri, 4, "    values.".Length));
        Assert.Equal("ReadValue", target.CanonicalName);
        Assert.Equal(VbaDefinitionOrigin.ProjectReference, target.SelectedDefinition.Identity.Origin);
        var nameResolution = new VbaNameResolutionService([document], selection.ToSemanticSelection(), catalogs);
        var callResolution = new VbaCallSiteResolution(
            nameResolution,
            new VbaMemberChainResolution(new VbaTypeResolution(nameResolution)),
            new VbaResolutionPolicy());
        var call = Assert.Single(syntaxTree.Module.ArgumentLists, candidate =>
            candidate.CalleeRange?.Start.Line == 4 && candidate.Form == VbaCallSyntaxForm.Statement);

        var compatibility = callResolution.AnalyzeCompleteCall(document, call, target);

        var variant = Assert.Single(compatibility.Variants);
        Assert.Equal(isApplicable ? VbaCallCompatibilityState.Applicable
            : VbaCallCompatibilityState.Indeterminate, variant.State);
        if (parameterVarType == VarEnum.VT_PTR && parameterElementType is null)
        {
            Assert.Null(variant.Mapping);
        }
        else
        {
            Assert.Empty(Assert.IsType<VbaCompleteCallArgumentMapping>(variant.Mapping).TypeMismatchReasons);
        }
        Assert.DoesNotContain(inventory.GetProjectValidationDiagnostics(uri), diagnostic =>
            diagnostic.Code == "validation.incompatibleCallArgumentList"
            || diagnostic.Message.Contains("ByRef type", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedCatalogPreservesTheRawTypeLibProjectNameSeparatelyFromDisplayAndAliasNames()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib("ActualProjectName"));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Human Visible Library Name",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Human Visible Library Name",
            metadata);

        Assert.Equal("ActualProjectName", catalog.ReferencedVbaProjectName);
        Assert.Contains("ActualProjectName", catalog.QualifierAliases);
        Assert.Contains("HumanVisibleLibraryName", catalog.QualifierAliases);
        Assert.Equal("Human Visible Library Name", catalog.ReferenceName);
    }

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
    public void ReadMetadataPreservesTheRawDispatchTypeKind()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo("Events", TYPEKIND.TKIND_DISPATCH)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var type = Assert.Single(metadata.Types);
        Assert.NotNull(type.Metadata);
        Assert.Equal(TypeLibCatalogRawTypeKind.Dispatch, type.Metadata.RawTypeKind);
    }

    [Fact]
    public void ReadMetadataPreservesTheDefaultSourceInterfaceAssociation()
    {
        var sourceInterface = CreateTypeInfo(
            "PublisherEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var coClass = Assert.Single(metadata.Types, type =>
            type.Name == "Publisher"
            && type.Metadata?.RawTypeKind == TypeLibCatalogRawTypeKind.CoClass);
        var implemented = Assert.Single(coClass.Metadata!.ImplementedInterfaces);
        Assert.Equal("PublisherEvents", implemented.Name);
        Assert.Equal(
            (int)(IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
            implemented.ImplementationFlags);
        Assert.Equal("Changed", Assert.Single(implemented.CallableMembers).Name);
    }

    [Fact]
    public void MissingDefaultSourceCallableNameMakesTheEventSurfaceIndeterminate()
    {
        var sourceInterface = CreateTypeInfo(
            "PublisherEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: [""]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
    }

    [Fact]
    public void MissingImplementedInterfaceNameMakesTheEventSurfaceIndeterminate()
    {
        var unnamedSourceInterface = CreateTypeInfo(
            "",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: unnamedSourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                unnamedSourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
    }

    [Fact]
    public void DefaultSourceAssociationToNonInterfaceMakesTheEventSurfaceIndeterminate()
    {
        var invalidSource = CreateTypeInfo(
            "NotAnEventInterface",
            TYPEKIND.TKIND_COCLASS,
            functionNames: ["Changed"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: invalidSource,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                invalidSource));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
        Assert.DoesNotContain(
            catalog.Definitions,
            definition => definition.ParentTypeName == "Publisher"
                && definition.Kind == VbaSourceDefinitionKind.Event);
    }

    [Fact]
    public void MissingParameterDescriptorsMakeTheCallableSurfaceIndeterminate()
    {
        var sourceInterface = CreateTypeInfo(
            "PublisherEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed", "value"],
            hasMissingParameterDescriptors: true);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var publisher = Assert.Single(metadata.Types, type =>
            type.Name == "Publisher"
            && type.Metadata?.RawTypeKind == TypeLibCatalogRawTypeKind.CoClass);
        var defaultSource = Assert.Single(
            publisher.Metadata!.ImplementedInterfaces);
        var member = Assert.Single(defaultSource.CallableMembers);
        Assert.False(member.Metadata?.IsComplete);

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(TypeLibReferenceCatalogBuilder.Build("Library", metadata))
            .GetTypeLibEventSurface("Library", "Publisher");
        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void MissingNestedParameterTypeMarksTheCallableMetadataIncomplete()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "IRunner",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Run", "value"],
                    functionParameterVarType: VarEnum.VT_PTR)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.False(member.Metadata?.IsComplete);
    }

    [Fact]
    public void ConflictingDefaultSourceCallableIdentitiesMakeTheSurfaceIndeterminate()
    {
        var parameterlessEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var parameterizedEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed(ByVal value As Long)",
                [
                    new VbaCallableParameter(
                        "value",
                        TypeReference: new VbaTypeReference("Long"),
                        IsByRef: false)
                ],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 2,
                FunctionFlags: 0));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Library",
            new TypeLibCatalogMetadata(
                "Library",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [parameterlessEvent, parameterizedEvent],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers:
                                        [parameterlessEvent, parameterizedEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void ConflictingDefaultSourceParameterContractsMakeTheSurfaceIndeterminate()
    {
        TypeLibCatalogMember CreateEvent(string parameterType)
            => new(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed(value)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: new VbaTypeReference(parameterType),
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));

        var longEvent = CreateEvent("Long");
        var stringEvent = CreateEvent("String");
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [longEvent, stringEvent],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void ConflictingDefaultSourceParameterDirectionsMakeTheSurfaceIndeterminate()
    {
        TypeLibCatalogMember CreateEvent(VbaTypeLibParameterDirection direction)
            => new(
                "Changed",
                VbaSourceDefinitionKind.Event,
                Documentation: null,
                new VbaCallableSignature(
                    "Event Changed(ByRef value As Long)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: new VbaTypeReference("Long"),
                            IsByRef: true)
                        {
                            TypeLibPassing = new VbaTypeLibParameterPassing(direction, AbiPointerDepth: 1)
                        }
                    ],
                    CallableKind: VbaCallableKind.Event)
                {
                    PassingConvention = VbaCallablePassingConvention.AutomationDispatch
                },
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));

        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers:
                        [
                            CreateEvent(VbaTypeLibParameterDirection.Input),
                            CreateEvent(VbaTypeLibParameterDirection.InputOutput)
                        ],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void EquivalentDefaultSourceParameterContractsIgnorePresentationMetadata()
    {
        TypeLibCatalogMember CreateEvent(
            string signatureLabel,
            string parameterName,
            string documentation)
            => new(
                "Changed",
                VbaSourceDefinitionKind.Event,
                documentation,
                new VbaCallableSignature(
                    signatureLabel,
                    [
                        new VbaCallableParameter(
                            parameterName,
                            Documentation: documentation,
                            DisplayLabel: $"{parameterName} As Long",
                            TypeReference: new VbaTypeReference("Long"),
                            IsByRef: false)
                    ],
                    Documentation: documentation,
                    CallableKind: VbaCallableKind.Event),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));

        var firstEvent = CreateEvent(
            "Event Changed(first As Long)",
            "first",
            "First presentation.");
        var secondEvent = CreateEvent(
            "",
            "",
            "Second presentation.");
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [firstEvent, secondEvent],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Complete, surface.State);
        Assert.Single(surface.StructuralEvents);
    }

    [Fact]
    public void ConflictingDefaultSourceReturnArrayEvidenceMakesTheSurfaceIndeterminate()
    {
        TypeLibCatalogMember CreateEvent(bool isReturnArray)
            => new(
                "Changed",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Function Changed() As Long",
                    [],
                    CallableKind: VbaCallableKind.Function),
                TypeReference: new VbaTypeReference("Long"),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0)
                {
                    IsReturnArray = isReturnArray
                });

        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [CreateEvent(false), CreateEvent(true)],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
    }

    [Fact]
    public void NullDefaultSourceParameterCollectionMakesTheSurfaceIndeterminate()
    {
        var eventWithMissingParameters = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed()",
                Parameters: null!,
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [eventWithMissingParameters],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void NullDefaultSourceParameterMakesTheSurfaceIndeterminate()
    {
        var eventWithMissingParameter = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed(value)",
                Parameters: [null!],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [eventWithMissingParameter],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void IncompleteCallableRetainsCompleteSiblingForExistingHandlerRecognition()
    {
        var completeEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var incompleteEvent = new TypeLibCatalogMember(
            "Unknown",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            Signature: null,
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 2,
                FunctionFlags: 0,
                IsComplete: false));
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [completeEvent, incompleteEvent],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch,
                        IsComplete: false)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Partial, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.AuthoringEvents);
        Assert.Equal(
            "Changed",
            Assert.Single(surface.ExistingHandlerRecognitionEvents).Name);
    }

    [Fact]
    public void DuplicateTypeWithMissingMetadataMakesTheSurfaceIndeterminate()
    {
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces: []));
        var completeType = Assert.Single(catalog.TypeLibTypes!);
        catalog = catalog with
        {
            TypeLibTypes =
            [
                completeType with { Metadata = null },
                completeType
            ]
        };

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void IncompleteNonDefaultAssociationMakesTheSurfaceIndeterminate()
    {
        var completeEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: [completeEvent],
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch),
                    new TypeLibCatalogImplementedInterface(
                        Name: null!,
                        TypeFlags: 0,
                        ImplementationFlags: 0,
                        CallableMembers: [],
                        RawTypeKind: null)
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void NullCatalogCollectionsMakeTheEventSurfaceIndeterminate()
    {
        var missingAssociations = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces: null!));
        var missingCallables = CreateCatalogWithTypeMetadata(
            new TypeLibCatalogTypeMetadata(
                TypeLibCatalogRawTypeKind.CoClass,
                TypeFlags: 0,
                ImplementedInterfaces:
                [
                    new TypeLibCatalogImplementedInterface(
                        "PublisherEvents",
                        TypeFlags: 0,
                        ImplementationFlags: 0x1 | 0x2,
                        CallableMembers: null!,
                        RawTypeKind: TypeLibCatalogRawTypeKind.Dispatch)
                ]));

        foreach (var catalog in new[] { missingAssociations, missingCallables })
        {
            var surface = VbaProjectReferenceCatalogSet.Empty
                .WithCatalog(catalog)
                .GetTypeLibEventSurface("Library", "Publisher");
            Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
            Assert.Empty(surface.StructuralEvents);
            Assert.Empty(surface.ExistingHandlerRecognitionEvents);
        }
    }

    [Fact]
    public void ReadMetadataPreservesCallableMemberIdentityAndFunctionFlags()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "PublisherEvents",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Changed"],
                    functionFlags: FUNCFLAGS.FUNCFLAG_FDEFAULTBIND)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.NotNull(member.Metadata);
        Assert.Equal(84, member.Metadata.MemberId);
        Assert.Equal((int)FUNCFLAGS.FUNCFLAG_FDEFAULTBIND, member.Metadata.FunctionFlags);
    }

    [Fact]
    public void ReadMetadataPreservesFunctionReturnArrayShape()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "IArray",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Values"],
                    functionReturnVarType: VarEnum.VT_SAFEARRAY,
                    functionReturnElementVarType: VarEnum.VT_BSTR)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.Equal(VbaCallableKind.Function, member.Signature?.CallableKind);
        Assert.Equal("String", member.TypeReference?.Name);
        Assert.True(member.Metadata?.IsReturnArray);
    }

    [Fact]
    public void ReadMetadataDoesNotInventVariantForAnUnsupportedFunctionReturnType()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "IReader",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Read"],
                    functionReturnVarType: unchecked((VarEnum)0x7fff))));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.Equal(VbaCallableKind.Function, member.Signature?.CallableKind);
        Assert.Null(member.TypeReference);
    }

    [Fact]
    public void ReadMetadataPreservesAnExplicitVariantFunctionReturnType()
    {
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "IReader",
                    TYPEKIND.TKIND_DISPATCH,
                    functionNames: ["Read"],
                    functionReturnVarType: VarEnum.VT_VARIANT)));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));

        var member = Assert.Single(Assert.Single(metadata.Types).Members);
        Assert.Equal("Variant", member.TypeReference?.Name);
        Assert.False(member.Metadata?.IsReturnArray);
    }

    [Fact]
    public void HiddenDefaultSourceCallablesRemainStructuralButNotAuthoringMembers()
    {
        var sourceInterface = CreateTypeInfo(
            "PublisherEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed"],
            functionFlags: FUNCFLAGS.FUNCFLAG_FHIDDEN);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);
        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Complete, surface.State);
        var structuralEvent = Assert.Single(surface.StructuralEvents);
        Assert.Equal("Changed", structuralEvent.Name);
        Assert.Equal(
            (int)FUNCFLAGS.FUNCFLAG_FHIDDEN,
            structuralEvent.Metadata?.FunctionFlags);
        var existingHandlerDefinition = Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "Changed"
                && definition.Kind == VbaSourceDefinitionKind.Event);
        Assert.False(existingHandlerDefinition.IsAuthoringAvailable);
    }

    [Fact]
    public void EventWithoutCompleteCallableMetadataIsNotAuthoringAvailable()
    {
        var incompleteEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            "Incomplete Event metadata.",
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Library",
            new TypeLibCatalogMetadata(
                "Library",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [incompleteEvent],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [incompleteEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));

        var eventDefinition = Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "Changed"
                && definition.Kind == VbaSourceDefinitionKind.Event);

        Assert.False(eventDefinition.IsAuthoringAvailable);
        Assert.Equal(
            VbaTypeLibEventSurfaceState.Indeterminate,
            VbaProjectReferenceCatalogSet.Empty
                .WithCatalog(catalog)
                .GetTypeLibEventSurface("Library", "Publisher")
                .State);
    }

    [Fact]
    public void DefaultSourceFunctionWithoutResultEvidenceIsNotACompleteEventSurface()
    {
        var incompleteFunction = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Procedure,
            Documentation: null,
            new VbaCallableSignature(
                "Function Changed()",
                [],
                CallableKind: VbaCallableKind.Function),
            TypeReference: null,
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Library",
            new TypeLibCatalogMetadata(
                "Library",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [incompleteFunction],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.AuthoringEvents);
    }

    [Fact]
    public void DefaultSourceParameterWithoutTypeEvidenceMakesOnlyTheKnownEventRecognizable()
    {
        var knownEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Procedure,
            Documentation: null,
            new VbaCallableSignature(
                "Sub Changed()",
                [],
                CallableKind: VbaCallableKind.Sub),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var incompleteEvent = new TypeLibCatalogMember(
            "Broken",
            VbaSourceDefinitionKind.Procedure,
            Documentation: null,
            new VbaCallableSignature(
                "Sub Broken(value)",
                [new VbaCallableParameter("value")],
                CallableKind: VbaCallableKind.Sub),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 2,
                FunctionFlags: 0));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Library",
            new TypeLibCatalogMetadata(
                "Library",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [knownEvent, incompleteEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ]))
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Partial, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.Empty(surface.AuthoringEvents);
        Assert.Equal(
            "Changed",
            Assert.Single(surface.ExistingHandlerRecognitionEvents).Name);
    }

    [Fact]
    public void IncompleteTypeMetadataDoesNotRetainKnownHandlerAssociations()
    {
        var knownEvent = new TypeLibCatalogMember(
            "Changed",
            VbaSourceDefinitionKind.Event,
            Documentation: null,
            new VbaCallableSignature(
                "Event Changed()",
                [],
                CallableKind: VbaCallableKind.Event),
            Metadata: new TypeLibCatalogCallableMetadata(
                MemberId: 1,
                FunctionFlags: 0));
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Library",
            new TypeLibCatalogMetadata(
                "Library",
                [
                    new TypeLibCatalogType(
                        "Publisher",
                        VbaSourceDefinitionKind.Class,
                        Documentation: null,
                        Members: [knownEvent],
                        IsCreatable: true,
                        Metadata: new TypeLibCatalogTypeMetadata(
                            TypeLibCatalogRawTypeKind.CoClass,
                            TypeFlags: 0,
                            ImplementedInterfaces:
                            [
                                new TypeLibCatalogImplementedInterface(
                                    "PublisherEvents",
                                    TypeFlags: 0,
                                    ImplementationFlags: 0x1 | 0x2,
                                    CallableMembers: [knownEvent],
                                    RawTypeKind:
                                        TypeLibCatalogRawTypeKind.Dispatch)
                            ],
                            IsComplete: false))
                ]));

        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.Empty(surface.ExistingHandlerRecognitionEvents);
    }

    [Fact]
    public void HiddenCoClassRemainsExplicitlyResolvableButNotAuthoringAvailable()
    {
        var sourceInterface = CreateTypeInfo(
            "PublisherEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "HiddenPublisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags:
                        IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
                        | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE,
                    typeFlags: TYPEFLAGS.TYPEFLAG_FHIDDEN),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);

        var type = Assert.Single(metadata.Types, candidate =>
            candidate.Name == "HiddenPublisher"
            && candidate.Metadata?.RawTypeKind == TypeLibCatalogRawTypeKind.CoClass);
        Assert.False(type.IsBrowsable);
        var definition = Assert.Single(catalog.Definitions, candidate =>
            candidate.Name == "HiddenPublisher"
            && candidate.Kind == VbaSourceDefinitionKind.Class);
        Assert.False(definition.IsAuthoringAvailable);
        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "HiddenPublisher");
        Assert.Equal(VbaTypeLibEventSurfaceState.Complete, surface.State);
        Assert.Equal("Changed", Assert.Single(surface.StructuralEvents).Name);
    }

    [Fact]
    public void NonDefaultSourceInterfaceIsNotProjectedAsACoClassEvent()
    {
        var sourceInterface = CreateTypeInfo(
            "SecondaryEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["Changed"]);
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypeInfo: sourceInterface,
                    implementationFlags: IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE),
                sourceInterface));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);
        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Complete, surface.State);
        Assert.Empty(surface.StructuralEvents);
        Assert.DoesNotContain(
            catalog.Definitions,
            definition => definition.ParentTypeName == "Publisher"
                && definition.Kind == VbaSourceDefinitionKind.Event);
    }

    [Fact]
    public void MultipleDefaultSourceInterfacesFailClosedWithoutEventProjection()
    {
        var firstSource = CreateTypeInfo(
            "FirstEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["FirstChanged"]);
        var secondSource = CreateTypeInfo(
            "SecondEvents",
            TYPEKIND.TKIND_DISPATCH,
            functionNames: ["SecondChanged"]);
        var defaultSourceFlags =
            IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT
            | IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE;
        var reader = new ComTypeLibCatalogMetadataReader(
            _ => CreateTypeLib(
                "Library",
                CreateTypeInfo(
                    "Publisher",
                    TYPEKIND.TKIND_COCLASS,
                    implementedTypes:
                    [
                        new ImplementedType(firstSource, defaultSourceFlags),
                        new ImplementedType(secondSource, defaultSourceFlags)
                    ]),
                firstSource,
                secondSource));

        var metadata = reader.ReadMetadata(new VbaProjectReferenceCatalogIdentity(
            "Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0,
            0,
            @"C:\TypeLibs\Library.tlb"));
        var catalog = TypeLibReferenceCatalogBuilder.Build("Library", metadata);
        var surface = VbaProjectReferenceCatalogSet.Empty
            .WithCatalog(catalog)
            .GetTypeLibEventSurface("Library", "Publisher");

        Assert.Equal(VbaTypeLibEventSurfaceState.Indeterminate, surface.State);
        Assert.DoesNotContain(
            catalog.Definitions,
            definition => definition.ParentTypeName == "Publisher"
                && definition.Kind == VbaSourceDefinitionKind.Event);
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

    private static VbaProjectReferenceCatalog CreateCatalogWithTypeMetadata(
        TypeLibCatalogTypeMetadata metadata)
        => new(
            "Library",
            ["Library"],
            [
                new VbaProjectReferenceDefinition(
                    "Library",
                    "Publisher",
                    VbaSourceDefinitionKind.Class)
            ],
            [
                new TypeLibCatalogType(
                    "Publisher",
                    VbaSourceDefinitionKind.Class,
                    Documentation: null,
                    Members: [],
                    Metadata: metadata)
            ]);

    private static ITypeInfo CreateTypeInfo(
        string typeName,
        TYPEKIND typeKind,
        string? variableName = null,
        string[]? functionNames = null,
        ITypeInfo? variableTypeInfo = null,
        ITypeInfo? implementedTypeInfo = null,
        IMPLTYPEFLAGS implementationFlags = IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT,
        FUNCFLAGS functionFlags = 0,
        IReadOnlyList<ImplementedType>? implementedTypes = null,
        TYPEFLAGS typeFlags = 0,
        bool hasMissingParameterDescriptors = false,
        VarEnum functionReturnVarType = VarEnum.VT_VOID,
        VarEnum? functionReturnElementVarType = null,
        VarEnum functionParameterVarType = VarEnum.VT_I4,
        VarEnum? functionParameterElementVarType = null,
        PARAMFLAG functionParameterFlags = PARAMFLAG.PARAMFLAG_FIN,
        FUNCKIND functionKind = FUNCKIND.FUNC_DISPATCH)
    {
        var typeInfo = DispatchProxy.Create<ITypeInfo, TypeInfoProxy>();
        var proxy = (TypeInfoProxy)(object)typeInfo;
        proxy.TypeName = typeName;
        proxy.TypeKind = typeKind;
        proxy.TypeFlags = typeFlags;
        proxy.VariableName = variableName;
        proxy.FunctionNames = functionNames;
        proxy.VariableTypeInfo = variableTypeInfo;
        proxy.ImplementedTypes = implementedTypes
            ?? (implementedTypeInfo is null
                ? []
                : [new ImplementedType(implementedTypeInfo, implementationFlags)]);
        proxy.FunctionFlags = functionFlags;
        proxy.HasMissingParameterDescriptors = hasMissingParameterDescriptors;
        proxy.FunctionReturnVarType = functionReturnVarType;
        proxy.FunctionReturnElementVarType = functionReturnElementVarType;
        proxy.FunctionParameterVarType = functionParameterVarType;
        proxy.FunctionParameterElementVarType = functionParameterElementVarType;
        proxy.FunctionParameterFlags = functionParameterFlags;
        proxy.FunctionKind = functionKind;
        return typeInfo;
    }

    private sealed record ImplementedType(
        ITypeInfo TypeInfo,
        IMPLTYPEFLAGS Flags);

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

        public TYPEFLAGS TypeFlags { get; set; }

        public string? VariableName { get; set; }

        public string[]? FunctionNames { get; set; }

        public ITypeInfo? VariableTypeInfo { get; set; }

        public IReadOnlyList<ImplementedType> ImplementedTypes { get; set; } = [];

        public FUNCFLAGS FunctionFlags { get; set; }

        public FUNCKIND FunctionKind { get; set; } = FUNCKIND.FUNC_DISPATCH;

        public bool HasMissingParameterDescriptors { get; set; }

        public VarEnum FunctionReturnVarType { get; set; } = VarEnum.VT_VOID;

        public VarEnum? FunctionReturnElementVarType { get; set; }

        public VarEnum FunctionParameterVarType { get; set; } = VarEnum.VT_I4;

        public VarEnum? FunctionParameterElementVarType { get; set; }

        public PARAMFLAG FunctionParameterFlags { get; set; } = PARAMFLAG.PARAMFLAG_FIN;

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
                        wTypeFlags = TypeFlags,
                        cVars = unchecked((short)(VariableName is null ? 0 : 1)),
                        cFuncs = unchecked((short)(FunctionNames is null ? 0 : 1)),
                        cImplTypes = unchecked((short)ImplementedTypes.Count)
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
                            || HasMissingParameterDescriptors
                        ? IntPtr.Zero
                        : Marshal.AllocHGlobal(elementSize * parameterCount);
                    for (var index = 0;
                        parameterPointer != IntPtr.Zero && index < parameterCount;
                        index++)
                    {
                        var parameterType = new TYPEDESC
                        {
                            vt = unchecked((short)FunctionParameterVarType)
                        };
                        if (FunctionParameterElementVarType is { } parameterElementVarType)
                        {
                            var parameterTypePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TYPEDESC>());
                            Marshal.StructureToPtr(
                                new TYPEDESC { vt = unchecked((short)parameterElementVarType) },
                                parameterTypePointer,
                                fDeleteOld: false);
                            parameterType.lpValue = parameterTypePointer;
                        }
                        var element = new ELEMDESC
                        {
                            tdesc = parameterType,
                            desc = new ELEMDESC.DESCUNION
                            {
                                paramdesc = new PARAMDESC
                                {
                                    wParamFlags = FunctionParameterFlags
                                }
                            }
                        };
                        Marshal.StructureToPtr(
                            element,
                            IntPtr.Add(parameterPointer, index * elementSize),
                            fDeleteOld: false);
                    }

                    var functionReturnType = new TYPEDESC
                    {
                        vt = unchecked((short)FunctionReturnVarType)
                    };
                    if (FunctionReturnElementVarType is { } elementVarType)
                    {
                        var elementPointer = Marshal.AllocHGlobal(
                            Marshal.SizeOf<TYPEDESC>());
                        Marshal.StructureToPtr(
                            new TYPEDESC
                            {
                                vt = unchecked((short)elementVarType)
                            },
                            elementPointer,
                            fDeleteOld: false);
                        functionReturnType.lpValue = elementPointer;
                    }

                    var function = new FUNCDESC
                    {
                        memid = FunctionMemberId,
                        lprgelemdescParam = parameterPointer,
                        funckind = FunctionKind,
                        invkind = INVOKEKIND.INVOKE_FUNC,
                        cParams = unchecked((short)parameterCount),
                        wFuncFlags = unchecked((short)FunctionFlags),
                        elemdescFunc = new ELEMDESC
                        {
                            tdesc = functionReturnType
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
                        for (var index = 0; index < releasedFunction.cParams; index++)
                        {
                            var parameter = Marshal.PtrToStructure<ELEMDESC>(IntPtr.Add(
                                releasedFunction.lprgelemdescParam, index * Marshal.SizeOf<ELEMDESC>()));
                            if (parameter.tdesc.lpValue != IntPtr.Zero)
                            {
                                Marshal.FreeHGlobal(parameter.tdesc.lpValue);
                            }
                        }
                        Marshal.FreeHGlobal(releasedFunction.lprgelemdescParam);
                    }

                    if (releasedFunction.elemdescFunc.tdesc.lpValue
                        != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(
                            releasedFunction.elemdescFunc.tdesc.lpValue);
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
                    var href = (int)args[0]!;
                    args[1] = href == 7 && VariableTypeInfo is not null
                        ? VariableTypeInfo
                        : ImplementedTypes[href - 9].TypeInfo;
                    return null;
                case nameof(ITypeInfo.GetImplTypeFlags):
                    args[1] = ImplementedTypes[(int)args[0]!].Flags;
                    return null;
                case nameof(ITypeInfo.GetRefTypeOfImplType):
                    args[1] = 9 + (int)args[0]!;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
