using VbaDev.Domain;

namespace VbaLanguageServer.ProjectModel;

/// <summary>
/// Carries typed document identity together with its current presentation URI.
/// </summary>
internal sealed record VbaIdentifiedDocument(
    VbaDocumentIdentity Identity,
    string Uri);

/// <summary>
/// Opaque equality identity for one manifest-backed or ad-hoc project authority.
/// </summary>
internal readonly struct VbaProjectAuthorityIdentity
    : IEquatable<VbaProjectAuthorityIdentity>,
      IComparable<VbaProjectAuthorityIdentity>
{
    private readonly VbaProjectResolutionKind kind;
    private readonly string? canonicalLocation;
    private readonly string? selectedDocument;

    internal VbaProjectAuthorityIdentity(
        VbaProjectResolutionKind kind,
        string canonicalLocation,
        string? selectedDocument)
    {
        this.kind = kind;
        this.canonicalLocation = canonicalLocation;
        this.selectedDocument = selectedDocument;
    }

    internal string StableKey
        => canonicalLocation is null
            ? throw new InvalidOperationException(
                "An uninitialized project authority has no stable key.")
            : kind == VbaProjectResolutionKind.ManifestDocument
                ? string.Join(
                    "\u001e",
                    "manifest",
                    canonicalLocation,
                    selectedDocument)
                : string.Join(
                    "\u001e",
                    "ad-hoc",
                    canonicalLocation);

    internal bool UsesManifest(VbaDocumentIdentity manifestDocument)
        => kind == VbaProjectResolutionKind.ManifestDocument
            && manifestDocument.IsLocalFile
            && canonicalLocation is not null
            && canonicalLocation.Equals(
                manifestDocument.CanonicalValue,
                StringComparison.OrdinalIgnoreCase);

    internal bool TryGetManifestPersistenceComponents(
        out string manifestPath,
        out string documentName)
    {
        manifestPath = "";
        documentName = "";
        if (kind != VbaProjectResolutionKind.ManifestDocument
            || canonicalLocation is null
            || selectedDocument is null)
        {
            return false;
        }

        manifestPath = canonicalLocation;
        documentName = selectedDocument;
        return true;
    }

    public bool Equals(VbaProjectAuthorityIdentity other)
        => kind == other.kind
            && StringComparer.OrdinalIgnoreCase.Equals(
                canonicalLocation,
                other.canonicalLocation)
            && StringComparer.OrdinalIgnoreCase.Equals(
                selectedDocument,
                other.selectedDocument);

    public override bool Equals(object? obj)
        => obj is VbaProjectAuthorityIdentity other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            kind,
            canonicalLocation is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(
                    canonicalLocation),
            selectedDocument is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(
                    selectedDocument));

    public int CompareTo(VbaProjectAuthorityIdentity other)
    {
        var kindComparison = kind.CompareTo(other.kind);
        if (kindComparison != 0)
        {
            return kindComparison;
        }

        var locationComparison = StringComparer.OrdinalIgnoreCase.Compare(
            canonicalLocation,
            other.canonicalLocation);
        return locationComparison != 0
            ? locationComparison
            : StringComparer.OrdinalIgnoreCase.Compare(
                selectedDocument,
                other.selectedDocument);
    }

    public static bool operator ==(
        VbaProjectAuthorityIdentity left,
        VbaProjectAuthorityIdentity right)
        => left.Equals(right);

    public static bool operator !=(
        VbaProjectAuthorityIdentity left,
        VbaProjectAuthorityIdentity right)
        => !left.Equals(right);

    public override string ToString()
        => canonicalLocation is null ? "" : StableKey;
}

