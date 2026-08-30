using VbaDev.Domain;

namespace VbaLanguageServer.ProjectModel;

/// <summary>
/// Opaque equality identity for one language-server source document.
/// </summary>
internal readonly struct VbaDocumentIdentity
    : IEquatable<VbaDocumentIdentity>
{
    private readonly VbaDocumentIdentityKind kind;
    private readonly string? canonicalValue;

    internal VbaDocumentIdentity(
        VbaDocumentIdentityKind kind,
        string canonicalValue)
    {
        this.kind = kind;
        this.canonicalValue = canonicalValue;
    }

    internal bool IsLocalFile
        => kind == VbaDocumentIdentityKind.LocalFile
            && canonicalValue is not null;

    internal string CanonicalValue
        => canonicalValue
            ?? throw new InvalidOperationException(
                "An uninitialized document identity has no canonical value.");

    internal string StableKey
        => canonicalValue is null
            ? throw new InvalidOperationException(
                "An uninitialized document identity has no stable key.")
            : string.Join("\u001e", kind, canonicalValue);

    public bool Equals(VbaDocumentIdentity other)
        => kind == other.kind
            && StringComparer.OrdinalIgnoreCase.Equals(
                canonicalValue,
                other.canonicalValue);

    public override bool Equals(object? obj)
        => obj is VbaDocumentIdentity other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            kind,
            canonicalValue is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(
                    canonicalValue));

    public static bool operator ==(
        VbaDocumentIdentity left,
        VbaDocumentIdentity right)
        => left.Equals(right);

    public static bool operator !=(
        VbaDocumentIdentity left,
        VbaDocumentIdentity right)
        => !left.Equals(right);

    public override string ToString() => canonicalValue ?? "";
}

internal enum VbaDocumentIdentityKind
{
    LocalFile,
    UnresolvedFileUri,
    NormalizedUri
}

/// <summary>
/// Opaque equality identity for one manifest-backed or ad-hoc project authority.
/// </summary>
internal readonly struct VbaProjectAuthorityIdentity
    : IEquatable<VbaProjectAuthorityIdentity>
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

    internal string? ManifestScopeKey
        => kind == VbaProjectResolutionKind.ManifestDocument
            && canonicalLocation is not null
            && selectedDocument is not null
                ? string.Join(
                    "\u001f",
                    canonicalLocation,
                    selectedDocument)
                : null;

    internal string? ManifestScopePrefix
        => kind == VbaProjectResolutionKind.ManifestDocument
            && canonicalLocation is not null
                ? $"{canonicalLocation}\u001f"
                : null;

    internal bool UsesManifest(VbaDocumentIdentity manifestDocument)
        => kind == VbaProjectResolutionKind.ManifestDocument
            && manifestDocument.IsLocalFile
            && canonicalLocation is not null
            && canonicalLocation.Equals(
                manifestDocument.CanonicalValue,
                StringComparison.OrdinalIgnoreCase);

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
    internal static bool TryIdentifyDocument(
        string uri,
        out VbaDocumentIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(uri)
            || LooksLikeLocalPath(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.IsFile)
        {
            var localPath = VbaProjectResolver.TryGetLocalPath(uri);
            if (localPath is null
                || !TryNormalizePath(localPath, out var canonicalPath))
            {
                identity = new VbaDocumentIdentity(
                    VbaDocumentIdentityKind.UnresolvedFileUri,
                    parsed.AbsoluteUri);
                return true;
            }

            identity = new VbaDocumentIdentity(
                VbaDocumentIdentityKind.LocalFile,
                canonicalPath);
            return true;
        }

        identity = new VbaDocumentIdentity(
            VbaDocumentIdentityKind.NormalizedUri,
            parsed.AbsoluteUri);
        return true;
    }

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

    internal static VbaProjectAuthorityRelation Relate(
        string subjectUri,
        VbaProjectResolution? previous,
        VbaProjectResolution? current)
    {
        if (TryIdentifyDocument(subjectUri, out var subjectDocument))
        {
            return Relate(subjectDocument, previous, current);
        }

        return new VbaProjectAuthorityRelation(
            VbaProjectAuthorityRelationKind.Indeterminate,
            default,
            TryIdentifyOptionalAuthority(previous),
            TryIdentifyOptionalAuthority(current),
            new VbaProjectAuthorityOwnershipFacts(
                PreviousOwnsSubject: null,
                CurrentOwnsSubject: null,
                SameSourceOwnershipBoundary: null,
                CurrentManifestWithinPreviousSourceRoot: null));
    }

    internal static bool SameDocument(
        string leftUri,
        string rightUri)
        => TryIdentifyDocument(leftUri, out var left)
            && TryIdentifyDocument(rightUri, out var right)
            && left == right;

    internal static string GetDocumentStableKey(string uri)
        => TryIdentifyDocument(uri, out var identity)
            ? identity.StableKey
            : string.Join("\u001e", "unidentified", uri);

    internal static bool TryIdentifyLocalDocumentPath(
        string path,
        out VbaDocumentIdentity identity)
    {
        identity = default;
        if (!TryNormalizePath(path, out var canonicalPath))
        {
            return false;
        }

        identity = new VbaDocumentIdentity(
            VbaDocumentIdentityKind.LocalFile,
            canonicalPath);
        return true;
    }

    internal static bool TryGetManifestScopeKey(
        VbaProjectResolution resolution,
        out string scopeKey)
    {
        scopeKey = "";
        if (!TryIdentifyAuthority(resolution, out var authority)
            || authority.ManifestScopeKey is not { } manifestScopeKey)
        {
            return false;
        }

        scopeKey = manifestScopeKey;
        return true;
    }

    internal static bool TryGetManifestScopePrefix(
        string manifestUri,
        out string scopePrefix)
    {
        scopePrefix = "";
        if (!TryIdentifyDocument(manifestUri, out var manifestDocument)
            || !manifestDocument.IsLocalFile)
        {
            return false;
        }

        scopePrefix = $"{manifestDocument.CanonicalValue}\u001f";
        return true;
    }

    internal static bool UsesManifestUri(
        VbaProjectResolution resolution,
        string manifestUri)
        => TryIdentifyAuthority(resolution, out var authority)
            && TryIdentifyDocument(
                manifestUri,
                out var manifestDocument)
            && authority.UsesManifest(manifestDocument);

    internal static bool UsesManifestPath(
        VbaProjectResolution resolution,
        string manifestPath)
        => TryIdentifyAuthority(resolution, out var authority)
            && TryIdentifyLocalDocumentPath(
                manifestPath,
                out var manifestDocument)
            && authority.UsesManifest(manifestDocument);

    internal static bool? OwnsDocument(
        VbaProjectResolution? resolution,
        string documentUri)
        => TryIdentifyDocument(documentUri, out var document)
            ? TryOwnsDocument(resolution, document)
            : null;

    internal static bool? OwnsTransferredProjectDocument(
        VbaProjectResolution resolution,
        string documentUri)
    {
        if (!TryIdentifyDocument(documentUri, out var document)
            || !document.IsLocalFile)
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

    private static bool TryNormalizePath(
        string path,
        out string canonicalPath)
    {
        canonicalPath = "";
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            canonicalPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool LooksLikeLocalPath(string value)
        => Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.Length >= 3
                && char.IsAsciiLetter(value[0])
                && value[1] == ':'
                && value[2] is '\\' or '/';
}
