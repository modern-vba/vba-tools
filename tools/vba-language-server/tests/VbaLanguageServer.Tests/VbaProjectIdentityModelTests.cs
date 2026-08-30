using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectIdentityModelTests
{
    [Fact]
    public void Document_identity_canonicalizes_equivalent_file_uris()
    {
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-document-identity");
        var canonicalUri = new Uri(
            Path.Combine(sourceRoot, "Module.bas"))
            .AbsoluteUri;
        var equivalentUri = new Uri(
            sourceRoot + Path.DirectorySeparatorChar)
            .AbsoluteUri
            + "Nested/../Module.bas";

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                canonicalUri,
                out var canonical));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                equivalentUri,
                out var equivalent));
        Assert.Equal(canonical, equivalent);
    }

    [Fact]
    public void Document_identity_normalizes_non_file_uris_without_using_them_as_authorities()
    {
        const string firstUri =
            "untitled://WORKSPACE/Folder/../Module.bas";
        const string secondUri =
            "untitled://workspace/Module.bas";

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                firstUri,
                out var first));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                secondUri,
                out var second));
        Assert.Equal(first, second);
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    RootPath: ""),
                out _));
    }

    [Fact]
    public void Distinct_document_uris_deduplicate_through_typed_document_identity()
    {
        const string canonicalUri =
            "untitled://workspace/Module.bas";
        const string equivalentUri =
            "untitled://WORKSPACE/Folder/../Module.bas";
        const string unidentified = "not a uri";

        Assert.Equal(
            [canonicalUri, unidentified],
            VbaProjectIdentityModel.DistinctDocumentUris(
                [canonicalUri, equivalentUri, unidentified, unidentified]));
    }

    [Fact]
    public void Raw_local_paths_cannot_enter_the_document_uri_identity_boundary()
    {
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                Path.Combine(Path.GetTempPath(), "Module.bas"),
                out _));
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "C:\\work\\Module.bas",
                out _));
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "C:work\\Module.bas",
                out _));
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "C:work/Module.bas",
                out _));
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "\\\\server\\share\\Module.bas",
                out _));
    }

    [Fact]
    public void Project_identity_relations_do_not_accept_raw_uri_strings()
    {
        var identityMethods = typeof(VbaProjectIdentityModel).GetMethods(
            System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);

        Assert.DoesNotContain(
            identityMethods,
            method => method.Name == "UsesManifestUri");
        Assert.DoesNotContain(
            identityMethods,
            method => method.Name == "Relate"
                && method.GetParameters()[0].ParameterType == typeof(string));
    }

    [Fact]
    public void Workspace_document_state_and_snapshot_capture_use_typed_identity()
    {
        var workspaceType = typeof(VbaLanguageWorkspace);
        foreach (var fieldName in new[] { "documents", "acceptedRevisions" })
        {
            var field = workspaceType.GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(
                typeof(VbaDocumentIdentity),
                field.FieldType.GetGenericArguments()[0]);
        }

        Assert.Null(
            workspaceType.GetMethod(
                "FindDocumentKey",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic));
        Assert.Null(
            workspaceType.GetMethod(
                "FindAcceptedRevisionKey",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic));
        Assert.Null(typeof(VbaWorkspaceSnapshotState).GetProperty("Documents"));
        Assert.DoesNotContain(
            typeof(VbaProjectSnapshotBuilder)
                .GetMethod("CreateInventorySnapshot")!
                .GetParameters(),
            parameter => parameter.ParameterType
                == typeof(IReadOnlyDictionary<string, VbaTrackedDocument>));
    }

    [Fact]
    public void Disk_project_scope_keeps_authority_typed_without_a_raw_manifest_identity()
    {
        var scopeType = typeof(VbaProjectDiskProjectScope);

        Assert.Equal(
            typeof(VbaProjectAuthorityIdentity?),
            scopeType.GetProperty("AuthorityIdentity")?.PropertyType);
        Assert.Null(scopeType.GetProperty("OwningManifestPath"));
    }

    [Fact]
    public void Manifest_barrier_cache_uses_typed_document_identities()
    {
        var snapshotType = typeof(VbaProjectManifestBarrierSnapshot);

        Assert.Equal(
            typeof(IReadOnlyDictionary<VbaDocumentIdentity, bool>),
            snapshotType.GetProperty("Overrides")?.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<VbaDocumentIdentity, long>),
            snapshotType.GetProperty("ReconciliationRevisions")?.PropertyType);
    }

    [Fact]
    public void Manifest_workspace_cache_and_revision_fences_use_typed_document_identity()
    {
        var workspaceType = typeof(VbaProjectManifestWorkspace);
        foreach (var fieldName in new[]
        {
            "states",
            "reconciliationRevisions",
            "effectiveScopeRevisions",
            "reconciliationBaselines",
            "lastKnownGoodDiskManifests"
        })
        {
            var field = workspaceType.GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(
                typeof(VbaDocumentIdentity),
                field.FieldType.GetGenericArguments()[0]);
        }

        var getRevision = Assert.Single(
            typeof(IVbaProjectManifestResolutionSource).GetMethods(),
            method => method.Name == "GetRevision");
        Assert.Equal(
            typeof(VbaIdentifiedDocument),
            getRevision.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Reconciliation_scope_does_not_expose_open_source_uri_identities()
    {
        Assert.Null(
            typeof(VbaProjectReconciliationScope)
                .GetProperty("OpenSourceUris"));
        Assert.Null(
            typeof(VbaProjectReconciliationScope)
                .GetProperty("OpenDocumentUris"));
        Assert.Null(
            typeof(ReconciliationChange)
                .GetProperty("CapturedOpenSourceUris"));
        Assert.Equal(
            typeof(IReadOnlyList<VbaIdentifiedDocument>),
            typeof(VbaProjectReconciliationScope)
                .GetProperty("OpenSources")?.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<VbaDocumentIdentity>),
            typeof(ReconciliationChange)
                .GetProperty("CapturedOpenSourceIdentities")?.PropertyType);
    }

    [Fact]
    public void Source_revision_and_snapshot_invalidation_fences_accept_typed_identity()
    {
        var record = Assert.Single(
            typeof(VbaSourceRevisionHistory).GetMethods(),
            method => method.Name == "Record");
        var getRevision = Assert.Single(
            typeof(VbaSourceRevisionHistory).GetMethods(),
            method => method.Name == "GetRevision");
        var invalidateSource = Assert.Single(
            typeof(VbaProjectSnapshotProvider).GetMethods(),
            method => method.Name == "InvalidateSource");
        var retireInactiveScopes = Assert.Single(
            typeof(VbaProjectSnapshotProvider).GetMethods(),
            method => method.Name == "RetireInactiveScopes");

        Assert.Equal(
            typeof(VbaIdentifiedDocument),
            record.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(VbaDocumentIdentity),
            getRevision.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(VbaIdentifiedDocument),
            invalidateSource.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyList<VbaIdentifiedDocument>),
            retireInactiveScopes.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Manifest_reconciliation_interfaces_accept_typed_document_identity()
    {
        var targetConstructor = Assert.Single(
            typeof(VbaProjectManifestReconciliationTarget)
                .GetConstructors());
        Assert.Equal(
            typeof(VbaIdentifiedDocument),
            targetConstructor.GetParameters()[0].ParameterType);

        foreach (var methodName in new[]
        {
            "CaptureReconciliationState",
            "GetReconciliationBaseline",
            "GetReconciliationRevision",
            "ReloadReconciledManifest",
            "DeleteReconciledManifest"
        })
        {
            var method = Assert.Single(
                typeof(VbaProjectManifestWorkspace).GetMethods(),
                candidate => candidate.Name == methodName);
            Assert.Equal(
                typeof(VbaIdentifiedDocument),
                method.GetParameters()[0].ParameterType);
        }
    }

    [Fact]
    public void Snapshot_provider_cache_entrypoints_accept_typed_documents()
    {
        var providerType = typeof(VbaProjectSnapshotProvider);
        var createOne = Assert.Single(
            providerType.GetMethods(),
            method => method.Name == "CreateProjectSnapshot");
        var createMany = Assert.Single(
            providerType.GetMethods(),
            method => method.Name == "CreateProjectSnapshots");
        var applyHostProjection = Assert.Single(
            providerType.GetMethods(),
            method => method.Name
                == "TryApplyHostClassProjectionSnapshot");

        Assert.Equal(
            typeof(VbaIdentifiedDocument),
            createOne.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyList<VbaIdentifiedDocument>),
            createMany.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyList<VbaIdentifiedDocument>),
            applyHostProjection.GetParameters()[1].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<
                VbaDocumentIdentity,
                VbaTrackedDocument>),
            typeof(VbaWorkspaceSnapshotState)
                .GetProperty("DocumentsByIdentity")?.PropertyType);
        Assert.Equal(
            typeof(Dictionary<
                VbaDocumentIdentity,
                VbaTrackedDocument>),
            typeof(VbaProjectSourceInventorySnapshot)
                .GetProperty("DocumentsByIdentity")?.PropertyType);
    }

    [Fact]
    public void Transferred_project_ownership_accepts_typed_document_identity()
    {
        var root = CreateRoot("typed-transfer-ownership");
        var resolution = ManifestResolution(
            Path.Combine(root, "src"),
            Path.Combine(root, "vba-project.json"),
            "Book1");
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                new Uri(Path.Combine(root, "src", "Module.bas")).AbsoluteUri,
                out var documentIdentity));

        Assert.True(
            VbaProjectIdentityModel.OwnsTransferredProjectDocument(
                resolution,
                documentIdentity));
    }

    [Fact]
    public void Manifest_authority_excludes_snapshot_forming_inputs()
    {
        var root = CreateRoot("manifest-authority");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var first = ManifestResolution(
            Path.Combine(root, "src", "Before"),
            manifestPath,
            "Book1") with
        {
            DocumentKind = "excel",
            References = [new VbaProjectReference("Office")],
            CommonModules =
            [
                new InstalledCommonModule(
                    "First",
                    "First.bas",
                    Requested: true,
                    TestOnly: false)
            ]
        };
        var second = ManifestResolution(
            Path.Combine(root, "src", "After"),
            manifestPath,
            "book1") with
        {
            DocumentKind = "word",
            References = [new VbaProjectReference("Excel")],
            CommonModules =
            [
                new InstalledCommonModule(
                    "Second",
                    "Second.bas",
                    Requested: false,
                    TestOnly: true)
            ]
        };

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                first,
                out var firstIdentity));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                second,
                out var secondIdentity));
        Assert.Equal(firstIdentity, secondIdentity);

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                second with { DocumentName = "Book2" },
                out var otherDocument));
        Assert.NotEqual(firstIdentity, otherDocument);
    }

    [Fact]
    public void Snapshot_identity_canonicalizes_equivalent_snapshot_forming_facts()
    {
        var root = CreateRoot("snapshot-canonicalization");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var sourceRoot = Path.Combine(root, "src", "Book1");
        var sourceTemplatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var first = ManifestResolution(
            sourceRoot,
            manifestPath,
            "Book1") with
        {
            DocumentKind = " excel ",
            References =
            [
                new VbaProjectReference(" Office "),
                new VbaProjectReference("Scripting")
            ],
            SourceTemplatePath = sourceTemplatePath,
            CommonModules =
            [
                new InstalledCommonModule(
                    "Second",
                    "Second.bas",
                    Requested: true,
                    TestOnly: false),
                new InstalledCommonModule(
                    "First",
                    "First.bas",
                    Requested: false,
                    TestOnly: true)
            ]
        };
        var equivalent = ManifestResolution(
            Path.Combine(sourceRoot, "Nested", ".."),
            Path.Combine(root, "Nested", "..", "vba-project.json"),
            "book1") with
        {
            DocumentKind = "EXCEL",
            References =
            [
                new VbaProjectReference("office", requested: false),
                new VbaProjectReference(" scripting ", requested: false)
            ],
            SourceTemplatePath = Path.Combine(
                root,
                "templates",
                "Nested",
                "..",
                "Book1.xlsm"),
            CommonModules =
            [
                new InstalledCommonModule(
                    "First renamed",
                    "first.BAS",
                    Requested: true,
                    TestOnly: false,
                    Orphaned: true),
                new InstalledCommonModule(
                    "Second renamed",
                    "SECOND.bas",
                    Requested: false,
                    TestOnly: true,
                    Orphaned: true)
            ]
        };

        var firstIdentity = CreateSnapshotIdentity(
            new Uri(Path.Combine(sourceRoot, "First.bas")).AbsoluteUri,
            first);
        var equivalentIdentity = CreateSnapshotIdentity(
            new Uri(Path.Combine(sourceRoot, "Second.bas")).AbsoluteUri,
            equivalent);

        Assert.Equal(firstIdentity, equivalentIdentity);
        Assert.Equal(
            firstIdentity.GetHashCode(),
            equivalentIdentity.GetHashCode());
    }

    [Fact]
    public void Snapshot_forming_changes_replace_snapshot_identity_without_replacing_authority()
    {
        var root = CreateRoot("snapshot-replacement");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var sourceRoot = Path.Combine(root, "src", "Book1");
        var activeUri = new Uri(
            Path.Combine(sourceRoot, "Main.bas"))
            .AbsoluteUri;
        var baseline = ManifestResolution(
            sourceRoot,
            manifestPath,
            "Book1") with
        {
            DocumentKind = "excel",
            References = [new VbaProjectReference("Office")],
            SourceTemplatePath = Path.Combine(root, "templates", "Book1.xlsm"),
            CommonModules =
            [
                new InstalledCommonModule(
                    "Shared",
                    "Shared.bas",
                    Requested: true,
                    TestOnly: false)
            ]
        };
        VbaProjectResolution[] replacements =
        [
            baseline with
            {
                RootPath = Path.Combine(root, "src", "Moved")
            },
            baseline with { DocumentKind = "word" },
            baseline with
            {
                References = [new VbaProjectReference("Scripting")]
            },
            baseline with
            {
                SourceTemplatePath = Path.Combine(
                    root,
                    "templates",
                    "Other.xlsm")
            },
            baseline with
            {
                CommonModules =
                [
                    new InstalledCommonModule(
                        "Other",
                        "Other.bas",
                        Requested: true,
                        TestOnly: false)
                ]
            }
        ];

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                baseline,
                out var baselineAuthority));
        var baselineSnapshot = CreateSnapshotIdentity(
            activeUri,
            baseline);
        foreach (var replacement in replacements)
        {
            Assert.True(
                VbaProjectIdentityModel.TryIdentifyAuthority(
                    replacement,
                    out var replacementAuthority));
            Assert.Equal(baselineAuthority, replacementAuthority);
            Assert.NotEqual(
                baselineSnapshot,
                CreateSnapshotIdentity(
                    activeUri,
                    replacement));
        }
    }

    [Fact]
    public void Snapshot_identity_stays_separate_from_document_and_disk_content_identity()
    {
        var root = CreateRoot("snapshot-separation");
        var sourceRoot = Path.Combine(root, "src", "Book1");
        var resolution = ManifestResolution(
            sourceRoot,
            Path.Combine(root, "vba-project.json"),
            "Book1") with
        {
            DocumentKind = "excel"
        };
        var firstUri = new Uri(
            Path.Combine(sourceRoot, "First.bas"))
            .AbsoluteUri;
        var secondUri = new Uri(
            Path.Combine(sourceRoot, "Second.bas"))
            .AbsoluteUri;

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                firstUri,
                out var firstDocument));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                secondUri,
                out var secondDocument));
        Assert.NotEqual(firstDocument, secondDocument);
        Assert.Equal(
            CreateSnapshotIdentity(firstUri, resolution),
            CreateSnapshotIdentity(secondUri, resolution));
        Assert.NotEqual(
            VbaProjectDiskContentIdentity.FromText("before"),
            VbaProjectDiskContentIdentity.FromText("after"));
    }

    [Fact]
    public void Snapshot_identity_is_opaque_and_is_the_cache_table_key()
    {
        var identityType = typeof(VbaProjectSnapshotIdentity);
        Assert.Null(identityType.GetProperty("Key"));
        Assert.All(
            identityType.GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.Equal(
            typeof(VbaProjectAuthorityIdentity?),
            identityType.GetField(
                "authority",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)
                ?.FieldType);
        Assert.Equal(
            typeof(VbaDocumentIdentity),
            Assert.Single(
                identityType.GetMethod("Create")!.GetParameters(),
                parameter => parameter.Name == "activeDocumentIdentity")
                .ParameterType);

        var providerType = typeof(VbaProjectSnapshotProvider);
        foreach (var fieldName in new[]
        {
            "cache",
            "scopeInvalidationStates",
            "scopeAuthoritySeeds"
        })
        {
            var field = providerType.GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(
                identityType,
                field.FieldType.GetGenericArguments()[0]);
        }

        foreach (var nestedTypeName in new[]
        {
            "WarmProjectScopeSeed",
            "PreferredRetirementScope"
        })
        {
            var nestedType = providerType.GetNestedType(
                nestedTypeName,
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(nestedType);
            Assert.Equal(
                identityType,
                nestedType.GetProperty("CacheIdentity")?.PropertyType);
        }
    }

    [Fact]
    public void Reconciliation_fence_observers_receive_typed_authority_identity()
    {
        foreach (var observerMethod in new[]
        {
            typeof(IVbaProjectReconciliationAuthorityLeaseObserver)
                .GetMethod("AuthorityLeaseAcquired"),
            typeof(IVbaProjectDiskReconciliationCommitObserver)
                .GetMethod("ScopeFenceValidated")
        })
        {
            Assert.NotNull(observerMethod);
            Assert.Equal(
                typeof(VbaProjectAuthorityIdentity),
                observerMethod.GetParameters()[0].ParameterType);
        }
    }

    [Fact]
    public void Reconciliation_progress_does_not_expose_a_composite_string_identity()
    {
        var identityProperty = typeof(VbaProjectReconciliationProgress)
            .GetProperty("Identity");

        Assert.NotNull(identityProperty);
        Assert.NotEqual(typeof(string), identityProperty.PropertyType);
    }

    [Fact]
    public void Rejected_reconciliation_progress_identity_is_structural()
    {
        var root = CreateRoot("reconciliation-progress-identity");
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                ManifestResolution(
                    Path.Combine(root, "src"),
                    Path.Combine(root, "vba-project.json"),
                    "Book1"),
                out var authority));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "untitled://workspace/A@1,B",
                out var singleDocument));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "untitled://workspace/A",
                out var firstDocument));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                "untitled://workspace/B",
                out var secondDocument));

        var single = new VbaProjectReconciliationRejectedProgressIdentity(
            VbaProjectReconciliationRejectionReason.Scope,
            VbaProjectReconciliationMutationKind.Reload,
            authority,
            manifestBarrierRevision: 1,
            authorityGeneration: 2,
            [
                new VbaProjectReconciliationDocumentRevisionIdentity(
                    singleDocument,
                    Revision: 3)
            ],
            fallbackDocumentIdentity: null,
            fallbackRevision: 0);
        var split = new VbaProjectReconciliationRejectedProgressIdentity(
            VbaProjectReconciliationRejectionReason.Scope,
            VbaProjectReconciliationMutationKind.Reload,
            authority,
            manifestBarrierRevision: 1,
            authorityGeneration: 2,
            [
                new VbaProjectReconciliationDocumentRevisionIdentity(
                    firstDocument,
                    Revision: 3),
                new VbaProjectReconciliationDocumentRevisionIdentity(
                    secondDocument,
                    Revision: 3)
            ],
            fallbackDocumentIdentity: null,
            fallbackRevision: 0);

        Assert.NotEqual(single, split);
    }

    [Fact]
    public void Host_projection_coalescing_uses_canonical_project_authority()
    {
        var root = CreateRoot("host-projection-authority");
        var equivalentRoot = Path.Combine(
            root,
            "Nested",
            "..");
        var first = new VbaHostClassProjectionSnapshotUpdate(
            new VbaHostClassProjectionContext(
                root,
                "Book1",
                Path.Combine(root, "Book1.xlsm")),
            Revision: 1,
            Snapshot: null);
        var equivalent = new VbaHostClassProjectionSnapshotUpdate(
            new VbaHostClassProjectionContext(
                equivalentRoot,
                "Book1",
                Path.Combine(root, "Book1.xlsm")),
            Revision: 2,
            Snapshot: null);

        Assert.True(first.TryGetAuthority(out var firstAuthority));
        Assert.True(
            equivalent.TryGetAuthority(out var equivalentAuthority));
        Assert.Equal(firstAuthority, equivalentAuthority);
        Assert.False(
            new VbaHostClassProjectionSnapshotUpdate(
                new VbaHostClassProjectionContext(
                    "\0",
                    "Book1",
                    Path.Combine(root, "Book1.xlsm")),
                Revision: 3,
                Snapshot: null)
                .TryGetAuthority(out _));
        Assert.Null(
            typeof(VbaHostClassProjectionSnapshotUpdate)
                .GetProperty("CoalescingKey"));
    }

    [Theory]
    [MemberData(nameof(AuthorityRelations))]
    public void Authority_relation_matrix_is_subject_document_aware(
        string subjectPath,
        VbaProjectResolution? previous,
        VbaProjectResolution? current,
        string expectedKind,
        bool? previousOwnsSubject,
        bool? currentOwnsSubject,
        bool? sameSourceOwnershipBoundary,
        bool? currentManifestWithinPreviousSourceRoot)
    {
        var subjectUri = new Uri(subjectPath).AbsoluteUri;
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                subjectUri,
                out var subject));

        var relation = VbaProjectIdentityModel.Relate(
            subject,
            previous,
            current);

        Assert.Equal(expectedKind, relation.Kind.ToString());
        Assert.Equal(
            previousOwnsSubject,
            relation.Ownership.PreviousOwnsSubject);
        Assert.Equal(
            currentOwnsSubject,
            relation.Ownership.CurrentOwnsSubject);
        Assert.Equal(
            sameSourceOwnershipBoundary,
            relation.Ownership.SameSourceOwnershipBoundary);
        Assert.Equal(
            currentManifestWithinPreviousSourceRoot,
            relation.Ownership.CurrentManifestWithinPreviousSourceRoot);
    }

    public static IEnumerable<object?[]> AuthorityRelations()
    {
        var root = CreateRoot("authority-relations");
        var outerRoot = Path.Combine(root, "src");
        var outerManifest = Path.Combine(root, "vba-project.json");
        var nestedProjectRoot = Path.Combine(
            outerRoot,
            "NestedProject");
        var nestedRoot = Path.Combine(
            nestedProjectRoot,
            "src");
        var nestedManifest = Path.Combine(
            nestedProjectRoot,
            "vba-project.json");
        var nestedSubject = Path.Combine(nestedRoot, "Module.bas");
        var outer = ManifestResolution(
            outerRoot,
            outerManifest,
            "Outer");
        var nested = ManifestResolution(
            nestedRoot,
            nestedManifest,
            "Inner");

        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            outer with
            {
                DocumentKind = "changed",
                References = [new VbaProjectReference("Office")]
            },
            VbaProjectAuthorityRelationKind.Same.ToString(),
            true,
            true,
            true,
            false
        ];
        yield return
        [
            Path.Combine(root, "src", "After", "Module.bas"),
            ManifestResolution(
                Path.Combine(root, "src", "Before"),
                outerManifest,
                "Outer"),
            ManifestResolution(
                Path.Combine(root, "src", "After"),
                outerManifest,
                "outer"),
            VbaProjectAuthorityRelationKind.Same.ToString(),
            false,
            true,
            false,
            false
        ];
        yield return
        [
            nestedSubject,
            outer,
            nested,
            VbaProjectAuthorityRelationKind.RetainPrevious.ToString(),
            true,
            true,
            false,
            true
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                outerRoot),
            outer,
            VbaProjectAuthorityRelationKind.Replace.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                outerRoot),
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                outerRoot + Path.DirectorySeparatorChar),
            VbaProjectAuthorityRelationKind.Same.ToString(),
            true,
            true,
            true,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                outerRoot),
            VbaProjectAuthorityRelationKind.Replace.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            outer with { DocumentName = "Other" },
            VbaProjectAuthorityRelationKind.Replace.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            ManifestResolution(
                outerRoot,
                Path.Combine(root, "other-vba-project.json"),
                "Outer"),
            VbaProjectAuthorityRelationKind.Replace.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            nestedSubject,
            nested,
            outer,
            VbaProjectAuthorityRelationKind.Replace.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(root, "Other", "Module.bas"),
            outer,
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                Path.Combine(root, "Other")),
            VbaProjectAuthorityRelationKind.Unrelated.ToString(),
            false,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                Path.Combine(root, "Other")),
            VbaProjectAuthorityRelationKind.Unrelated.ToString(),
            true,
            false,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer,
            null,
            VbaProjectAuthorityRelationKind.Indeterminate.ToString(),
            true,
            null,
            null,
            null
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            null,
            outer,
            VbaProjectAuthorityRelationKind.Indeterminate.ToString(),
            null,
            true,
            null,
            null
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            null,
            null,
            VbaProjectAuthorityRelationKind.Indeterminate.ToString(),
            null,
            null,
            null,
            null
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            new VbaProjectResolution(
                VbaProjectResolutionKind.ManifestDocument,
                outerRoot,
                ManifestPath: null,
                DocumentName: "Outer"),
            outer,
            VbaProjectAuthorityRelationKind.Indeterminate.ToString(),
            true,
            true,
            false,
            false
        ];
        yield return
        [
            Path.Combine(outerRoot, "Module.bas"),
            outer with { DocumentName = null },
            outer,
            VbaProjectAuthorityRelationKind.Indeterminate.ToString(),
            true,
            true,
            false,
            false
        ];
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("relative/Module.bas")]
    public void Malformed_or_non_absolute_subject_cannot_enter_typed_relations(
        string subjectUri)
    {
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyDocument(
                subjectUri,
                out _));
    }

    [Fact]
    public void Rootless_authorities_and_non_file_subjects_are_indeterminate()
    {
        var root = CreateRoot("indeterminate-authority");
        var rooted = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            root);
        var rootless = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            RootPath: "");

        var rootlessRelation = VbaProjectIdentityModel.Relate(
            IdentifyDocument(
                new Uri(Path.Combine(root, "Module.bas")).AbsoluteUri),
            rootless,
            rooted);
        var nonFileRelation = VbaProjectIdentityModel.Relate(
            IdentifyDocument("untitled://workspace/Module.bas"),
            rooted,
            rooted);

        Assert.Equal(
            VbaProjectAuthorityRelationKind.Indeterminate,
            rootlessRelation.Kind);
        Assert.Null(rootlessRelation.PreviousAuthority);
        Assert.Equal(
            VbaProjectAuthorityRelationKind.Indeterminate,
            nonFileRelation.Kind);
        Assert.NotNull(nonFileRelation.PreviousAuthority);
        Assert.Null(nonFileRelation.Ownership.PreviousOwnsSubject);
        Assert.Null(nonFileRelation.Ownership.CurrentOwnsSubject);
    }

    [Fact]
    public void Indeterminate_authorities_receive_distinct_snapshot_cache_fences()
    {
        const string activeUri = "file:///C:/work/Module.bas";
        var missingManifest = new VbaProjectResolution(
            VbaProjectResolutionKind.ManifestDocument,
            "C:\\work",
            ManifestPath: null,
            DocumentName: "Book1");
        var otherDocument = missingManifest with
        {
            DocumentName = "Book2"
        };
        var rootlessAdHoc = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            RootPath: "");

        var missingManifestKey = CreateSnapshotIdentity(
            activeUri,
            missingManifest);
        var otherDocumentKey = CreateSnapshotIdentity(
            activeUri,
            otherDocument);
        var rootlessAdHocKey = CreateSnapshotIdentity(
            activeUri,
            rootlessAdHoc);

        Assert.NotEqual(missingManifestKey, otherDocumentKey);
        Assert.NotEqual(missingManifestKey, rootlessAdHocKey);
        Assert.NotEqual(otherDocumentKey, rootlessAdHocKey);
    }

    [Fact]
    public void Unresolved_file_uri_stays_typed_but_has_no_local_ownership()
    {
        const string unresolvedUri =
            "file:///C:/invalid%00path/Module.bas";
        var root = CreateRoot("unresolved-file-uri");
        var authority = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            root);

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                unresolvedUri,
                out var unresolved));
        Assert.False(unresolved.IsLocalFile);

        var relation = VbaProjectIdentityModel.Relate(
            unresolved,
            authority,
            authority);

        Assert.Equal(unresolved, relation.SubjectDocument);
        Assert.Equal(
            VbaProjectAuthorityRelationKind.Indeterminate,
            relation.Kind);
    }

    [Fact]
    public void Authority_identity_normalizes_presentation_path_variants()
    {
        var root = CreateRoot("authority-path-normalization");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var equivalentManifestPath = Path.Combine(
            root,
            "Nested",
            "..",
            "vba-project.json");
        var equivalentRoot = Path.Combine(root, ".");

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                ManifestResolution(root, manifestPath, "Book1"),
                out var manifest));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                ManifestResolution(
                    root,
                    equivalentManifestPath,
                    "book1"),
                out var equivalentManifest));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    root),
                out var adHoc));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    equivalentRoot),
                out var equivalentAdHoc));

        Assert.Equal(manifest, equivalentManifest);
        Assert.Equal(adHoc, equivalentAdHoc);
        Assert.NotEqual(manifest, adHoc);
    }

    [Theory]
    [InlineData("file:///C:/work/vba-project.json")]
    [InlineData("untitled://workspace/vba-project.json")]
    [InlineData("relative/vba-project.json")]
    public void Protocol_uris_and_relative_paths_cannot_become_authority_locations(
        string invalidLocation)
    {
        var root = CreateRoot("invalid-authority-location");

        Assert.False(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                ManifestResolution(
                    root,
                    invalidLocation,
                    "Book1"),
                out _));
        Assert.False(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    invalidLocation),
                out _));
    }

    [Fact]
    public void Filesystem_root_is_a_valid_manifest_ownership_boundary()
    {
        var fileSystemRoot = Path.GetPathRoot(
            CreateRoot("filesystem-root-boundary"))!;
        var resolution = ManifestResolution(
            fileSystemRoot,
            Path.Combine(fileSystemRoot, "vba-project.json"),
            "Book1");
        var subjectUri = new Uri(
            Path.Combine(
                fileSystemRoot,
                "identity-root-boundary",
                "Module.bas"))
            .AbsoluteUri;

        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                subjectUri,
                out var subjectIdentity));
        var relation = VbaProjectIdentityModel.Relate(
            subjectIdentity,
            resolution,
            resolution);
        Assert.Equal(
            VbaProjectAuthorityRelationKind.Same,
            relation.Kind);
        Assert.True(relation.Ownership.PreviousOwnsSubject);
        Assert.True(relation.Ownership.CurrentOwnsSubject);
    }

    private static VbaProjectResolution ManifestResolution(
        string sourceRoot,
        string manifestPath,
        string documentName)
        => new(
            VbaProjectResolutionKind.ManifestDocument,
            sourceRoot,
            manifestPath,
            documentName);

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

    private static VbaDocumentIdentity IdentifyDocument(string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? identity
            : throw new InvalidOperationException(
                "The test document must have a typed identity.");

    private static string CreateRoot(string name)
        => Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-project-identity",
            name);
}
