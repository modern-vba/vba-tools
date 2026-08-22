using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogRefreshTests
{
    [Fact]
    public async Task TypeLibDiscoveryResolvesHighestVersionFromOneNeutralGuidLineage()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(
                new TypeLibRegistryCatalog(
                    complete: true,
                    names:
                    [
                        new TypeLibRegistryCatalogName(
                            "Custom Library",
                            [
                                new TypeLibRegistryLineage(
                                    "11111111-1111-1111-1111-111111111111",
                                    [
                                        new TypeLibRegistryVersion(
                                            16,
                                            0,
                                            [
                                                new TypeLibRegistryLocale(
                                                    0,
                                                    [new TypeLibRegistryPath("win32", @"C:\TypeLibs\Custom16.tlb")])
                                            ]),
                                        new TypeLibRegistryVersion(
                                            1,
                                            0,
                                            [
                                                new TypeLibRegistryLocale(
                                                    0,
                                                    [new TypeLibRegistryPath("win32", @"C:\TypeLibs\Custom1.tlb")])
                                            ])
                                    ])
                            ])
                    ],
                    warnings: [],
                    diagnostic: null)),
            new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Custom", [])));

        var result = await discovery.DiscoverAsync(" custom library ");

        var identity = Assert.Single(result.Identities);
        Assert.True(result.HasUsableCatalog);
        Assert.Equal("Custom Library", identity.ReferenceName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", identity.Guid);
        Assert.Equal(16, identity.MajorVersion);
        Assert.Equal(0, identity.MinorVersion);
        Assert.Equal(@"C:\TypeLibs\Custom16.tlb", identity.Path);
    }

    [Fact]
    public async Task TypeLibDiscoveryTriesEveryLocationForTheUniqueNeutralIdentity()
    {
        const string availablePath = @"C:\TypeLibs\English64.tlb";
        var metadataReader = new PathFallbackTypeLibCatalogMetadataReader(availablePath);
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(
                new TypeLibRegistryCatalog(
                    complete: true,
                    names:
                    [
                        new TypeLibRegistryCatalogName(
                            "Custom Library",
                            [
                                new TypeLibRegistryLineage(
                                    "11111111-1111-1111-1111-111111111111",
                                    [
                                        new TypeLibRegistryVersion(
                                            1,
                                            0,
                                            [
                                                new TypeLibRegistryLocale(
                                                    0,
                                                    [
                                                        new TypeLibRegistryPath("win64", @"C:\TypeLibs\Neutral64.tlb"),
                                                        new TypeLibRegistryPath("win32", @"C:\TypeLibs\Neutral32.tlb")
                                                    ]),
                                                new TypeLibRegistryLocale(
                                                    0x409,
                                                    [
                                                        new TypeLibRegistryPath("win64", availablePath),
                                                        new TypeLibRegistryPath("win32", @"C:\TypeLibs\English32.tlb")
                                                    ])
                                            ])
                                    ])
                            ])
                    ],
                    warnings: [],
                    diagnostic: null)),
            metadataReader);

        var result = await discovery.DiscoverAsync("Custom Library");

        var identity = Assert.Single(result.Identities);
        Assert.True(result.HasUsableCatalog);
        Assert.Equal(0x409, identity.Lcid);
        Assert.Equal(availablePath, identity.Path);
        Assert.Equal(
            [
                @"C:\TypeLibs\Neutral32.tlb",
                @"C:\TypeLibs\Neutral64.tlb",
                @"C:\TypeLibs\English32.tlb",
                availablePath
            ],
            metadataReader.AttemptedPaths);
    }

    [Fact]
    public async Task ExplicitCatalogRetryReadsOneFreshNeutralRegistrySnapshotForTheBatch()
    {
        var registryReader = new SequencedTypeLibRegistryCatalogReader(
            new TypeLibRegistryCatalog(
                complete: false,
                names: [],
                warnings: [],
                diagnostic: new TypeLibRegistryCatalogDiagnostic(
                    "registryCatalogIncomplete",
                    "The first registry scan did not complete.")),
            CreateNeutralRegistryCatalog("Library A", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CreateNeutralRegistryCatalog("Library B", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new TypeLibReferenceCatalogDiscovery(
                registryReader,
                new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Custom", []))));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Library A"), new VbaProjectReference("Library B")]);

        var first = await service.RefreshAsync(selection);
        var second = await service.RefreshAsync(selection);
        var cached = await service.RefreshAsync(selection);

        Assert.Equal(2, first.Count);
        Assert.All(first, result => Assert.True(result.DiscoveryResult.IsFailure));
        Assert.Equal(2, second.Count);
        Assert.All(second, result => Assert.True(result.DiscoveryResult.HasUsableCatalog));
        Assert.Empty(cached);
        Assert.Equal(2, registryReader.ReadCount);
        Assert.True(cache.HasIdentity("Library A"));
        Assert.True(cache.HasIdentity("Library B"));
    }

    [Fact]
    public async Task CatalogRefreshActivatesCanonicalNeutralCatalogForTrimmedManifestLookup()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new TypeLibReferenceCatalogDiscovery(
                new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                    "Custom Library",
                    "11111111-1111-1111-1111-111111111111")),
                new FakeTypeLibCatalogMetadataReader(
                    new TypeLibCatalogMetadata(
                        "Custom",
                        [
                            new TypeLibCatalogType(
                                "CustomType",
                                VbaSourceDefinitionKind.Class,
                                null,
                                [])
                        ]))));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(" custom library ")]);

        var results = await service.RefreshAsync(selection);

        Assert.True(Assert.Single(results).DiscoveryResult.HasUsableCatalog);
        Assert.True(cache.HasIdentity(" custom library "));
        Assert.True(cache.Current.HasCatalog(" custom library "));
        Assert.Contains(
            cache.Current.GetActiveDefinitions(selection),
            definition => definition.Name == "CustomType");
    }

    [Fact]
    public void ReferenceSelectionPreservesSpellingWhileMatchingTrimmedMainReferenceName()
    {
        const string storedName = " Microsoft Excel 16.0 Object Library ";

        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(storedName)]);

        Assert.Equal(storedName, selection.MainVbaProjectReference?.Name);
        Assert.Null(selection.MissingExpectedMainReference);
    }

    [Fact]
    public async Task NeutralCatalogRefreshPreservesLastKnownGoodPerReference()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(CreateReferenceCatalog("Library A", "AKnownType"));
        cache.StoreStaleCatalog(CreateReferenceCatalog("Library B", "BOldType"));
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new TypeLibReferenceCatalogDiscovery(
                new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                    "Library B",
                    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
                new FakeTypeLibCatalogMetadataReader(
                    new TypeLibCatalogMetadata(
                        "LibraryB",
                        [
                            new TypeLibCatalogType(
                                "BFreshType",
                                VbaSourceDefinitionKind.Class,
                                null,
                                [])
                        ]))));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Library A"), new VbaProjectReference("Library B")]);

        var results = await service.RefreshAsync(selection);

        Assert.True(results.Single(result => result.ReferenceName == "Library A").DiscoveryResult.IsFailure);
        Assert.True(results.Single(result => result.ReferenceName == "Library B").DiscoveryResult.HasUsableCatalog);
        Assert.Equal(VbaProjectReferenceCatalogSource.StalePersisted, cache.GetCatalogSource("Library A"));
        Assert.Equal(VbaProjectReferenceCatalogSource.Generated, cache.GetCatalogSource("Library B"));
        var activeNames = cache.Current.GetActiveDefinitions(selection)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("AKnownType", activeNames);
        Assert.Contains("BFreshType", activeNames);
        Assert.DoesNotContain("BOldType", activeNames);
    }

    [Fact]
    public void TypeLibCatalogBuilderMarksCallableSignaturesAsSupportingNamedArguments()
    {
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Generated Library",
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "GeneratedType",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [
                            new TypeLibCatalogMember(
                                "GeneratedMethod",
                                VbaSourceDefinitionKind.Procedure,
                                null,
                                new VbaCallableSignature(
                                    "GeneratedMethod(Value)",
                                    [new VbaCallableParameter("Value")],
                                    CallableKind: VbaCallableKind.Function))
                        ])
                ]));

        var callable = Assert.Single(
            catalog.Definitions,
            definition => definition.Name == "GeneratedMethod");
        Assert.True(callable.Signature?.SupportsNamedArguments);
    }

    [Fact]
    public void TypeLibCatalogBuilderUsesExplicitBindingMetadataForGlobalExposure()
    {
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Generated Library",
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "Application",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [new TypeLibCatalogMember("ActiveItem", VbaSourceDefinitionKind.Property, null)],
                        IsApplicationObject: true),
                    new TypeLibCatalogType(
                        "GlobalModule",
                        VbaSourceDefinitionKind.Module,
                        null,
                        [new TypeLibCatalogMember("LibraryValue", VbaSourceDefinitionKind.Property, null)]),
                    new TypeLibCatalogType(
                        "GeneratedConstants",
                        VbaSourceDefinitionKind.Enum,
                        null,
                        [new TypeLibCatalogMember("generatedCenter", VbaSourceDefinitionKind.EnumMember, null)]),
                    new TypeLibCatalogType(
                        "_Global",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [new TypeLibCatalogMember("NameOnlyValue", VbaSourceDefinitionKind.Property, null)]),
                    new TypeLibCatalogType(
                        "OrdinaryType",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [new TypeLibCatalogMember("OrdinaryValue", VbaSourceDefinitionKind.Property, null)]),
                    new TypeLibCatalogType(
                        "_Application",
                        VbaSourceDefinitionKind.Class,
                        null,
                        [new TypeLibCatalogMember("ExplicitHiddenOwnerGlobal", VbaSourceDefinitionKind.Property, null)],
                        IsApplicationObject: true,
                        IsBrowsable: false)
                ]));

        Assert.Equal(
            ReferenceDefinitionGlobalExposure.MainHostGlobal,
            Assert.Single(catalog.Definitions, definition => definition.Name == "ActiveItem").GlobalExposure);
        Assert.Equal(
            ReferenceDefinitionGlobalExposure.LibraryGlobal,
            Assert.Single(catalog.Definitions, definition => definition.Name == "LibraryValue").GlobalExposure);
        Assert.Equal(
            ReferenceDefinitionGlobalExposure.LibraryGlobal,
            Assert.Single(catalog.Definitions, definition => definition.Name == "generatedCenter").GlobalExposure);
        Assert.Equal(
            ReferenceDefinitionGlobalExposure.None,
            Assert.Single(catalog.Definitions, definition => definition.Name == "NameOnlyValue").GlobalExposure);
        Assert.Equal(
            ReferenceDefinitionGlobalExposure.None,
            Assert.Single(catalog.Definitions, definition => definition.Name == "OrdinaryValue").GlobalExposure);
        Assert.DoesNotContain(catalog.Definitions, definition => definition.Name == "_Application");
        Assert.Equal(
            ReferenceDefinitionGlobalExposure.MainHostGlobal,
            Assert.Single(
                catalog.Definitions,
                definition => definition.Name == "ExplicitHiddenOwnerGlobal").GlobalExposure);
    }

    [Fact]
    public void ComTypeLibMetadataUsesApplicationObjectFlagsAndModuleKinds()
    {
        Assert.True(ComTypeLibCatalogMetadataReader.IsApplicationObjectType(
            TYPEFLAGS.TYPEFLAG_FAPPOBJECT | TYPEFLAGS.TYPEFLAG_FHIDDEN));
        Assert.False(ComTypeLibCatalogMetadataReader.IsApplicationObjectType(
            TYPEFLAGS.TYPEFLAG_FHIDDEN));
        Assert.Equal(
            VbaSourceDefinitionKind.Module,
            ComTypeLibCatalogMetadataReader.GetTypeDefinitionKind(TYPEKIND.TKIND_MODULE));
    }

    [Fact]
    public void ComTypeLibMetadataSuppressesHiddenRestrictedAndNonBrowsableEntries()
    {
        Assert.False(ComTypeLibCatalogMetadataReader.IsBrowsableType(TYPEFLAGS.TYPEFLAG_FHIDDEN));
        Assert.False(ComTypeLibCatalogMetadataReader.IsBrowsableType(TYPEFLAGS.TYPEFLAG_FRESTRICTED));
        Assert.False(ComTypeLibCatalogMetadataReader.IsBrowsableFunction(FUNCFLAGS.FUNCFLAG_FNONBROWSABLE));
        Assert.False(ComTypeLibCatalogMetadataReader.IsBrowsableVariable(VARFLAGS.VARFLAG_FRESTRICTED));
        Assert.True(ComTypeLibCatalogMetadataReader.IsBrowsableType(0));
        Assert.True(ComTypeLibCatalogMetadataReader.IsBrowsableFunction(0));
        Assert.True(ComTypeLibCatalogMetadataReader.IsBrowsableVariable(0));
    }

    [Fact]
    public void TypeLibCatalogDeduplicationPreservesTheBroadestExplicitExposure()
    {
        var catalog = TypeLibReferenceCatalogBuilder.Build(
            "Generated Library",
            new TypeLibCatalogMetadata(
                "Generated",
                [
                    new TypeLibCatalogType(
                        "Globals",
                        VbaSourceDefinitionKind.Module,
                        null,
                        [new TypeLibCatalogMember("SharedValue", VbaSourceDefinitionKind.Property, null)]),
                    new TypeLibCatalogType(
                        "Globals",
                        VbaSourceDefinitionKind.Module,
                        null,
                        [new TypeLibCatalogMember("SharedValue", VbaSourceDefinitionKind.Property, null)],
                        IsApplicationObject: true)
                ]));

        Assert.Equal(
            ReferenceDefinitionGlobalExposure.LibraryGlobal,
            Assert.Single(catalog.Definitions, definition => definition.Name == "SharedValue").GlobalExposure);
    }

    [Fact]
    public void TypeLibCallableKindUsesReturnValueParameterPresenceWhenItsTypeIsUnavailable()
    {
        var callableKind = ComTypeLibCatalogMetadataReader.GetCallableKind(
            INVOKEKIND.INVOKE_FUNC,
            VarEnum.VT_HRESULT,
            hasResolvedReturnType: false,
            hasReturnValueParameter: true);

        Assert.Equal(VbaCallableKind.Function, callableKind);
    }

    [Fact]
    public async Task TypeLibDiscoveryResolvesReferenceCatalogIdentity()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                "Custom Library",
                "11111111-1111-1111-1111-111111111111",
                minor: 2,
                path: @"C:\TypeLibs\Custom.tlb")),
            new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Custom", [])));

        var result = await discovery.DiscoverAsync("custom library");

        Assert.False(result.IsFailure);
        Assert.False(result.IsAmbiguous);
        var identity = Assert.Single(result.Identities);
        Assert.Equal("Custom Library", identity.ReferenceName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", identity.Guid);
        Assert.Equal(1, identity.MajorVersion);
        Assert.Equal(2, identity.MinorVersion);
        Assert.Equal(0, identity.Lcid);
        Assert.Equal(@"C:\TypeLibs\Custom.tlb", identity.Path);
    }

    [Fact]
    public async Task TypeLibDiscoveryReportsAmbiguousMatchesInsteadOfGuessing()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(
                new TypeLibRegistryCatalog(
                    complete: true,
                    names:
                    [
                        new TypeLibRegistryCatalogName(
                            "Ambiguous Library",
                            [
                                CreateNeutralRegistryCatalog(
                                    "Ambiguous Library",
                                    "11111111-1111-1111-1111-111111111111").Names[0].Lineages[0],
                                CreateNeutralRegistryCatalog(
                                    "Ambiguous Library",
                                    "22222222-2222-2222-2222-222222222222").Names[0].Lineages[0]
                            ])
                    ],
                    warnings: [],
                    diagnostic: null)));

        var result = await discovery.DiscoverAsync("Ambiguous Library");

        Assert.True(result.IsAmbiguous);
        Assert.False(result.HasUsableCatalog);
        Assert.Equal(2, result.Identities.Count);
    }

    [Fact]
    public async Task TypeLibDiscoveryBuildsReferenceCatalogMetadataForRepresentativeReference()
    {
        var discovery = CreateRegExpDiscovery();

        var result = await discovery.DiscoverAsync("Microsoft VBScript Regular Expressions 5.5");

        Assert.False(result.IsFailure);
        var catalog = Assert.IsType<VbaProjectReferenceCatalog>(result.Catalog);
        Assert.Contains("VBScript_RegExp_55", catalog.QualifierAliases);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "RegExp"
            && definition.Kind == VbaSourceDefinitionKind.Class
            && definition.ParentTypeName is null);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Pattern"
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.ParentTypeName == "RegExp"
            && definition.TypeReference?.Name == "String");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Execute"
            && definition.Kind == VbaSourceDefinitionKind.Procedure
            && definition.ParentTypeName == "RegExp"
            && definition.Signature?.Label == "Execute(String) As MatchCollection");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "RegExpError"
            && definition.Kind == VbaSourceDefinitionKind.Enum);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "SyntaxError"
            && definition.Kind == VbaSourceDefinitionKind.EnumMember
            && definition.ParentTypeName == "RegExpError");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "ExecuteComplete"
            && definition.Kind == VbaSourceDefinitionKind.Event
            && definition.ParentTypeName == "RegExpEvents");
    }

    [Fact]
    public async Task ComTypeLibCatalogMetadataReaderReadsRegisteredRegExpMetadataWhenAvailable()
    {
        const string referenceName = "Microsoft VBScript Regular Expressions 5.5";
        var registryCatalog = new RegistryTypeLibRegistryCatalogReader().Read();
        if (!registryCatalog.Complete || registryCatalog.Find(referenceName) is null)
        {
            return;
        }

        var result = await new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(registryCatalog)).DiscoverAsync(referenceName);
        var catalog = Assert.IsType<VbaProjectReferenceCatalog>(result.Catalog);

        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "RegExp"
            && definition.Kind == VbaSourceDefinitionKind.Class);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Pattern"
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.ParentTypeName == "RegExp");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Execute"
            && definition.Kind == VbaSourceDefinitionKind.Procedure
            && definition.ParentTypeName == "RegExp");
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("major")]
    [InlineData("minor")]
    public async Task ComTypeLibCatalogMetadataReaderRejectsMismatchedRegisteredIdentityWhenAvailable(
        string mismatchedComponent)
    {
        const string referenceName = "Microsoft VBScript Regular Expressions 5.5";
        var registryCatalog = new RegistryTypeLibRegistryCatalogReader().Read();
        if (!OperatingSystem.IsWindows()
            || !registryCatalog.Complete
            || registryCatalog.Find(referenceName) is null)
        {
            return;
        }

        var resolved = await new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(registryCatalog),
            new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("VBScript_RegExp_55", [])))
            .DiscoverAsync(referenceName);
        var registeredIdentity = Assert.Single(resolved.Identities);
        var mismatchedIdentity = mismatchedComponent switch
        {
            "guid" => registeredIdentity with
            {
                Guid = "00000000-0000-0000-0000-000000000000"
            },
            "major" => registeredIdentity with
            {
                MajorVersion = registeredIdentity.MajorVersion + 1
            },
            "minor" => registeredIdentity with
            {
                MinorVersion = registeredIdentity.MinorVersion + 1
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchedComponent))
        };

        Assert.Throws<InvalidDataException>(
            () => new ComTypeLibCatalogMetadataReader().ReadMetadata(mismatchedIdentity));
    }

    [Fact]
    public async Task ComTypeLibCatalogMetadataReaderReadsRegisteredExcelWorkbookMetadataWhenAvailable()
    {
        const string referenceName = "Microsoft Excel 16.0 Object Library";
        var registryCatalog = new RegistryTypeLibRegistryCatalogReader().Read();
        if (!registryCatalog.Complete || registryCatalog.Find(referenceName) is null)
        {
            return;
        }

        var result = await new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(registryCatalog)).DiscoverAsync(referenceName);
        var catalog = Assert.IsType<VbaProjectReferenceCatalog>(result.Catalog);

        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Workbook"
            && definition.Kind == VbaSourceDefinitionKind.Class
            && definition.ParentTypeName is null);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Worksheet"
            && definition.Kind == VbaSourceDefinitionKind.Class
            && definition.ParentTypeName is null);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Worksheets"
            && definition.Kind == VbaSourceDefinitionKind.Class
            && definition.ParentTypeName is null);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Sheets"
            && definition.Kind == VbaSourceDefinitionKind.Class
            && definition.ParentTypeName is null);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Worksheets"
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.ParentTypeName == "Workbook"
            && definition.TypeReference?.Name == "Sheets");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Item"
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.ParentTypeName == "Sheets");
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Range"
            && definition.Kind == VbaSourceDefinitionKind.Property
            && definition.ParentTypeName == "Worksheet"
            && definition.TypeReference?.Name == "Range"
            && definition.Signature?.Label == "Range(Cell1, [Cell2]) As Range"
            && definition.Signature.Parameters.Select(parameter => parameter.Name).SequenceEqual(["Cell1", "Cell2"])
            && definition.Signature.Parameters[1].IsOptional);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Activate"
            && definition.Kind == VbaSourceDefinitionKind.Procedure
            && definition.Signature?.CallableKind == VbaCallableKind.Sub);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Kind == VbaSourceDefinitionKind.Event
            && definition.Signature?.CallableKind == VbaCallableKind.Event);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "xlCenter"
            && definition.Kind == VbaSourceDefinitionKind.EnumMember
            && definition.GlobalExposure == ReferenceDefinitionGlobalExposure.LibraryGlobal);
        Assert.Contains(catalog.Definitions, definition =>
            definition.Name == "Workbooks"
            && definition.GlobalExposure == ReferenceDefinitionGlobalExposure.MainHostGlobal);
    }

    [Fact]
    public async Task CatalogRefreshUpdatesCacheAfterDiscoveryWithoutBlockingEditorRequests()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb"),
                new VbaProjectReferenceCatalog(
                    "Generated Library",
                    ["Generated"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Generated Library",
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class,
                            "Generated from refreshed catalog metadata.")
                    ])));
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim value As ",
                "End Sub"
            ])
        };

        var refreshTask = service.RefreshAsync(selection);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var beforeRefresh = VbaSemanticInventoryFixture
            .Create(sourceDocuments, selection, cache.Current)
            .GetCompletionResult(uri, 2, "    Dim value As ".Length)
            .Definitions
            .Select(definition => definition.Name)
            .ToArray();
        Assert.DoesNotContain("GeneratedType", beforeRefresh);

        discovery.Release();
        await refreshTask;

        var afterRefresh = VbaSemanticInventoryFixture
            .Create(sourceDocuments, selection, cache.Current)
            .GetCompletionResult(uri, 2, "    Dim value As ".Length)
            .Definitions
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("GeneratedType", afterRefresh);
        Assert.True(cache.Identities.ContainsKey("Generated Library"));
    }

    [Fact]
    public async Task CatalogRefreshServiceUpdatesBestAvailableCatalogState()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                "Generated Library",
                "33333333-3333-3333-3333-333333333333",
                path: @"C:\TypeLibs\Generated.tlb")),
            new FakeTypeLibCatalogMetadataReader(
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class,
                            "Generated metadata.",
                            [])
                    ])));
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);

        Assert.Equal(VbaProjectReferenceCatalogSource.Unavailable, cache.GetCatalogSource("Generated Library"));

        await service.RefreshAsync(selection);

        Assert.Equal(VbaProjectReferenceCatalogSource.Generated, cache.GetCatalogSource("Generated Library"));
        Assert.Contains("Generated Library", cache.Current.ReferenceNames);
    }

    [Fact]
    public async Task CatalogRefreshCoalescesConcurrentDiscoveryForSameReference()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var discovery = new BlockingCatalogDiscovery(
            VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb"),
                new VbaProjectReferenceCatalog(
                    "Generated Library",
                    ["Generated"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Generated Library",
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class,
                            "Generated from refreshed catalog metadata.")
                    ])));
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);

        var firstRefresh = service.RefreshAsync(selection);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRefresh = service.RefreshAsync(selection);

        try
        {
            var completedSecond = await Task.WhenAny(secondRefresh, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(secondRefresh, completedSecond);
            Assert.Empty(await secondRefresh);
        }
        finally
        {
            discovery.Release();
            await firstRefresh;
            if (secondRefresh.IsCompleted)
            {
                await secondRefresh;
            }
        }

        Assert.Equal(1, discovery.CallCount);
    }

    [Fact]
    public async Task CatalogRefreshReplacesBundledCatalogWithGeneratedCatalog()
    {
        var bundledCatalog = new VbaProjectReferenceCatalog(
            "Generated Library",
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "GeneratedType",
                    VbaSourceDefinitionKind.Class,
                    "Bundled minimal metadata."),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "BundledOnly",
                    VbaSourceDefinitionKind.Property,
                    "Bundled-only member.",
                    ParentTypeName: "GeneratedType",
                    PropertyAccess: VbaPropertyAccess.Readable)
            ]);
        var cache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(bundledCatalog));
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                "Generated Library",
                "33333333-3333-3333-3333-333333333333",
                path: @"C:\TypeLibs\Generated.tlb")),
            new FakeTypeLibCatalogMetadataReader(
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class,
                            "Generated metadata.",
                            [
                                new TypeLibCatalogMember(
                                    "GeneratedOnly",
                                    VbaSourceDefinitionKind.Property,
                                    "Generated-only member.",
                                    PropertyAccess: VbaPropertyAccess.Readable)
                            ])
                    ])));
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim generated As GeneratedType",
                "    generated.",
                "End Sub"
            ])
        };

        var results = await service.RefreshAsync(selection);
        var index = VbaSemanticInventoryFixture.Create(sourceDocuments, selection, cache.Current);
        var memberCompletion = index.GetCompletionResult(uri, 3, "    generated.".Length).Definitions
            .Select(definition => definition.Name)
            .ToArray();

        Assert.Single(results);
        Assert.Contains("GeneratedOnly", memberCompletion);
        Assert.DoesNotContain("BundledOnly", memberCompletion);
        Assert.True(cache.Identities.ContainsKey("Generated Library"));
    }

    [Fact]
    public async Task CatalogRefreshUsesGeneratedTypeLibCatalogForEditorFeatures()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            CreateRegExpDiscovery());
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Microsoft VBScript Regular Expressions 5.5")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim regex As RegExp",
                "    regex.",
                "    regex.Pattern",
                "    regex.Execute(",
                "End Sub"
            ])
        };

        await service.RefreshAsync(selection);
        var index = VbaSemanticInventoryFixture.Create(sourceDocuments, selection, cache.Current);

        var typeCompletion = index.GetCompletionResult(uri, 2, "    Dim regex As ".Length);
        Assert.Contains(typeCompletion.Definitions, definition =>
            definition.Name == "RegExp"
            && definition.Kind == VbaSourceDefinitionKind.Class);
        var memberCompletion = index.GetCompletionResult(uri, 3, "    regex.".Length).Definitions;
        Assert.Contains(memberCompletion, definition =>
            definition.Name == "Pattern"
            && definition.Kind == VbaSourceDefinitionKind.Property);
        Assert.Contains(memberCompletion, definition =>
            definition.Name == "Execute"
            && definition.Kind == VbaSourceDefinitionKind.Procedure);

        var patternDefinition = index.ResolveSourceDefinition(uri, 4, "    regex.Pattern".IndexOf("Pattern", StringComparison.Ordinal));
        Assert.NotNull(patternDefinition);
        Assert.StartsWith(VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix, patternDefinition.Uri);
        Assert.Contains("regular expression pattern", patternDefinition.Documentation, StringComparison.OrdinalIgnoreCase);

        var signatureHelp = index.GetSignatureHelp(uri, 5, "    regex.Execute(".Length);
        Assert.NotNull(signatureHelp);
        Assert.Equal("Function Execute(String) As MatchCollection", signatureHelp.Signature.Label);

        var location = index.ResolveDefinition(uri, 5, "    regex.Execute(".IndexOf("Execute", StringComparison.Ordinal));
        Assert.Null(location);
    }

    [Fact]
    public async Task CatalogRefreshReportsFailuresWithoutBreakingSourceFeatures()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new FailingCatalogDiscovery("TypeLib registry is unavailable."));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Unavailable Library")]);
        const string uri = "file:///C:/work/Worker.bas";
        var sourceDocuments = new Dictionary<string, string>
        {
            [uri] = string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Function BuildValue() As String",
                "End Function",
                "",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ])
        };

        var results = await service.RefreshAsync(selection);

        var result = Assert.Single(results);
        Assert.True(result.DiscoveryResult.IsFailure);
        Assert.Contains("TypeLib registry is unavailable.", result.DiscoveryResult.ErrorMessage, StringComparison.Ordinal);
        var definitions = VbaSemanticInventoryFixture
            .Create(sourceDocuments, selection, cache.Current)
            .GetCompletionResult(uri, 5, 4)
            .Definitions
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("BuildValue", definitions);
    }

    [Fact]
    public async Task CatalogRefreshHonorsCancellationWithoutCachingCatalogMetadata()
    {
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new CancellationAwareCatalogDiscovery());
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Cancelable Library")]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RefreshAsync(selection, cancellation.Token));

        Assert.Empty(cache.Identities);
        Assert.False(cache.Current.HasCatalog("Cancelable Library"));
    }

    private sealed class FakeTypeLibRegistryCatalogReader(TypeLibRegistryCatalog catalog)
        : ITypeLibRegistryCatalogReader
    {
        public TypeLibRegistryCatalog Read() => catalog;
    }

    private sealed class SequencedTypeLibRegistryCatalogReader(params TypeLibRegistryCatalog[] catalogs)
        : ITypeLibRegistryCatalogReader
    {
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);

        public TypeLibRegistryCatalog Read()
        {
            var index = Interlocked.Increment(ref readCount) - 1;
            if (index == 0)
            {
                return catalogs[0];
            }

            return new TypeLibRegistryCatalog(
                complete: true,
                names: catalogs
                    .Skip(1)
                    .SelectMany(catalog => catalog.Names)
                    .ToArray(),
                warnings: [],
                diagnostic: null);
        }
    }

    private sealed class FakeTypeLibCatalogMetadataReader : ITypeLibCatalogMetadataReader
    {
        private readonly TypeLibCatalogMetadata metadata;

        public FakeTypeLibCatalogMetadataReader(TypeLibCatalogMetadata metadata)
        {
            this.metadata = metadata;
        }

        public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
            => metadata;
    }

    private sealed class PathFallbackTypeLibCatalogMetadataReader(string availablePath)
        : ITypeLibCatalogMetadataReader
    {
        private readonly List<string> attemptedPaths = [];

        public IReadOnlyList<string> AttemptedPaths => attemptedPaths;

        public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
        {
            attemptedPaths.Add(identity.Path);
            return identity.Path.Equals(availablePath, StringComparison.OrdinalIgnoreCase)
                ? new TypeLibCatalogMetadata("Custom", [])
                : throw new FileNotFoundException("The registered TypeLib location is unavailable.", identity.Path);
        }
    }

    private static TypeLibRegistryCatalog CreateNeutralRegistryCatalog(
        string name,
        string guid,
        int major = 1,
        int minor = 0,
        string? path = null)
        => new(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    name,
                    [
                        new TypeLibRegistryLineage(
                            guid,
                            [
                                new TypeLibRegistryVersion(
                                    major,
                                    minor,
                                    [
                                        new TypeLibRegistryLocale(
                                            0,
                                            [new TypeLibRegistryPath("win32", path ?? $@"C:\TypeLibs\{name}.tlb")])
                                    ])
                            ])
                    ])
            ],
            warnings: [],
            diagnostic: null);

    private static VbaProjectReferenceCatalog CreateReferenceCatalog(
        string referenceName,
        string typeName)
        => new(
            referenceName,
            [],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    typeName,
                    VbaSourceDefinitionKind.Class,
                    null)
            ]);

    private static TypeLibReferenceCatalogDiscovery CreateRegExpDiscovery()
        => new(
            new FakeTypeLibRegistryCatalogReader(CreateNeutralRegistryCatalog(
                "Microsoft VBScript Regular Expressions 5.5",
                "3f4daca7-160d-11d2-a8e9-00104b365c9f",
                major: 5,
                minor: 5,
                path: @"C:\Windows\System32\vbscript.dll\3")),
            new FakeTypeLibCatalogMetadataReader(
                new TypeLibCatalogMetadata(
                    "VBScript_RegExp_55",
                    [
                        new TypeLibCatalogType(
                            "RegExp",
                            VbaSourceDefinitionKind.Class,
                            "Regular expression engine.",
                            [
                                new TypeLibCatalogMember(
                                    "Pattern",
                                    VbaSourceDefinitionKind.Property,
                                    "Sets or returns the regular expression pattern.",
                                    TypeReference: new VbaTypeReference("String"),
                                    PropertyAccess: VbaPropertyAccess.Readable | VbaPropertyAccess.Writable),
                                new TypeLibCatalogMember(
                                    "Execute",
                                    VbaSourceDefinitionKind.Procedure,
                                    "Executes a regular expression search.",
                                    new VbaCallableSignature(
                                        "Execute(String) As MatchCollection",
                                        [new VbaCallableParameter("String", "The string to search.")],
                                        "Executes a regular expression search.",
                                        CallableKind: VbaCallableKind.Function),
                                    new VbaTypeReference("MatchCollection"))
                            ]),
                        new TypeLibCatalogType(
                            "RegExpError",
                            VbaSourceDefinitionKind.Enum,
                            "Regular expression parse errors.",
                            [
                                new TypeLibCatalogMember(
                                    "SyntaxError",
                                    VbaSourceDefinitionKind.EnumMember,
                                    "The regular expression syntax is invalid.")
                            ]),
                        new TypeLibCatalogType(
                            "RegExpEvents",
                            VbaSourceDefinitionKind.Class,
                            null,
                            [
                                new TypeLibCatalogMember(
                                    "ExecuteComplete",
                                    VbaSourceDefinitionKind.Event,
                                    "Occurs after a regular expression search completes.")
                            ])
                    ])));

    private sealed class BlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult result;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        {
            this.result = result;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public void Release() => release.TrySetResult();
    }

    private sealed class FailingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly string message;

        public FailingCatalogDiscovery(string message)
        {
            this.message = message;
        }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(referenceName, message));
    }

    private sealed class CancellationAwareCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Cancellation was not observed."));
        }
    }
}
