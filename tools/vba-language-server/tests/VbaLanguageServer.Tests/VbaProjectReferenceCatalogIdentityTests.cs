using VbaDev.Domain;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectReferenceCatalogIdentityTests
{
    [Fact]
    public void Reference_selection_fingerprint_is_typed_canonical_and_order_sensitive()
    {
        var firstSelection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference(" Office "),
                new VbaProjectReference("Scripting")
            ]);
        var equivalentSelection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind.ToUpperInvariant(),
            [
                new VbaProjectReference("office", requested: false),
                new VbaProjectReference(" scripting ", requested: false)
            ]);
        var reorderedSelection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference("Scripting"),
                new VbaProjectReference("Office")
            ]);

        var first = ReferenceSelectionFingerprint.Create(
            $" {ProjectDocument.ExcelKind} ",
            firstSelection);
        var equivalent = ReferenceSelectionFingerprint.Create(
            ProjectDocument.ExcelKind.ToUpperInvariant(),
            equivalentSelection);
        var reordered = ReferenceSelectionFingerprint.Create(
            ProjectDocument.ExcelKind,
            reorderedSelection);

        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(first, reordered);
        Assert.DoesNotContain(
            typeof(ReferenceSelectionFingerprint).GetConstructors(),
            constructor => constructor.IsPublic);
    }

    [Fact]
    public void Reference_selection_fingerprint_does_not_use_delimiter_joined_equality()
    {
        var singleToken = ReferenceSelectionFingerprint.Create(
            "word",
            VbaProjectReferenceSelection.Create(
                "word",
                [new VbaProjectReference("A\u001eB")]));
        var twoTokens = ReferenceSelectionFingerprint.Create(
            "word",
            VbaProjectReferenceSelection.Create(
                "word",
                [
                    new VbaProjectReference("A"),
                    new VbaProjectReference("B")
                ]));

        Assert.NotEqual(singleToken, twoTokens);
    }

    [Fact]
    public void Catalog_scope_retains_authority_when_only_non_reference_snapshot_facts_change()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-reference-catalog-identity");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var baseline = CreateResolution(
            root,
            manifestPath,
            sourceTemplatePath: Path.Combine(root, "Before.xlsm"),
            commonModuleFile: "Before.bas");
        var changed = CreateResolution(
            Path.Combine(root, "other-source-root"),
            manifestPath,
            sourceTemplatePath: Path.Combine(root, "After.xlsm"),
            commonModuleFile: "After.bas");

        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                baseline,
                out var baselineScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                changed,
                out var changedScope));

        Assert.Equal(baselineScope, changedScope);
        Assert.NotEqual(
            CreateSnapshotIdentity(
                new Uri(Path.Combine(root, "Main.bas")).AbsoluteUri,
                baseline),
            CreateSnapshotIdentity(
                new Uri(Path.Combine(root, "Other.bas")).AbsoluteUri,
                changed));
    }

    [Fact]
    public void Refresh_authority_excludes_selection_while_automatic_work_keeps_it()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-reference-refresh-identity");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var first = CreateResolution(
            root,
            manifestPath,
            sourceTemplatePath: null,
            commonModuleFile: null);
        var reordered = first with
        {
            References =
            [
                new VbaProjectReference("Scripting"),
                new VbaProjectReference("Office")
            ]
        };
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                first,
                out var firstScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                reordered,
                out var reorderedScope));

        Assert.NotEqual(firstScope, reorderedScope);
        Assert.Equal(
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                firstScope,
                " Library "),
            VbaProjectReferenceCatalogRefreshAuthorityIdentity.Create(
                reorderedScope,
                "library"));
        Assert.NotEqual(
            VbaProjectReferenceCatalogAutomaticWorkIdentity.Create(
                firstScope.Fingerprint,
                firstScope.Authority),
            VbaProjectReferenceCatalogAutomaticWorkIdentity.Create(
                reorderedScope.Fingerprint,
                reorderedScope.Authority));
    }

    [Fact]
    public void Snapshot_ordering_is_consistent_with_equality_and_structural_tokens()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-snapshot-ordering");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var baseline = CreateResolution(
            root,
            manifestPath,
            sourceTemplatePath: null,
            commonModuleFile: "A\u001fB.bas");
        var caseEquivalent = baseline with
        {
            DocumentName = "book1",
            DocumentKind = ProjectDocument.ExcelKind.ToUpperInvariant(),
            References = baseline.ReferenceEntries
                .Select(reference => new VbaProjectReference(
                    reference.Name.ToUpperInvariant(),
                    requested: false))
                .ToArray()
        };
        var splitTokens = baseline with
        {
            CommonModules =
            [
                new InstalledCommonModule(
                    "First",
                    "A",
                    Requested: true,
                    TestOnly: false),
                new InstalledCommonModule(
                    "Second",
                    "B.bas",
                    Requested: true,
                    TestOnly: false)
            ]
        };
        var activeUri = new Uri(Path.Combine(root, "Main.bas")).AbsoluteUri;
        var baselineIdentity = CreateSnapshotIdentity(
            activeUri,
            baseline);
        var equivalentIdentity = CreateSnapshotIdentity(
            activeUri,
            caseEquivalent);
        var splitIdentity = CreateSnapshotIdentity(
            activeUri,
            splitTokens);

        Assert.Equal(baselineIdentity, equivalentIdentity);
        Assert.Equal(0, baselineIdentity.CompareTo(equivalentIdentity));
        Assert.NotEqual(baselineIdentity, splitIdentity);
        Assert.NotEqual(0, baselineIdentity.CompareTo(splitIdentity));
    }

    [Fact]
    public void Scoped_persistent_paths_follow_typed_equality_without_delimiter_collisions()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-reference-persistence");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var first = CreateResolution(
            root,
            manifestPath,
            sourceTemplatePath: null,
            commonModuleFile: null);
        var caseEquivalent = first with
        {
            ManifestPath = Path.Combine(
                root,
                "nested",
                "..",
                "VBA-PROJECT.JSON"),
            DocumentName = "book1",
            DocumentKind = ProjectDocument.ExcelKind.ToUpperInvariant(),
            References = first.ReferenceEntries
                .Select(reference => new VbaProjectReference(
                    reference.Name.ToUpperInvariant(),
                    requested: false))
                .ToArray()
        };
        var delimiterSingle = first with
        {
            DocumentKind = "word",
            References = [new VbaProjectReference("A\u001eB")]
        };
        var delimiterSplit = delimiterSingle with
        {
            References =
            [
                new VbaProjectReference("A"),
                new VbaProjectReference("B")
            ]
        };
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                first,
                out var firstScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                caseEquivalent,
                out var equivalentScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                delimiterSingle,
                out var delimiterSingleScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                delimiterSplit,
                out var delimiterSplitScope));
        Assert.Equal(firstScope, equivalentScope);
        Assert.Equal(
            firstScope.CreatePersistentKey(" Library "),
            equivalentScope.CreatePersistentKey("library"));
        Assert.NotEqual(delimiterSingleScope, delimiterSplitScope);
        Assert.NotEqual(
            delimiterSingleScope.CreatePersistentKey("Library"),
            delimiterSplitScope.CreatePersistentKey("Library"));
    }

    [Fact]
    public async Task Scoped_persistent_store_loads_equal_scope_and_isolates_structural_selections()
    {
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-language-server-typed-scope-").FullName;
        try
        {
            var manifestPath = Path.Combine(cacheRoot, "vba-project.json");
            var first = CreateResolution(
                cacheRoot,
                manifestPath,
                sourceTemplatePath: null,
                commonModuleFile: null);
            var equivalent = first with
            {
                ManifestPath = Path.Combine(
                    cacheRoot,
                    "nested",
                    "..",
                    "VBA-PROJECT.JSON"),
                DocumentName = "book1",
                References = first.ReferenceEntries
                    .Select(reference => new VbaProjectReference(
                        reference.Name.ToUpperInvariant(),
                        requested: false))
                    .ToArray()
            };
            var delimiterSingle = first with
            {
                DocumentKind = "word",
                References = [new VbaProjectReference("A\u001eB")]
            };
            var delimiterSplit = delimiterSingle with
            {
                References =
                [
                    new VbaProjectReference("A"),
                    new VbaProjectReference("B")
                ]
            };
            Assert.True(
                VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                    first,
                    out var firstScope));
            Assert.True(
                VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                    equivalent,
                    out var equivalentScope));
            Assert.True(
                VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                    delimiterSingle,
                    out var delimiterSingleScope));
            Assert.True(
                VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                    delimiterSplit,
                    out var delimiterSplitScope));
            var store = new VbaProjectReferenceCatalogPersistentStore(
                Path.Combine(cacheRoot, "cache"));
            var firstEntry = CreatePersistentEntry(
                "Library",
                "11111111-1111-1111-1111-111111111111");
            var delimiterSingleEntry = CreatePersistentEntry(
                "Library",
                "22222222-2222-2222-2222-222222222222");
            var delimiterSplitEntry = CreatePersistentEntry(
                "Library",
                "33333333-3333-3333-3333-333333333333");

            await store.SaveScopedAsync(
                firstEntry,
                firstScope,
                CancellationToken.None);
            var equivalentLoad = await store.LoadScopedAsync(
                " library ",
                equivalentScope,
                CancellationToken.None);
            await store.SaveScopedAsync(
                delimiterSingleEntry,
                delimiterSingleScope,
                CancellationToken.None);
            await store.SaveScopedAsync(
                delimiterSplitEntry,
                delimiterSplitScope,
                CancellationToken.None);
            var delimiterSingleLoad = await store.LoadScopedAsync(
                "Library",
                delimiterSingleScope,
                CancellationToken.None);
            var delimiterSplitLoad = await store.LoadScopedAsync(
                "Library",
                delimiterSplitScope,
                CancellationToken.None);

            Assert.Equal(
                firstEntry.Identity.Guid,
                Assert.IsType<VbaProjectReferenceCatalogPersistentEntry>(
                    equivalentLoad.Entry).Identity.Guid);
            Assert.Equal(
                delimiterSingleEntry.Identity.Guid,
                Assert.IsType<VbaProjectReferenceCatalogPersistentEntry>(
                    delimiterSingleLoad.Entry).Identity.Guid);
            Assert.Equal(
                delimiterSplitEntry.Identity.Guid,
                Assert.IsType<VbaProjectReferenceCatalogPersistentEntry>(
                    delimiterSplitLoad.Entry).Identity.Guid);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void Scoped_cache_isolates_bindings_for_distinct_reference_selections()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-reference-scope-cache");
        var first = CreateResolution(
            root,
            Path.Combine(root, "vba-project.json"),
            sourceTemplatePath: null,
            commonModuleFile: null);
        var reordered = first with
        {
            References =
            [
                new VbaProjectReference("Scripting"),
                new VbaProjectReference("Office")
            ]
        };
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                first,
                out var firstScope));
        Assert.True(
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                reordered,
                out var reorderedScope));
        Assert.NotEqual(firstScope, reorderedScope);

        var firstEntry = CreatePersistentEntry(
            "Office",
            "11111111-1111-1111-1111-111111111111");
        var reorderedEntry = CreatePersistentEntry(
            "Office",
            "22222222-2222-2222-2222-222222222222");
        var cache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.Empty);
        cache.StorePersistedCatalog(
            firstEntry,
            firstScope,
            identityAuthoritative: true);
        cache.StorePersistedCatalog(
            reorderedEntry,
            reorderedScope,
            identityAuthoritative: true);

        Assert.Equal(
            firstEntry.Identity.Guid,
            cache.CaptureSelectionState(
                first.ReferenceEntries,
                firstScope).Identities["Office"].Guid);
        Assert.Equal(
            reorderedEntry.Identity.Guid,
            cache.CaptureSelectionState(
                reordered.ReferenceEntries,
                reorderedScope).Identities["Office"].Guid);
        var scopedBindings = typeof(VbaProjectReferenceCatalogCache).GetField(
            "scopedBindings",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(scopedBindings);
        Assert.Equal(
            typeof(VbaProjectReferenceCatalogScopeIdentity),
            scopedBindings.FieldType.GetGenericArguments()[0]);
    }

    private static VbaProjectResolution CreateResolution(
        string sourceRoot,
        string manifestPath,
        string? sourceTemplatePath,
        string? commonModuleFile)
        => new(
            VbaProjectResolutionKind.ManifestDocument,
            sourceRoot,
            manifestPath,
            "Book1",
            ProjectDocument.ExcelKind,
            References:
            [
                new VbaProjectReference("Office"),
                new VbaProjectReference("Scripting")
            ],
            SourceTemplatePath: sourceTemplatePath,
            CommonModules: commonModuleFile is null
                ? []
                :
                [
                    new InstalledCommonModule(
                        "Shared",
                        commonModuleFile,
                        Requested: true,
                        TestOnly: false)
                ]);

    private static VbaProjectReferenceCatalogPersistentEntry CreatePersistentEntry(
        string referenceName,
        string guid)
        => new(
            new VbaProjectReferenceCatalogIdentity(
                referenceName,
                guid,
                1,
                0,
                0,
                @"C:\TypeLibs\Library.tlb"),
            new VbaProjectReferenceCatalog(
                referenceName,
                [],
                []));

    private static VbaProjectSnapshotIdentity CreateSnapshotIdentity(
        string activeUri,
        VbaProjectResolution resolution)
        => VbaProjectIdentityModel.TryIdentifyDocument(
                activeUri,
                out var activeDocumentIdentity)
            ? VbaProjectSnapshotIdentity.Create(
                activeDocumentIdentity,
                resolution)
            : throw new InvalidOperationException(
                "The test active document must have a typed identity.");
}
