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
    public void PersistentStoreDefaultRootUsesConfiguredEnvironmentOverride()
    {
        var previousCacheRoot = Environment.GetEnvironmentVariable(
            VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable);
        var configuredCacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"vba-ls-configured-catalog-store-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(
                VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable,
                configuredCacheRoot);

            Assert.Equal(
                configuredCacheRoot,
                VbaProjectReferenceCatalogPersistentStore.GetDefaultRootDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable,
                previousCacheRoot);
        }
    }

    [Fact]
    public void PersistentStoreDefaultRootDoesNotUseBuildOutputWhenUnconfigured()
    {
        var previousCacheRoot = Environment.GetEnvironmentVariable(
            VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable,
                null);

            var defaultRoot = Path.GetFullPath(VbaProjectReferenceCatalogPersistentStore.GetDefaultRootDirectory());
            var buildOutputRoot = Path.GetFullPath(AppContext.BaseDirectory);

            Assert.False(
                defaultRoot.StartsWith(buildOutputRoot, StringComparison.OrdinalIgnoreCase),
                $"Default reference catalog cache root should not be inside build output. Root: {defaultRoot}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                VbaProjectReferenceCatalogPersistentStore.CacheRootEnvironmentVariable,
                previousCacheRoot);
        }
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
            Assert.Equal(VbaProjectReferenceCatalogPersistentLoadStatus.Unreadable, loadResult.Status);
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
            Assert.Equal("Function GeneratedMethod(Value) As String", signatureHelp.Signature.Label);
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
            MarkReferenceIndexAsStale(store, "Generated Library");
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

            Assert.Contains(results, result =>
                result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache);
            var result = Assert.Single(
                results,
                result => result.Status == VbaProjectReferenceCatalogRefreshStatus.Refreshed);
            Assert.Equal(1, discovery.CallCount);
            Assert.True(result.DiscoveryResult.HasUsableCatalog);
            Assert.True(cache.Current.HasCatalog("Generated Library"));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshKeepsStalePersistedCatalogActiveWhileDiscoveryIsBlockedAndReplacesAfterSuccess()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateIdentity("Generated Library"),
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "StaleMember")));
            MarkReferenceIndexAsStale(store, "Generated Library");
            var discovery = new BlockingCatalogDiscovery(
                VbaProjectReferenceCatalogDiscoveryResult.Success(
                    CreateIdentity("Generated Library"),
                    CreateGeneratedCatalog("Generated Library", "GeneratedType", "FreshMember")));
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery, store);
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]);

            var refreshTask = service.RefreshAsync(selection);
            await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beforeRefreshMembers = GetGeneratedTypeMemberCompletionNames(cache, selection);

            discovery.Release();
            var results = await refreshTask;
            var afterRefreshMembers = GetGeneratedTypeMemberCompletionNames(cache, selection);

            Assert.Contains("StaleMember", beforeRefreshMembers);
            Assert.DoesNotContain("FreshMember", beforeRefreshMembers);
            Assert.Contains(results, result =>
                result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache);
            Assert.Contains("FreshMember", afterRefreshMembers);
            Assert.DoesNotContain("StaleMember", afterRefreshMembers);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshPreservesStalePersistedCatalogWhenRefreshFails()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateIdentity("Generated Library"),
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "StaleMember")));
            MarkReferenceIndexAsStale(store, "Generated Library");
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]);
            var service = new VbaProjectReferenceCatalogRefreshService(
                cache,
                new CountingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                    "Generated Library",
                    "TypeLib registry is unavailable.")),
                store);

            var results = await service.RefreshAsync(selection);
            var members = GetGeneratedTypeMemberCompletionNames(cache, selection);

            Assert.Contains(results, result =>
                result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache);
            Assert.Contains(results, result => result.DiscoveryResult.IsFailure);
            Assert.Contains("StaleMember", members);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshPreservesStalePersistedCatalogWhenRefreshIsAmbiguous()
    {
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                CreateIdentity("Generated Library"),
                CreateGeneratedCatalog("Generated Library", "GeneratedType", "StaleMember")));
            MarkReferenceIndexAsStale(store, "Generated Library");
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]);
            var service = new VbaProjectReferenceCatalogRefreshService(
                cache,
                new CountingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(
                    "Generated Library",
                    [
                        CreateIdentity("Generated Library"),
                        CreateIdentity("Generated Library") with { Guid = "{44444444-4444-4444-4444-444444444444}" }
                    ])),
                store);

            var results = await service.RefreshAsync(selection);
            var members = GetGeneratedTypeMemberCompletionNames(cache, selection);

            Assert.Contains(results, result =>
                result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache);
            Assert.Contains(results, result => result.DiscoveryResult.IsAmbiguous);
            Assert.Contains("StaleMember", members);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogRefreshReportsNonFatalPersistentWriteFailure()
    {
        var tempRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-store-").FullName;
        try
        {
            var cacheRootFile = Path.Combine(tempRoot, "cache-root");
            File.WriteAllText(cacheRootFile, "not a directory");
            var refreshedCatalog = CreateGeneratedCatalog("Generated Library", "GeneratedType", "GeneratedMember");
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var service = new VbaProjectReferenceCatalogRefreshService(
                cache,
                new CountingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult.Success(
                    CreateIdentity("Generated Library"),
                    refreshedCatalog)),
                new VbaProjectReferenceCatalogPersistentStore(cacheRootFile));
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Generated Library")]);

            var result = Assert.Single(await service.RefreshAsync(selection));

            Assert.True(result.DiscoveryResult.HasUsableCatalog);
            Assert.Equal(VbaProjectReferenceCatalogSource.Generated, result.Source);
            Assert.True(result.ExpensiveMetadataRan);
            Assert.Equal("typelib-discovery", result.Phase);
            Assert.Contains("could not be written", result.WarningMessage, StringComparison.OrdinalIgnoreCase);
            Assert.True(cache.Current.HasCatalog("Generated Library"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
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

    private static void MarkReferenceIndexAsStale(
        VbaProjectReferenceCatalogPersistentStore store,
        string referenceName)
    {
        var indexPath = store.GetReferenceIndexPath(referenceName);
        File.WriteAllText(
            indexPath,
            File.ReadAllText(indexPath)
                .Replace(
                    VbaProjectReferenceCatalogPersistentStore.CurrentGeneratorVersion,
                    "older-generator",
                    StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetGeneratedTypeMemberCompletionNames(
        VbaProjectReferenceCatalogCache cache,
        VbaProjectReferenceSelection selection)
    {
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
        return VbaSourceIndex
            .Build(sourceDocuments, selection, cache.Current)
            .GetCompletionDefinitions(uri, 3, "    generated.".Length)
            .Select(definition => definition.Name)
            .ToArray();
    }

    private sealed class BlockingCatalogDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        private readonly VbaProjectReferenceCatalogDiscoveryResult result;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        {
            this.result = result;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public void Release() => release.TrySetResult();
    }

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