/// <summary>
/// Opaque deterministic identity for one effective project-reference selection.
/// </summary>
internal readonly struct ReferenceSelectionFingerprint
    : IEquatable<ReferenceSelectionFingerprint>,
      IComparable<ReferenceSelectionFingerprint>
{
    private readonly string? documentKind;
    private readonly string? mainReference;
    private readonly string? missingExpectedMainReference;
    private readonly string[]? referenceNames;

    private ReferenceSelectionFingerprint(
        string documentKind,
        string mainReference,
        string missingExpectedMainReference,
        string[] referenceNames)
    {
        this.documentKind = documentKind;
        this.mainReference = mainReference;
        this.missingExpectedMainReference = missingExpectedMainReference;
        this.referenceNames = referenceNames;
    }

    internal string CreatePersistenceHashMaterial()
    {
        if (documentKind is null
            || mainReference is null
            || missingExpectedMainReference is null
            || referenceNames is null)
        {
            throw new InvalidOperationException(
                "An uninitialized reference-selection fingerprint has no persistence material.");
        }

        var builder = new System.Text.StringBuilder();
        AppendPersistenceToken(builder, documentKind);
        AppendPersistenceToken(builder, mainReference);
        AppendPersistenceToken(builder, missingExpectedMainReference);
        AppendPersistenceToken(
            builder,
            referenceNames.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        foreach (var referenceName in referenceNames)
        {
            AppendPersistenceToken(builder, referenceName);
        }

        return builder.ToString();
    }

    internal static ReferenceSelectionFingerprint Create(
        string documentKind,
        VbaProjectReferenceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(documentKind);
        ArgumentNullException.ThrowIfNull(selection);
        return new ReferenceSelectionFingerprint(
            NormalizeToken(documentKind),
            NormalizeToken(selection.MainVbaProjectReference?.Name),
            NormalizeToken(selection.MissingExpectedMainReference),
            selection.References
                .Select(reference => NormalizeToken(reference.Name))
                .ToArray());
    }

    internal static bool TryCreate(
        VbaProjectResolution resolution,
        out ReferenceSelectionFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        fingerprint = default;
        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrWhiteSpace(resolution.DocumentKind))
        {
            return false;
        }

        var documentKind = resolution.DocumentKind.Trim();
        fingerprint = Create(
            documentKind,
            VbaProjectReferenceSelection.Create(
                documentKind,
                resolution.ReferenceEntries));
        return true;
    }

    public bool Equals(ReferenceSelectionFingerprint other)
        => string.Equals(
                documentKind,
                other.documentKind,
                StringComparison.Ordinal)
            && string.Equals(
                mainReference,
                other.mainReference,
                StringComparison.Ordinal)
            && string.Equals(
                missingExpectedMainReference,
                other.missingExpectedMainReference,
                StringComparison.Ordinal)
            && (referenceNames is null
                ? other.referenceNames is null
                : other.referenceNames is not null
                    && referenceNames.SequenceEqual(
                        other.referenceNames,
                        StringComparer.Ordinal));

    public override bool Equals(object? obj)
        => obj is ReferenceSelectionFingerprint other
            && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(documentKind, StringComparer.Ordinal);
        hash.Add(mainReference, StringComparer.Ordinal);
        hash.Add(missingExpectedMainReference, StringComparer.Ordinal);
        if (referenceNames is not null)
        {
            foreach (var referenceName in referenceNames)
            {
                hash.Add(referenceName, StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    public int CompareTo(ReferenceSelectionFingerprint other)
    {
        var comparison = StringComparer.Ordinal.Compare(
            documentKind,
            other.documentKind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            mainReference,
            other.mainReference);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            missingExpectedMainReference,
            other.missingExpectedMainReference);
        if (comparison != 0)
        {
            return comparison;
        }

        if (referenceNames is null || other.referenceNames is null)
        {
            return referenceNames is null
                ? other.referenceNames is null ? 0 : -1
                : 1;
        }

        var sharedLength = Math.Min(
            referenceNames.Length,
            other.referenceNames.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            comparison = StringComparer.Ordinal.Compare(
                referenceNames[index],
                other.referenceNames[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return referenceNames.Length.CompareTo(other.referenceNames.Length);
    }

    public static bool operator ==(
        ReferenceSelectionFingerprint left,
        ReferenceSelectionFingerprint right)
        => left.Equals(right);

    public static bool operator !=(
        ReferenceSelectionFingerprint left,
        ReferenceSelectionFingerprint right)
        => !left.Equals(right);

    public override string ToString()
        => documentKind is null ? "" : CreatePersistenceHashMaterial();

    private static string NormalizeToken(string? value)
        => value?.Trim().ToUpperInvariant() ?? "";

    private static void AppendPersistenceToken(
        System.Text.StringBuilder builder,
        string value)
    {
        builder.Append(
            value.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }
}

internal enum VbaProjectAuthorityRelationKind
{
    Same,
    RetainPrevious,
    Replace,
    Unrelated,
    Indeterminate
}

internal sealed record VbaProjectAuthorityOwnershipFacts(
    bool? PreviousOwnsSubject,
    bool? CurrentOwnsSubject,
    bool? SameSourceOwnershipBoundary,
    bool? CurrentManifestWithinPreviousSourceRoot);

internal sealed record VbaProjectAuthorityRelation(
    VbaProjectAuthorityRelationKind Kind,
    VbaDocumentIdentity SubjectDocument,
    VbaProjectAuthorityIdentity? PreviousAuthority,
    VbaProjectAuthorityIdentity? CurrentAuthority,
    VbaProjectAuthorityOwnershipFacts Ownership)
{
    internal bool TransfersSubjectAuthority
        => Kind is VbaProjectAuthorityRelationKind.RetainPrevious
            or VbaProjectAuthorityRelationKind.Replace;
}

/// <summary>
/// Owns language-server document and project-authority identity decisions.
/// </summary>
internal static class VbaProjectIdentityModel
{
    internal static bool TryIdentifyDocument(string uri, out VbaDocumentIdentity identity)
        => VbaDocumentIdentityPolicy.TryIdentifyDocument(uri, out identity);

    internal static bool TryIdentifyAuthority(
        VbaProjectResolution resolution,
        out VbaProjectAuthorityIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        identity = default;
        if (resolution.Kind
            == VbaProjectResolutionKind.ManifestDocument)
        {
            if (string.IsNullOrWhiteSpace(resolution.ManifestPath)
                || string.IsNullOrWhiteSpace(resolution.DocumentName)
                || !TryNormalizeAuthorityPath(
                    resolution.ManifestPath,
                    out var manifestPath))
            {
                return false;
            }

            identity = new VbaProjectAuthorityIdentity(
                VbaProjectResolutionKind.ManifestDocument,
                manifestPath,
                resolution.DocumentName);
            return true;
        }

        if (resolution.Kind != VbaProjectResolutionKind.AdHoc
            || !TryNormalizeAuthorityPath(
                resolution.RootPath,
                out var sourceRoot))
        {
            return false;
        }

        identity = new VbaProjectAuthorityIdentity(
            VbaProjectResolutionKind.AdHoc,
            sourceRoot,
            selectedDocument: null);
        return true;
    }

    internal static VbaProjectAuthorityRelation Relate(
        VbaDocumentIdentity subjectDocument,
        VbaProjectResolution? previous,
        VbaProjectResolution? current)
    {
        var previousAuthority = TryIdentifyOptionalAuthority(previous);
        var currentAuthority = TryIdentifyOptionalAuthority(current);
        var previousOwnsSubject = TryOwnsDocument(
            previous,
            subjectDocument);
        var currentOwnsSubject = TryOwnsDocument(
            current,
            subjectDocument);
        var sameSourceOwnershipBoundary =
            TryHasSameSourceOwnershipBoundary(previous, current);
        var currentManifestWithinPreviousSourceRoot =
            TryIsCurrentManifestWithinPreviousSourceRoot(
                previous,
                current);
        var ownership = new VbaProjectAuthorityOwnershipFacts(
            previousOwnsSubject,
            currentOwnsSubject,
            sameSourceOwnershipBoundary,
            currentManifestWithinPreviousSourceRoot);

        if (previousAuthority is null
            || currentAuthority is null
            || previousOwnsSubject is null
            || currentOwnsSubject is null
            || sameSourceOwnershipBoundary is null
            || currentManifestWithinPreviousSourceRoot is null)
        {
            return new VbaProjectAuthorityRelation(
                VbaProjectAuthorityRelationKind.Indeterminate,
                subjectDocument,
                previousAuthority,
                currentAuthority,
                ownership);
        }

        if (previousAuthority.Value == currentAuthority.Value)
        {
            return new VbaProjectAuthorityRelation(
                VbaProjectAuthorityRelationKind.Same,
                subjectDocument,
                previousAuthority,
                currentAuthority,
                ownership);
        }

        if (!previousOwnsSubject.Value
            || !currentOwnsSubject.Value)
        {
            return new VbaProjectAuthorityRelation(
                VbaProjectAuthorityRelationKind.Unrelated,
                subjectDocument,
                previousAuthority,
                currentAuthority,
                ownership);
        }

        var kind = currentManifestWithinPreviousSourceRoot.Value
            ? VbaProjectAuthorityRelationKind.RetainPrevious
            : VbaProjectAuthorityRelationKind.Replace;
        return new VbaProjectAuthorityRelation(
            kind,
            subjectDocument,
            previousAuthority,
            currentAuthority,
            ownership);
    }

    internal static bool SameDocument(string leftUri, string rightUri)
        => VbaDocumentIdentityPolicy.SameDocument(leftUri, rightUri);

    internal static IEnumerable<string> DistinctDocumentUris(IEnumerable<string> uris)
        => VbaDocumentIdentityPolicy.DistinctDocumentUris(uris);

    internal static string GetDocumentStableKey(string uri)
        => VbaDocumentIdentityPolicy.GetDocumentStableKey(uri);

    internal static bool TryNormalizeSnapshotPath(
        string? path,
        out string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            canonicalPath = "";
            return true;
        }

        return TryNormalizeAuthorityPath(path, out canonicalPath);
    }

    internal static bool TryIdentifyLocalDocumentPath(string path, out VbaDocumentIdentity identity)
        => VbaDocumentIdentityPolicy.TryIdentifyLocalDocumentPath(path, out identity);

    internal static bool? OwnsTransferredProjectDocument(
        VbaProjectResolution resolution,
        VbaDocumentIdentity document)
    {
        if (!document.IsLocalFile)
        {
            return null;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument)
        {
            return TryOwnsDocument(resolution, document);
        }

        if (!TryNormalizeAuthorityPath(
                resolution.ManifestPath ?? "",
                out var manifestPath))
        {
            return null;
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (manifestDirectory is null
            || !TryNormalizeAuthorityPath(
                manifestDirectory,
                out var normalizedManifestDirectory))
        {
            return null;
        }

        try
        {
            return FileSystemPathIdentityRelations.SameOrDescendant(
                VbaProjectResolver.ResolvePathIdentity(
                    document.CanonicalValue),
                VbaProjectResolver.ResolvePathIdentity(
                    normalizedManifestDirectory));
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static VbaProjectAuthorityIdentity?
        TryIdentifyOptionalAuthority(VbaProjectResolution? resolution)
        => resolution is not null
            && TryIdentifyAuthority(resolution, out var identity)
                ? identity
                : null;

    private static bool? TryOwnsDocument(
        VbaProjectResolution? resolution,
        VbaDocumentIdentity subjectDocument)
    {
        if (resolution is null
            || !subjectDocument.IsLocalFile
            || !TryNormalizeAuthorityPath(
                resolution.RootPath,
                out var sourceRoot))
        {
            return null;
        }

        if (resolution.Kind
            == VbaProjectResolutionKind.ManifestDocument)
        {
            if (resolution.RootIdentity is null)
            {
                return IsSameOrDescendant(
                    subjectDocument.CanonicalValue,
                    sourceRoot);
            }

            try
            {
                return FileSystemPathIdentityRelations.SameOrDescendant(
                    VbaProjectResolver.ResolvePathIdentity(
                        subjectDocument.CanonicalValue),
                    resolution.RootIdentity);
            }
            catch (VbaProjectManifestException)
            {
                return null;
            }
        }

        if (resolution.Kind != VbaProjectResolutionKind.AdHoc)
        {
            return null;
        }

        var subjectDirectory = Path.GetDirectoryName(
            subjectDocument.CanonicalValue);
        return subjectDirectory is not null
            && TryNormalizeAuthorityPath(
                subjectDirectory,
                out var normalizedSubjectDirectory)
            ? normalizedSubjectDirectory.Equals(
                sourceRoot,
                StringComparison.OrdinalIgnoreCase)
            : null;
    }

    private static bool? TryHasSameSourceOwnershipBoundary(
        VbaProjectResolution? previous,
        VbaProjectResolution? current)
    {
        if (previous is null
            || current is null
            || !TryNormalizeAuthorityPath(
                previous.RootPath,
                out var previousRoot)
            || !TryNormalizeAuthorityPath(
                current.RootPath,
                out var currentRoot)
            || !TryNormalizeOptionalAuthorityPath(
                previous.ManifestPath,
                out var previousManifest)
            || !TryNormalizeOptionalAuthorityPath(
                current.ManifestPath,
                out var currentManifest))
        {
            return null;
        }

        var sameSourceRoot = TryHaveSameSourceRoot(
            previous,
            current,
            previousRoot,
            currentRoot);
        if (sameSourceRoot is null)
        {
            return null;
        }

        return previous.Kind == current.Kind
            && sameSourceRoot.Value
            && string.Equals(
                previousManifest,
                currentManifest,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                previous.DocumentName,
                current.DocumentName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool?
        TryIsCurrentManifestWithinPreviousSourceRoot(
            VbaProjectResolution? previous,
            VbaProjectResolution? current)
    {
        if (previous is null || current is null)
        {
            return null;
        }

        if (previous.Kind
                != VbaProjectResolutionKind.ManifestDocument
            || current.Kind
                != VbaProjectResolutionKind.ManifestDocument)
        {
            return false;
        }

        if (!TryNormalizeAuthorityPath(
                previous.RootPath,
                out var previousRoot)
            || !TryNormalizeAuthorityPath(
                current.ManifestPath ?? "",
                out var currentManifest))
        {
            return null;
        }

        if (previous.RootIdentity is null)
        {
            return IsSameOrDescendant(currentManifest, previousRoot)
                && !currentManifest.Equals(
                    previousRoot,
                    StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var currentManifestIdentity =
                VbaProjectResolver.ResolvePathIdentity(currentManifest);
            return FileSystemPathIdentityRelations.SameOrDescendant(
                    currentManifestIdentity,
                    previous.RootIdentity)
                && !FileSystemPathIdentityRelations.Same(
                    currentManifestIdentity,
                    previous.RootIdentity);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static bool? TryHaveSameSourceRoot(
        VbaProjectResolution previous,
        VbaProjectResolution current,
        string previousRoot,
        string currentRoot)
    {
        if (previous.RootIdentity is null
            && current.RootIdentity is null)
        {
            return previousRoot.Equals(
                currentRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var previousIdentity = previous.RootIdentity
                ?? VbaProjectResolver.ResolvePathIdentity(previousRoot);
            var currentIdentity = current.RootIdentity
                ?? VbaProjectResolver.ResolvePathIdentity(currentRoot);
            return FileSystemPathIdentityRelations.Same(
                previousIdentity,
                currentIdentity);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static bool TryNormalizeOptionalAuthorityPath(
        string? path,
        out string? canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            canonicalPath = null;
            return true;
        }

        if (TryNormalizeAuthorityPath(path, out var normalized))
        {
            canonicalPath = normalized;
            return true;
        }

        canonicalPath = null;
        return false;
    }

    private static bool TryNormalizeAuthorityPath(
        string path,
        out string canonicalPath)
    {
        if (!TryNormalizePath(path, out canonicalPath))
        {
            return false;
        }

        canonicalPath = Path.TrimEndingDirectorySeparator(
            canonicalPath);
        return !string.IsNullOrWhiteSpace(canonicalPath);
    }

    private static bool IsSameOrDescendant(
        string candidatePath,
        string rootPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(candidatePath);
        var root = Path.TrimEndingDirectorySeparator(rootPath);
        var rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.Equals(
                root,
                StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizePath(string path, out string canonicalPath)
        => VbaDocumentIdentityPolicy.TryNormalizePath(path, out canonicalPath);
}
