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
            var localPath = TryGetLocalPath(parsed);
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

    internal static bool TryNormalizePath(
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

    public static string? TryGetLocalPath(string uri)
    {
        try
        {
            return TryGetLocalPath(new Uri(uri));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string? TryGetLocalPath(Uri parsed)
    {
        try
        {
            if (!parsed.IsFile)
            {
                return null;
            }

            if (TryGetFullPath(parsed.LocalPath, out var localPath))
            {
                return localPath;
            }

            var absolutePath = Uri.UnescapeDataString(parsed.AbsolutePath);
            var candidatePath = NormalizeFileAbsolutePath(absolutePath);
            return TryGetFullPath(candidatePath, out localPath) ? localPath : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string NormalizeFileAbsolutePath(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Length >= 3
            && IsDirectorySeparator(normalized[0])
            && char.IsLetter(normalized[1])
            && normalized[2] == Path.VolumeSeparatorChar)
        {
            return normalized[1..];
        }

        return normalized;
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(NormalizeFileAbsolutePath(path));
            return true;
        }
        catch (ArgumentException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (PathTooLongException)
        {
        }

        fullPath = "";
        return false;
    }

    private static bool IsDirectorySeparator(char value)
        => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
}
