using VbaTools.SourceIdentities;

namespace VbaTools.Semantics;

internal static class VbaDocumentIdentityPolicy
{
    internal static bool TryIdentifyDocument(
        string uri,
        out VbaDocumentIdentity identity)
    {
        VbaSemanticWorkObservation.RecordDocumentIdentification();
        identity = default;
        if (string.IsNullOrWhiteSpace(uri)
            || LooksLikeLocalPath(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.IsFile)
        {
            // Reuse the admitted parse; reparsing adds no identity evidence and re-enters runtime URI canonicalization.
            if (!SourceIdentity.TryFromUri(parsed, out var sourceIdentity))
            {
                identity = new VbaDocumentIdentity(
                    VbaDocumentIdentityKind.UnresolvedFileUri,
                    parsed.AbsoluteUri);
                return true;
            }

            identity = new VbaDocumentIdentity(
                VbaDocumentIdentityKind.LocalFile,
                sourceIdentity.Path);
            return true;
        }

        identity = new VbaDocumentIdentity(
            VbaDocumentIdentityKind.NormalizedUri,
            parsed.AbsoluteUri);
        return true;
    }

    internal static bool SameDocument(
        string leftUri,
        string rightUri)
        => TryIdentifyDocument(leftUri, out var left)
            && TryIdentifyDocument(rightUri, out var right)
            && left == right;

    internal static IEnumerable<string> DistinctDocumentUris(
        IEnumerable<string> uris)
    {
        var identified = new HashSet<VbaDocumentIdentity>();
        var unidentified = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var uri in uris)
        {
            if (TryIdentifyDocument(uri, out var identity)
                    ? identified.Add(identity)
                    : unidentified.Add(uri))
            {
                yield return uri;
            }
        }
    }

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

    internal static bool TryNormalizePath(string path, out string canonicalPath)
    {
        var identified = SourceIdentity.TryFromPath(path, out var identity);
        canonicalPath = identity.Path;
        return identified;
    }

    private static bool LooksLikeLocalPath(string value)
        => Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.Length >= 3
                && char.IsAsciiLetter(value[0])
                && value[1] == ':'
                && value[2] is '\\' or '/';

    public static string? TryGetLocalPath(string uri)
        => SourceIdentity.TryFromUri(uri, out var identity) ? identity.Path : null;
}
