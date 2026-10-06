using System.Text;

namespace VbaTools.SourceIdentities;

/// <summary>A lexical local source identity, separate from its original URI and physical ownership.</summary>
public readonly struct SourceIdentity : IEquatable<SourceIdentity>
{
    private readonly string? path;
    private readonly bool windows;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private SourceIdentity(string path, bool windows)
    {
        this.path = path;
        this.windows = windows;
    }

    /// <summary>The normalized path, or an empty string for an uninitialized identity.</summary>
    public string Path => path ?? string.Empty;
    public bool IsWindowsPath => windows;

    /// <summary>
    /// Admits an explicit absolute file URI by decoding its path once and normalizing lexical segments.
    /// Windows roots use ordinal case-insensitive identity. Native POSIX paths remain ordinal on non-Windows hosts.
    /// Query and fragment are excluded; callers retain original URI spelling separately. Invalid or null inputs return false.
    /// </summary>
    public static bool TryFromUri(string? uri, out SourceIdentity identity)
    {
        identity = default;
        if (uri is null || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        var end = uri.IndexOfAny(['?', '#'], 5);
        var rawPath = uri[5..(end < 0 ? uri.Length : end)];
        if (!TryDecodePath(rawPath, out var decoded)) return false;
        if (!OperatingSystem.IsWindows() && TryGetNativePosixPath(decoded, out var nativePath))
            return TryFromPath(nativePath, out identity);
        if (!TryNormalizeWindowsPath(decoded, out var candidate)) return false;
        identity = new SourceIdentity(candidate, true);
        return true;
    }

    /// <summary>Reuses the original spelling of an already admitted URI without parsing another Uri.</summary>
    public static bool TryFromUri(Uri? admittedUri, out SourceIdentity identity)
    {
        identity = default;
        return admittedUri is { IsAbsoluteUri: true, IsFile: true }
            && TryFromUri(admittedUri.OriginalString, out identity);
    }

    /// <summary>Admits a fully qualified native path without applying URI decoding or changing its resolution base.</summary>
    public static bool TryFromPath(string? path, out SourceIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return false;
        try
        {
            var normalized = System.IO.Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && TryNormalizeWindowsPath(normalized, out var windowsPath, allowLocalhostDrive: false))
                normalized = windowsPath;
            identity = new SourceIdentity(normalized, OperatingSystem.IsWindows());
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or System.IO.PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public bool Equals(SourceIdentity other)
        => windows == other.windows && Comparer.Equals(path, other.path);
    public override bool Equals(object? obj) => obj is SourceIdentity other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(windows, path is null ? 0 : Comparer.GetHashCode(path));
    public static bool operator ==(SourceIdentity left, SourceIdentity right) => left.Equals(right);
    public static bool operator !=(SourceIdentity left, SourceIdentity right) => !left.Equals(right);
    public override string ToString() => Path;

    private StringComparer Comparer => windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool TryNormalizeWindowsPath(string decoded, out string normalized, bool allowLocalhostDrive = true)
    {
        normalized = string.Empty;
        var value = decoded.Replace('\\', '/');
        var leadingSlashes = value.Length - value.TrimStart('/').Length;
        var drive = value[leadingSlashes..];
        if (leadingSlashes <= 3 && IsDrivePath(drive))
        {
            normalized = NormalizeSegments(drive[..2] + "\\", drive[3..]);
            return true;
        }
        if (leadingSlashes != 2 && leadingSlashes < 4) return false;
        var authorityEnd = value.IndexOf('/', leadingSlashes);
        if (authorityEnd < 0) return false;
        var authority = value[leadingSlashes..authorityEnd];
        if (authority is "" or "." or ".." || authority.IndexOfAny(['@', ':', '[', ']']) >= 0
            || authority.Any(character => character <= ' ' || character == '\u007f')) return false;
        var remainder = value[(authorityEnd + 1)..].TrimStart('/');
        if (allowLocalhostDrive && authority.Equals("localhost", StringComparison.OrdinalIgnoreCase) && IsDrivePath(remainder))
        {
            normalized = NormalizeSegments(remainder[..2] + "\\", remainder[3..]);
            return true;
        }
        var shareEnd = remainder.IndexOf('/');
        var share = shareEnd < 0 ? remainder : remainder[..shareEnd];
        if (share is "" or "." or "..") return false;
        normalized = NormalizeSegments("\\\\" + authority + "\\" + share + "\\",
            shareEnd < 0 ? string.Empty : remainder[(shareEnd + 1)..]);
        return true;
    }

    private static bool IsDrivePath(string value)
        => value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/';

    private static bool TryGetNativePosixPath(string decoded, out string nativePath)
    {
        nativePath = string.Empty;
        if (decoded.StartsWith("///", StringComparison.Ordinal) && !decoded.StartsWith("////", StringComparison.Ordinal))
            nativePath = decoded[2..];
        else if (decoded.StartsWith("//localhost/", StringComparison.OrdinalIgnoreCase))
            nativePath = decoded[11..];
        else if (decoded.StartsWith('/') && !decoded.StartsWith("//", StringComparison.Ordinal))
            nativePath = decoded;
        if (nativePath.Length == 0 || IsDrivePath(nativePath.TrimStart('/').Replace('\\', '/')))
        {
            nativePath = string.Empty;
            return false;
        }
        return true;
    }

    private static string NormalizeSegments(string root, string remainder)
    {
        var segments = new List<string>();
        // Scan directly to avoid the observed String.SplitInternal failure for valid URIs (#415).
        for (var index = 0; index < remainder.Length;)
        {
            if (remainder[index] == '/')
            {
                index++;
                continue;
            }
            var start = index;
            while (index < remainder.Length && remainder[index] != '/') index++;
            var segment = remainder.Substring(start, index - start);
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return root + string.Join('\\', segments);
    }

    private static bool TryDecodePath(string raw, out string decoded)
    {
        decoded = string.Empty;
        var text = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length;)
        {
            if (raw[index] != '%')
            {
                var current = raw[index++];
                if (current == '\0' || char.IsLowSurrogate(current)) return false;
                text.Append(current);
                if (!char.IsHighSurrogate(current)) continue;
                if (index >= raw.Length || !char.IsLowSurrogate(raw[index])) return false;
                text.Append(raw[index++]);
                continue;
            }

            var bytes = new List<byte>();
            while (index < raw.Length && raw[index] == '%')
            {
                if (index + 2 >= raw.Length || !byte.TryParse(raw.AsSpan(index + 1, 2),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
                if (value is 0 or 0x2f or 0x5c) return false;
                bytes.Add(value);
                index += 3;
            }
            try { text.Append(StrictUtf8.GetString(bytes.ToArray())); }
            catch (DecoderFallbackException) { return false; }
        }
        decoded = text.ToString();
        return true;
    }
}
