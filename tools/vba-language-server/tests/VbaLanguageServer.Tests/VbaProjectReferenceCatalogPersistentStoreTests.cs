using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogPersistentStoreTests
{
    [Fact]
    public void PersistentStoreSavesAndLoadsGeneratedCatalogWithIdentityMetadata()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            var identity = CreateIdentity("Generated Library");
            var catalog = CreateGeneratedCatalog("Generated Library", "GeneratedType", "GeneratedMember");

            store.Save(new VbaProjectReferenceCatalogPersistentEntry(identity, catalog));
            var loadResult = store.Load("Generated Library");

            Assert.Null(loadResult.WarningMessage);
            var entry = Assert.IsType<VbaProjectReferenceCatalogPersistentEntry>(loadResult.Entry);
            Assert.Equal(identity, entry.Identity);
            Assert.Equal("Generated Library", entry.Catalog.ReferenceName);
            Assert.Contains(entry.Catalog.Definitions, definition =>
                definition.Name == "GeneratedType"
                && definition.Kind == VbaSourceDefinitionKind.Class);
            Assert.Contains(entry.Catalog.Definitions, definition =>
                definition.Name == "GeneratedMember"
                && definition.Kind == VbaSourceDefinitionKind.Property
                && definition.ParentTypeName == "GeneratedType");
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void PersistentStoreUsesDeterministicSafeKeysForReferenceAndIdentity()
    {
        var identity = CreateIdentity("Generated Library");
        var sameIdentity = identity with
        {
            ReferenceName = "generated library",
            Guid = identity.Guid.ToLowerInvariant(),
            Path = @"c:\typelibs\generated.tlb"
        };
        var differentIdentity = identity with { MinorVersion = 1 };

        var referenceKey = VbaProjectReferenceCatalogPersistentStore.CreateReferenceIndexKey("Generated Library");
        var repeatedReferenceKey = VbaProjectReferenceCatalogPersistentStore.CreateReferenceIndexKey("generated library");
        var catalogKey = VbaProjectReferenceCatalogPersistentStore.CreateCatalogEntryKey(identity);
        var repeatedCatalogKey = VbaProjectReferenceCatalogPersistentStore.CreateCatalogEntryKey(sameIdentity);
        var differentCatalogKey = VbaProjectReferenceCatalogPersistentStore.CreateCatalogEntryKey(differentIdentity);

        Assert.Equal(referenceKey, repeatedReferenceKey);
        Assert.Equal(catalogKey, repeatedCatalogKey);
        Assert.NotEqual(catalogKey, differentCatalogKey);
        Assert.Matches("^[a-z0-9-]+\\.json$", referenceKey);
        Assert.Matches("^[a-z0-9-]+\\.json$", catalogKey);
    }

    [Fact]
    public void PersistentStoreTreatsCorruptedCacheAsRecoverableMiss()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            var identity = CreateIdentity("Generated Library");
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                identity,
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "GeneratedMember")));

            File.WriteAllText(
                store.GetReferenceIndexPath("Generated Library"),
                "{ this is not valid json");

            var loadResult = store.Load("Generated Library");

            Assert.Null(loadResult.Entry);
            Assert.Contains("could not be read", loadResult.WarningMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void PersistentStoreAtomicallyReplacesExistingCatalogEntry()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            var identity = CreateIdentity("Generated Library");
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                identity,
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "OldMember")));

            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                identity,
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "NewMember")));
            var loadResult = store.Load("Generated Library");

            var entry = Assert.IsType<VbaProjectReferenceCatalogPersistentEntry>(loadResult.Entry);
            Assert.Contains(entry.Catalog.Definitions, definition => definition.Name == "NewMember");
            Assert.DoesNotContain(entry.Catalog.Definitions, definition => definition.Name == "OldMember");
            Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshPreloadsPersistedCatalogBeforeDiscovery()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var persistedCatalog = CreateGeneratedCatalog("Generated Library", "GeneratedType", "GeneratedMember");
            var bundledCatalog = CreateGeneratedCatalog("Generated Library", "GeneratedType", "BundledOnly");
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateIdentity("Generated Library"),
                persistedCatalog));
            var cache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty.WithCatalog(bundledCatalog));
            var discovery = new CountingCatalogDiscovery();
            var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery, store);
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
                    "    generated.GeneratedMember",
                    "    generated.GeneratedMethod(",
                    "End Sub"
                ])
            };

            var results = await service.RefreshAsync(selection);
            var index = VbaSourceIndex.Build(sourceDocuments, selection, cache.Current);
            var memberCompletion = index.GetCompletionDefinitions(uri, 3, "    generated.".Length)
                .Select(definition => definition.Name)
                .ToArray();
            var memberDefinition = index.ResolveSourceDefinition(
                uri,
                4,
                "    generated.GeneratedMember".IndexOf("GeneratedMember", StringComparison.Ordinal));
            var signatureHelp = index.GetSignatureHelp(uri, 5, "    generated.GeneratedMethod(".Length);
            var semanticTokens = index.GetSemanticTokens(uri);

            var result = Assert.Single(results);
            Assert.Equal(VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache, result.Status);
            Assert.Equal(0, discovery.CallCount);
            Assert.Contains("GeneratedMember", memberCompletion);
            Assert.DoesNotContain("BundledOnly", memberCompletion);
            Assert.NotNull(memberDefinition);
            Assert.Equal("Generated member.", memberDefinition.Documentation);
            Assert.NotNull(signatureHelp);
            Assert.Equal("GeneratedMethod(Value) As String", signatureHelp.Signature.Label);
            Assert.Contains(semanticTokens, token => token.Text == "GeneratedType" && token.TokenType == "class");
            Assert.Contains(semanticTokens, token => token.Text == "GeneratedMember" && token.TokenType == "property");
            Assert.True(cache.Identities.ContainsKey("Generated Library"));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshFallsBackToDiscoveryWhenPersistedCatalogMetadataIsIncompatible()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateIdentity("Generated Library"),
                CreateGeneratedCatalog("Generated Library", "OldType", "OldMember")));
            var indexPath = store.GetReferenceIndexPath("Generated Library");
            File.WriteAllText(
                indexPath,
                File.ReadAllText(indexPath)
                    .Replace(
                        VbaProjectReferenceCatalogPersistentStore.CurrentGeneratorVersion,
                        "older-generator",
                        StringComparison.Ordinal));
            var refreshedCatalog = CreateGeneratedCatalog("Generated Library", "GeneratedType", "GeneratedMember");
            var discovery = new CountingCatalogDiscovery(
                VbaProjectReferenceCatalogDiscoveryResult.Success(
                    CreateIdentity("Generated Library"),
                    refreshedCatalog));
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery, store);
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]);

            var results = await service.RefreshAsync(selection);

            var result = Assert.Single(results);
            Assert.Equal(VbaProjectReferenceCatalogRefreshStatus.Refreshed, result.Status);
            Assert.Equal(1, discovery.CallCount);
            Assert.True(result.DiscoveryResult.HasUsableCatalog);
            Assert.True(cache.Current.HasCatalog("Generated Library"));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static VbaProjectReferenceCatalogIdentity CreateIdentity(string referenceName)
        => new(
            referenceName,
            "{33333333-3333-3333-3333-333333333333}",
            1,
            0,
            0,
            @"C:\TypeLibs\Generated.tlb");

    private static VbaProjectReferenceCatalog CreateGeneratedCatalog(
        string referenceName,
        string typeName,
        string memberName)
        => new(
            referenceName,
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    referenceName,
                    typeName,
                    VbaSourceDefinitionKind.Class,
                    "Generated type."),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    memberName,
                    VbaSourceDefinitionKind.Property,
                    "Generated member.",
                    ParentTypeName: typeName),
                new VbaProjectReferenceDefinition(
                    referenceName,
                    "GeneratedMethod",
                    VbaSourceDefinitionKind.Procedure,
                    "Generated method.",
                    new VbaCallableSignature(
                        "GeneratedMethod(Value) As String",
                        [new VbaCallableParameter("Value", "The input value.")],
                        "Generated method."),
                    ParentTypeName: typeName,
                    TypeReference: new VbaTypeReference("String"))
            ]);

    private sealed class CountingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult? result;

        public CountingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult? result = null)
        {
            this.result = result;
        }

        public int CallCount { get; private set; }

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result ?? VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Discovery should not have been called."));
        }
    }
}
