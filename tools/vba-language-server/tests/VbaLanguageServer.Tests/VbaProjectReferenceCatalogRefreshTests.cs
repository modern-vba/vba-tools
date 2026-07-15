using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogRefreshTests
{
    [Fact]
    public async Task TypeLibDiscoveryResolvesReferenceCatalogIdentity()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryReader(
                new TypeLibRegistryEntry(
                    "Custom Library",
                    "{11111111-1111-1111-1111-111111111111}",
                    1,
                    2,
                    0,
                    @"C:\TypeLibs\Custom.tlb")),
            new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Custom", [])));

        var result = await discovery.DiscoverAsync("custom library");

        Assert.False(result.IsFailure);
        Assert.False(result.IsAmbiguous);
        var identity = Assert.Single(result.Identities);
        Assert.Equal("Custom Library", identity.ReferenceName);
        Assert.Equal("{11111111-1111-1111-1111-111111111111}", identity.Guid);
        Assert.Equal(1, identity.MajorVersion);
        Assert.Equal(2, identity.MinorVersion);
        Assert.Equal(0, identity.Lcid);
        Assert.Equal(@"C:\TypeLibs\Custom.tlb", identity.Path);
    }

    [Fact]
    public async Task TypeLibDiscoveryReportsAmbiguousMatchesInsteadOfGuessing()
    {
        var discovery = new TypeLibReferenceCatalogDiscovery(new FakeTypeLibRegistryReader(
            new TypeLibRegistryEntry(
                "Ambiguous Library",
                "{11111111-1111-1111-1111-111111111111}",
                1,
                0,
                0,
                @"C:\TypeLibs\AmbiguousA.tlb"),
            new TypeLibRegistryEntry(
                "Ambiguous Library",
                "{22222222-2222-2222-2222-222222222222}",
                1,
                0,
                0,
                @"C:\TypeLibs\AmbiguousB.tlb")));

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
    public void ComTypeLibCatalogMetadataReaderReadsRegisteredRegExpMetadataWhenAvailable()
    {
        var registryEntry = new RegistryTypeLibRegistryReader()
            .ReadTypeLibraries()
            .FirstOrDefault(entry => entry.ReferenceName.Equals(
                "Microsoft VBScript Regular Expressions 5.5",
                StringComparison.OrdinalIgnoreCase));
        if (registryEntry is null)
        {
            return;
        }

        var identity = new VbaProjectReferenceCatalogIdentity(
            registryEntry.ReferenceName,
            registryEntry.Guid,
            registryEntry.MajorVersion,
            registryEntry.MinorVersion,
            registryEntry.Lcid,
            registryEntry.Path);
        var metadata = new ComTypeLibCatalogMetadataReader().ReadMetadata(identity);
        var catalog = TypeLibReferenceCatalogBuilder.Build(registryEntry.ReferenceName, metadata);

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

    [Fact]
    public void ComTypeLibCatalogMetadataReaderReadsRegisteredExcelWorkbookMetadataWhenAvailable()
    {
        var registryEntry = new RegistryTypeLibRegistryReader()
            .ReadTypeLibraries()
            .FirstOrDefault(entry => entry.ReferenceName.Equals(
                "Microsoft Excel 16.0 Object Library",
                StringComparison.OrdinalIgnoreCase));
        if (registryEntry is null)
        {
            return;
        }

        var identity = new VbaProjectReferenceCatalogIdentity(
            registryEntry.ReferenceName,
            registryEntry.Guid,
            registryEntry.MajorVersion,
            registryEntry.MinorVersion,
            registryEntry.Lcid,
            registryEntry.Path);
        var metadata = new ComTypeLibCatalogMetadataReader().ReadMetadata(identity);
        var catalog = TypeLibReferenceCatalogBuilder.Build(registryEntry.ReferenceName, metadata);

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
                "End Sub"
            ])
        };

        var refreshTask = service.RefreshAsync(selection);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var beforeRefresh = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 1, 0)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.DoesNotContain("GeneratedType", beforeRefresh);

        discovery.Release();
        await refreshTask;

        var afterRefresh = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 1, 0)
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
            new FakeTypeLibRegistryReader(
                new TypeLibRegistryEntry(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb")),
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
                    ParentTypeName: "GeneratedType")
            ]);
        var cache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(bundledCatalog));
        var discovery = new TypeLibReferenceCatalogDiscovery(
            new FakeTypeLibRegistryReader(
                new TypeLibRegistryEntry(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb")),
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
                                    "Generated-only member.")
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
        var index = VbaSourceIndex.Build(sourceDocuments, selection, cache.Current);
        var memberCompletion = index.GetCompletionDefinitions(uri, 3, "    generated.".Length)
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
        var index = VbaSourceIndex.Build(sourceDocuments, selection, cache.Current);

        var typeCompletion = index.GetCompletionResult(uri, 2, "    Dim regex As ".Length);
        Assert.Contains(typeCompletion.Definitions, definition =>
            definition.Name == "RegExp"
            && definition.Kind == VbaSourceDefinitionKind.Class);
        var memberCompletion = index.GetCompletionDefinitions(uri, 3, "    regex.".Length);
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
        Assert.NotNull(location);
        Assert.StartsWith(VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix, location.Uri);
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
        var definitions = VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 5, 4)
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

    private sealed class FakeTypeLibRegistryReader : ITypeLibRegistryReader
    {
        private readonly IReadOnlyList<TypeLibRegistryEntry> entries;

        public FakeTypeLibRegistryReader(params TypeLibRegistryEntry[] entries)
        {
            this.entries = entries;
        }

        public IReadOnlyList<TypeLibRegistryEntry> ReadTypeLibraries() => entries;
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

    private static TypeLibReferenceCatalogDiscovery CreateRegExpDiscovery()
        => new(
            new FakeTypeLibRegistryReader(
                new TypeLibRegistryEntry(
                    "Microsoft VBScript Regular Expressions 5.5",
                    "{3F4DACA7-160D-11D2-A8E9-00104B365C9F}",
                    5,
                    5,
                    0,
                    @"C:\Windows\System32\vbscript.dll\3")),
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
                                    TypeReference: new VbaTypeReference("String")),
                                new TypeLibCatalogMember(
                                    "Execute",
                                    VbaSourceDefinitionKind.Procedure,
                                    "Executes a regular expression search.",
                                    new VbaCallableSignature(
                                        "Execute(String) As MatchCollection",
                                        [new VbaCallableParameter("String", "The string to search.")],
                                        "Executes a regular expression search."),
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
