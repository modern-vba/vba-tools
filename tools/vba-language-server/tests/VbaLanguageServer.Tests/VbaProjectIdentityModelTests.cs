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

        Assert.NotNull(first.CoalescingKey);
        Assert.Equal(first.CoalescingKey, equivalent.CoalescingKey);
        Assert.Null(
            new VbaHostClassProjectionSnapshotUpdate(
                new VbaHostClassProjectionContext(
                    "\0",
                    "Book1",
                    Path.Combine(root, "Book1.xlsm")),
                Revision: 3,
                Snapshot: null)
                .CoalescingKey);
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
    public void Malformed_or_non_absolute_subject_is_indeterminate(
        string subjectUri)
    {
        var root = CreateRoot("invalid-subject");
        var authority = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            root);

        var relation = VbaProjectIdentityModel.Relate(
            subjectUri,
            authority,
            authority);

        Assert.Equal(
            VbaProjectAuthorityRelationKind.Indeterminate,
            relation.Kind);
        Assert.Null(relation.Ownership.PreviousOwnsSubject);
        Assert.Null(relation.Ownership.CurrentOwnsSubject);
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
            new Uri(Path.Combine(root, "Module.bas")).AbsoluteUri,
            rootless,
            rooted);
        var nonFileRelation = VbaProjectIdentityModel.Relate(
            "untitled://workspace/Module.bas",
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

        var missingManifestKey = VbaProjectSnapshotIdentity.Create(
            activeUri,
            missingManifest);
        var otherDocumentKey = VbaProjectSnapshotIdentity.Create(
            activeUri,
            otherDocument);
        var rootlessAdHocKey = VbaProjectSnapshotIdentity.Create(
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
        Assert.Null(
            VbaProjectIdentityModel.OwnsDocument(
                authority,
                unresolvedUri));

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
            VbaProjectIdentityModel.OwnsDocument(
                resolution,
                subjectUri));
        var relation = VbaProjectIdentityModel.Relate(
            subjectUri,
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

    private static string CreateRoot(string name)
        => Path.Combine(
            Path.GetTempPath(),
            "vba-language-server-project-identity",
            name);
}
