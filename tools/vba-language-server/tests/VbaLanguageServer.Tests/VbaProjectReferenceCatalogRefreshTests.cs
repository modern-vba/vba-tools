using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
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
    public async Task BackgroundRefreshUsesPinnedCliOnceToResolveAmbiguousNeutralIdentity()
    {
        const string referenceName = "Ambiguous Library";
        const string selectedGuid = "22222222-2222-2222-2222-222222222222";
        var projectPath = Path.GetFullPath(Path.Combine("projects", "Sample"));
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = new List<(string File, IReadOnlyList<string> Arguments)>();
        var registryCatalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    referenceName,
                    [
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            "11111111-1111-1111-1111-111111111111").Names[0].Lineages[0],
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            selectedGuid).Names[0].Lineages[0]
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var registryReader = new FakeTypeLibRegistryCatalogReader(registryCatalog);
        var discovery = new VbaDevReferenceListCatalogDiscoveryFactory(
            new TypeLibReferenceCatalogDiscovery(
                registryReader,
                new FakeTypeLibCatalogMetadataReader(
                    new TypeLibCatalogMetadata(
                        "Ambiguous",
                        [
                            new TypeLibCatalogType(
                                "ResolvedType",
                                VbaSourceDefinitionKind.Class,
                                null,
                                [])
                        ]))),
            executablePath,
            (file, arguments, _) =>
            {
                processCalls.Add((file, arguments));
                return Task.FromResult(new VbaDevReferenceListProcessResult(
                    0,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "1.0",
                        scope = "project",
                        project = projectPath,
                        document = "Book1",
                        mode = "configured",
                        complete = true,
                        warnings = Array.Empty<object>(),
                        references = new[]
                        {
                            new
                            {
                                name = referenceName,
                                status = "resolved",
                                identity = new
                                {
                                    guid = selectedGuid,
                                    major = 1,
                                    minor = 0
                                }
                            }
                        }
                    }),
                    ""));
            });
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);

        var results = await service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(projectPath, "Book1", selection),
            CancellationToken.None);

        Assert.True(Assert.Single(results).DiscoveryResult.HasUsableCatalog);
        var call = Assert.Single(processCalls);
        Assert.Equal(executablePath, call.File);
        Assert.Equal(
            [
                "reference",
                "list",
                "--project",
                projectPath,
                "--document",
                "Book1",
                "--format",
                "json"
            ],
            call.Arguments);
        Assert.Equal(selectedGuid, cache.Identities[referenceName].Guid);
        Assert.Equal(1, registryReader.ReadCount);
        Assert.Contains(
            cache.Current.GetActiveDefinitions(selection),
            definition => definition.Name == "ResolvedType");
    }

    [Fact]
    public async Task CompleteMixedNonzeroCliResultCommitsResolvedSiblingAndPreservesAmbiguousLastKnownGood()
    {
        const string ambiguousName = "Library A";
        const string resolvedName = "Library B";
        const string resolvedGuid = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        var projectPath = Path.GetFullPath(Path.Combine("projects", "Mixed"));
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var registryCatalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    ambiguousName,
                    [
                        CreateNeutralRegistryCatalog(
                            ambiguousName,
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").Names[0].Lineages[0],
                        CreateNeutralRegistryCatalog(
                            ambiguousName,
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb").Names[0].Lineages[0]
                    ]),
                new TypeLibRegistryCatalogName(
                    resolvedName,
                    [
                        CreateNeutralRegistryCatalog(
                            resolvedName,
                            "cccccccc-cccc-cccc-cccc-cccccccccccc").Names[0].Lineages[0],
                        CreateNeutralRegistryCatalog(
                            resolvedName,
                            resolvedGuid).Names[0].Lineages[0]
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var processCallCount = 0;
        var discovery = new VbaDevReferenceListCatalogDiscoveryFactory(
            new TypeLibReferenceCatalogDiscovery(
                new FakeTypeLibRegistryCatalogReader(registryCatalog),
                new FakeTypeLibCatalogMetadataReader(
                    new TypeLibCatalogMetadata(
                        "Mixed",
                        [
                            new TypeLibCatalogType(
                                "ResolvedSiblingType",
                                VbaSourceDefinitionKind.Class,
                                null,
                                [])
                        ]))),
            executablePath,
            (_, _, _) =>
            {
                Interlocked.Increment(ref processCallCount);
                return Task.FromResult(new VbaDevReferenceListProcessResult(
                    7,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "1.0",
                        scope = "project",
                        project = projectPath,
                        document = "Book1",
                        mode = "configured",
                        complete = true,
                        warnings = Array.Empty<object>(),
                        references = new object[]
                        {
                            new
                            {
                                name = ambiguousName,
                                status = "ambiguous",
                                reasonCode = "multipleUsableIdentities",
                                candidates = new[]
                                {
                                    new { guid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", major = 1, minor = 0 },
                                    new { guid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", major = 1, minor = 0 }
                                },
                                message = "Multiple registered identities remain usable."
                            },
                            new
                            {
                                name = resolvedName,
                                status = "resolved",
                                identity = new { guid = resolvedGuid, major = 1, minor = 0 }
                            }
                        }
                    }),
                    "conclusive ambiguity"));
            });
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(CreateReferenceCatalog(ambiguousName, "LastKnownGoodType"));
        var ambiguousSelection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(ambiguousName)]);
        var revisionBefore = cache.CaptureSelectionState(ambiguousSelection.References).Revision;
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(ambiguousName), new VbaProjectReference(resolvedName)]);

        var results = await service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(projectPath, "Book1", selection),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].DiscoveryResult.IsAmbiguous);
        Assert.True(results[1].DiscoveryResult.HasUsableCatalog);
        Assert.Equal(1, processCallCount);
        Assert.Equal(VbaProjectReferenceCatalogSource.StalePersisted, cache.GetCatalogSource(ambiguousName));
        Assert.Equal(revisionBefore, cache.CaptureSelectionState(ambiguousSelection.References).Revision);
        Assert.Contains(
            cache.Current.GetActiveDefinitions(ambiguousSelection),
            definition => definition.Name == "LastKnownGoodType");
        Assert.Equal(resolvedGuid, cache.Identities[resolvedName].Guid);
    }

    [Fact]
    public async Task MissingPinnedCliPreservesLastKnownGoodWithoutFallback()
    {
        const string referenceName = "Ambiguous Library";
        var projectPath = Path.GetFullPath(Path.Combine("projects", "CliLoss"));
        var executablePath = Path.GetFullPath(Path.Combine("missing", "vba-dev.exe"));
        var registryCatalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    referenceName,
                    [
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").Names[0].Lineages[0],
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb").Names[0].Lineages[0]
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var processCalls = 0;
        var discovery = new VbaDevReferenceListCatalogDiscoveryFactory(
            new TypeLibReferenceCatalogDiscovery(
                new FakeTypeLibRegistryCatalogReader(registryCatalog),
                new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Missing", []))),
            executablePath,
            (_, _, _) =>
            {
                Interlocked.Increment(ref processCalls);
                throw new FileNotFoundException("The pinned executable disappeared.", executablePath);
            });
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(CreateReferenceCatalog(referenceName, "LastKnownGoodType"));
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);
        var revisionBefore = cache.CaptureSelectionState(selection.References).Revision;
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);

        var result = Assert.Single(await service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(projectPath, "Book1", selection),
            CancellationToken.None));

        Assert.True(result.DiscoveryResult.IsFailure);
        Assert.Contains("pinned executable disappeared", result.DiscoveryResult.ErrorMessage);
        Assert.Equal(1, processCalls);
        Assert.Equal(VbaProjectReferenceCatalogSource.StalePersisted, cache.GetCatalogSource(referenceName));
        Assert.Equal(revisionBefore, cache.CaptureSelectionState(selection.References).Revision);
        Assert.Contains(
            cache.Current.GetActiveDefinitions(selection),
            definition => definition.Name == "LastKnownGoodType");
    }

    [Fact]
    public async Task SupersededContextCannotCommitAfterEnteringMutationLane()
    {
        const string referenceName = "Scoped Library";
        const string scopeKey = "scope-a";
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);
        var discoveryResult = VbaProjectReferenceCatalogDiscoveryResult.Success(
            new VbaProjectReferenceCatalogIdentity(
                referenceName,
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0,
                0,
                @"C:\TypeLibs\Scoped.tlb"),
            CreateReferenceCatalog(referenceName, "SupersededType"));
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new InlineCatalogDiscovery(discoveryResult));
        var mutationLane = new BlockingMutationLane();
        service.AttachMutationLane(mutationLane);
        var current = 1;
        var refresh = service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(
                Path.GetFullPath(@"C:\work\Scoped"),
                "Book1",
                selection,
                scopeKey,
                SelectionFingerprint: "fingerprint-a",
                IsCurrent: () => Volatile.Read(ref current) == 1),
            CancellationToken.None);
        await mutationLane.CommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Write(ref current, 0);
        mutationLane.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));

        var state = cache.CaptureSelectionState(selection.References, scopeKey);
        Assert.DoesNotContain(
            state.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "SupersededType");
    }

    [Fact]
    public async Task CompleteUnavailableCliEntryPreservesScopedLastKnownGood()
    {
        const string referenceName = "Unavailable Library";
        const string scopeKey = "unavailable-scope";
        var projectPath = Path.GetFullPath(@"C:\work\Unavailable");
        var registryCatalog = new TypeLibRegistryCatalog(
            complete: true,
            names:
            [
                new TypeLibRegistryCatalogName(
                    referenceName,
                    [
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa").Names[0].Lineages[0],
                        CreateNeutralRegistryCatalog(
                            referenceName,
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb").Names[0].Lineages[0]
                    ])
            ],
            warnings: [],
            diagnostic: null);
        var discovery = new VbaDevReferenceListCatalogDiscoveryFactory(
            new TypeLibReferenceCatalogDiscovery(
                new FakeTypeLibRegistryCatalogReader(registryCatalog),
                new FakeTypeLibCatalogMetadataReader(new TypeLibCatalogMetadata("Unavailable", []))),
            Path.GetFullPath(@"C:\tools\vba-dev.exe"),
            (_, _, _) => Task.FromResult(new VbaDevReferenceListProcessResult(
                3,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    scope = "project",
                    project = projectPath,
                    document = "Book1",
                    mode = "configured",
                    complete = true,
                    warnings = Array.Empty<object>(),
                    references = new[]
                    {
                        new
                        {
                            name = referenceName,
                            status = "unavailable",
                            reasonCode = "noUsableIdentity",
                            candidates = new[]
                            {
                                new { guid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", major = 1, minor = 0 },
                                new { guid = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", major = 1, minor = 0 }
                            },
                            message = "No candidate was usable by the selected project."
                        }
                    }
                }),
                "unavailable")));
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        cache.StoreStaleCatalog(
            CreateReferenceCatalog(referenceName, "ScopedLastKnownGoodType"),
            scopeKey);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);
        var revisionBefore = cache.CaptureSelectionState(selection.References, scopeKey).Revision;
        var service = new VbaProjectReferenceCatalogRefreshService(cache, discovery);

        var result = Assert.Single(await service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(
                projectPath,
                "Book1",
                selection,
                scopeKey),
            CancellationToken.None));

        Assert.True(result.DiscoveryResult.IsFailure);
        var state = cache.CaptureSelectionState(selection.References, scopeKey);
        Assert.Equal(revisionBefore, state.Revision);
        Assert.Contains(
            state.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "ScopedLastKnownGoodType");
    }

    [Fact]
    public async Task ContextSpecificRefreshDoesNotAdoptNameOnlyPersistedCatalogAsScopedLastKnownGood()
    {
        const string referenceName = "Ambiguous Library";
        const string scopeKey = "project-b-scope";
        var persistedEntry = new VbaProjectReferenceCatalogPersistentEntry(
            new VbaProjectReferenceCatalogIdentity(
                referenceName,
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0,
                0,
                @"C:\TypeLibs\ProjectA.tlb"),
            CreateReferenceCatalog(referenceName, "ProjectAType"));
        var persistentStore = new SingleEntryPersistentStore(persistedEntry);
        var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
        var service = new VbaProjectReferenceCatalogRefreshService(
            cache,
            new FailingContextCatalogDiscovery(),
            persistentStore);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference(referenceName)]);

        var results = await service.RefreshAutomaticallyAsync(
            new VbaProjectReferenceCatalogRefreshContext(
                Path.GetFullPath(@"C:\work\ProjectB"),
                "Book1",
                selection,
                scopeKey,
                SelectionFingerprint: "project-b-fingerprint"),
            CancellationToken.None);

        Assert.Equal(0, persistentStore.LoadCount);
        Assert.Contains(results, result => result.DiscoveryResult.IsFailure);
        var state = cache.CaptureSelectionState(selection.References, scopeKey);
        Assert.DoesNotContain(
            state.CatalogSet.GetActiveDefinitions(selection),
            definition => definition.Name == "ProjectAType");
    }

    [Fact]
    public async Task ScopedPersistentCatalogsRemainIsolatedAcrossLanguageServerSessions()
    {
        const string referenceName = "Ambiguous Library";
        const string selectionFingerprint = "shared-fingerprint";
        const string projectAScope = "project-a-scope";
        const string projectBScope = "project-b-scope";
        var projectAPath = Path.GetFullPath(@"C:\work\ProjectA");
        var projectBPath = Path.GetFullPath(@"C:\work\ProjectB");
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-scoped-catalog-store-").FullName;
        try
        {
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference(referenceName)]);
            var initialService = new VbaProjectReferenceCatalogRefreshService(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty),
                new ProjectContextCatalogDiscoveryFactory(),
                store);
            await initialService.RefreshAutomaticallyAsync(
                new VbaProjectReferenceCatalogRefreshContext(
                    projectAPath,
                    "Book1",
                    selection,
                    projectAScope,
                    selectionFingerprint),
                CancellationToken.None);
            await initialService.RefreshAutomaticallyAsync(
                new VbaProjectReferenceCatalogRefreshContext(
                    projectBPath,
                    "Book1",
                    selection,
                    projectBScope,
                    selectionFingerprint),
                CancellationToken.None);

            var resumedCache = new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.Empty);
            var resumedDiscovery = new FailingContextCatalogDiscovery();
            var resumedService = new VbaProjectReferenceCatalogRefreshService(
                resumedCache,
                resumedDiscovery,
                store);
            var projectAResults = await resumedService.RefreshAutomaticallyAsync(
                new VbaProjectReferenceCatalogRefreshContext(
                    projectAPath,
                    "Book1",
                    selection,
                    projectAScope,
                    selectionFingerprint),
                CancellationToken.None);
            var projectBResults = await resumedService.RefreshAutomaticallyAsync(
                new VbaProjectReferenceCatalogRefreshContext(
                    projectBPath,
                    "Book1",
                    selection,
                    projectBScope,
                    selectionFingerprint),
                CancellationToken.None);

            Assert.All(
                projectAResults.Concat(projectBResults),
                result => Assert.Equal(
                    VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache,
                    result.Status));
            Assert.Equal(0, resumedDiscovery.CallCount);
            var projectAState = resumedCache.CaptureSelectionState(
                selection.References,
                projectAScope);
            var projectBState = resumedCache.CaptureSelectionState(
                selection.References,
                projectBScope);
            Assert.Contains(
                projectAState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "ProjectAType");
            Assert.DoesNotContain(
                projectAState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "ProjectBType");
            Assert.Contains(
                projectBState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "ProjectBType");
            Assert.DoesNotContain(
                projectBState.CatalogSet.GetActiveDefinitions(selection),
                definition => definition.Name == "ProjectAType");
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryConclusiveOrIncompleteResultsNeverInvokePinnedCli()
    {
        const string referenceName = "Registry Library";
        var processCalls = 0;
        var projectPath = Path.GetFullPath(@"C:\work\RegistryOnly");

        async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            TypeLibRegistryCatalog registryCatalog)
        {
            var factory = new VbaDevReferenceListCatalogDiscoveryFactory(
                new TypeLibReferenceCatalogDiscovery(
                    new FakeTypeLibRegistryCatalogReader(registryCatalog),
                    new FakeTypeLibCatalogMetadataReader(
                        new TypeLibCatalogMetadata("Registry", []))),
                Path.GetFullPath(@"C:\tools\vba-dev.exe"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref processCalls);
                    throw new InvalidOperationException("The CLI must not run.");
                });
            var selection = VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference(referenceName)]);
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var service = new VbaProjectReferenceCatalogRefreshService(cache, factory);
            return Assert.Single(await service.RefreshAutomaticallyAsync(
                new VbaProjectReferenceCatalogRefreshContext(projectPath, "Book1", selection),
                CancellationToken.None)).DiscoveryResult;
        }

        var unique = await DiscoverAsync(CreateNeutralRegistryCatalog(
            referenceName,
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var missing = await DiscoverAsync(new TypeLibRegistryCatalog(
            complete: true,
            names: [],
            warnings: [],
            diagnostic: null));
        var incomplete = await DiscoverAsync(new TypeLibRegistryCatalog(
            complete: false,
            names: [],
            warnings: [],
            diagnostic: new TypeLibRegistryCatalogDiagnostic(
                "registryCatalogIncomplete",
                "The registry catalog is incomplete.")));

        Assert.True(unique.HasUsableCatalog);
        Assert.True(missing.IsFailure);
        Assert.True(incomplete.IsFailure);
        Assert.Equal(0, processCalls);
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
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);

        public TypeLibRegistryCatalog Read()
        {
            Interlocked.Increment(ref readCount);
            return catalog;
        }
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

    private sealed class FailingContextCatalogDiscovery
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The project-specific identity could not be resolved."));
        }

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
            => this;
    }

    private sealed class ProjectContextCatalogDiscoveryFactory
        : IVbaProjectReferenceCatalogDiscovery,
          IVbaProjectReferenceCatalogContextDiscoveryFactory
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "Context-free discovery is not authoritative for this test."));

        public IVbaProjectReferenceCatalogDiscovery CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
        {
            var isProjectA = context.ProjectPath.EndsWith(
                "ProjectA",
                StringComparison.OrdinalIgnoreCase);
            return new InlineCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    "Ambiguous Library",
                    isProjectA
                        ? "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
                        : "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    1,
                    0,
                    0,
                    isProjectA
                        ? @"C:\TypeLibs\ProjectA.tlb"
                        : @"C:\TypeLibs\ProjectB.tlb"),
                CreateReferenceCatalog(
                    "Ambiguous Library",
                    isProjectA ? "ProjectAType" : "ProjectBType")));
        }
    }

    private sealed class SingleEntryPersistentStore(VbaProjectReferenceCatalogPersistentEntry entry)
        : IVbaProjectReferenceCatalogPersistentStore
    {
        public int LoadCount { get; private set; }

        public Task<VbaProjectReferenceCatalogPersistentLoadResult> LoadAsync(
            string referenceName,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            return Task.FromResult(VbaProjectReferenceCatalogPersistentLoadResult.Current(entry));
        }

        public Task SaveAsync(
            VbaProjectReferenceCatalogPersistentEntry savedEntry,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
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

    private sealed class InlineCatalogDiscovery(VbaProjectReferenceCatalogDiscoveryResult result)
        : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class BlockingMutationLane : IVbaProjectReferenceCatalogMutationLane
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CommitAsync(
            string authorityKey,
            Action commit,
            CancellationToken cancellationToken)
        {
            CommitStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            commit();
        }

        public void Release() => release.TrySetResult();
    }
}
