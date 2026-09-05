using System.Runtime.InteropServices;
using System.Text;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

internal sealed class TransportedDebugSourceSnapshotValidator
{
    private static readonly byte[][] SupportedUnicodePreambles =
    [
        [0xef, 0xbb, 0xbf],
        [0xff, 0xfe],
        [0xfe, 0xff]
    ];

    private static readonly byte[][] UnsupportedUnicodePreambles =
    [
        [0xff, 0xfe, 0x00, 0x00],
        [0x00, 0x00, 0xfe, 0xff],
        [0x2b, 0x2f, 0x76, 0x38],
        [0x2b, 0x2f, 0x76, 0x39],
        [0x2b, 0x2f, 0x76, 0x2b],
        [0x2b, 0x2f, 0x76, 0x2f]
    ];

    private readonly int activeWindowsCodePage;

    internal TransportedDebugSourceSnapshotValidator(int activeWindowsCodePage)
    {
        if (activeWindowsCodePage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(activeWindowsCodePage));
        }
        this.activeWindowsCodePage = activeWindowsCodePage;
    }

    internal static TransportedDebugSourceSnapshotValidator CreateForCurrentWindowsSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "VBA debug source validation requires Windows.");
        }
        return new TransportedDebugSourceSnapshotValidator(checked((int)GetAcp()));
    }

    internal ValidatedTransportedDebugSourceSnapshot Validate(
        TransportedDebugSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != 2)
        {
            throw new InvalidOperationException(
                $"Unsupported transported source snapshot schema version {snapshot.SchemaVersion}.");
        }
        if (snapshot.Sources.Count == 0)
        {
            throw new InvalidOperationException(
                "The transported source snapshot must contain a complete source inventory.");
        }

        var seenRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSourceUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFlatTextNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ValidatedTransportedDebugSource>(snapshot.Sources.Count);
        string? persistentSourceSetRoot = null;
        foreach (var source in snapshot.Sources)
        {
            var relativePath = ValidateRelativePath(source.RelativePath);
            if (!seenRelativePaths.Add(relativePath))
            {
                throw new InvalidOperationException(
                    $"The transported source snapshot contains duplicate path '{relativePath}'.");
            }

            var extension = Path.GetExtension(relativePath);
            var isText = new[] { ".bas", ".cls", ".frm" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
            if (!isText && !extension.Equals(".frx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The transported source snapshot contains unsupported path '{relativePath}'.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(source.ContentBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"The transported source snapshot contains invalid base64 for '{relativePath}'.",
                    exception);
            }

            if (!isText)
            {
                if (source.SourceUri is not null || source.Encoding is not null)
                {
                    throw new InvalidOperationException(
                        $"Binary source '{relativePath}' must not declare text metadata.");
                }
                validated.Add(new ValidatedTransportedDebugSource(
                    relativePath,
                    null,
                    null,
                    bytes,
                    null));
                continue;
            }

            if (!seenFlatTextNames.Add(Path.GetFileName(relativePath)))
            {
                throw new InvalidOperationException(
                    $"The transported source snapshot contains duplicate flat source identity " +
                    $"'{Path.GetFileName(relativePath)}'.");
            }
            if (source.SourceUri is null ||
                !Uri.TryCreate(source.SourceUri, UriKind.Absolute, out var sourceUri) ||
                !sourceUri.IsFile ||
                !seenSourceUris.Add(sourceUri.AbsoluteUri))
            {
                throw new InvalidOperationException(
                    $"Text source '{relativePath}' requires a unique persistent file URI.");
            }
            var sourceSetRoot = ValidatePersistentSourceIdentity(relativePath, sourceUri);
            if (persistentSourceSetRoot is null)
            {
                persistentSourceSetRoot = sourceSetRoot;
            }
            else if (!persistentSourceSetRoot.Equals(
                         sourceSetRoot,
                         StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Text source '{relativePath}' sourceUri is outside the persistent source set.");
            }
            if (source.Encoding is null)
            {
                throw new InvalidOperationException(
                    $"Text source '{relativePath}' requires a declared encoding.");
            }

            var text = StrictDecode(relativePath, source.Encoding, bytes);
            validated.Add(new ValidatedTransportedDebugSource(
                relativePath,
                sourceUri.AbsoluteUri,
                source.Encoding,
                bytes,
                text));
        }

        var orderedPaths = validated.Select(source => source.RelativePath).ToArray();
        if (!orderedPaths.SequenceEqual(
                orderedPaths.OrderBy(path => path, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Transported source snapshot entries must use canonical relative-path order.");
        }

        foreach (var sidecar in validated.Where(source =>
                     Path.GetExtension(source.RelativePath)
                         .Equals(".frx", StringComparison.OrdinalIgnoreCase)))
        {
            var formPath = Path.ChangeExtension(sidecar.RelativePath, ".frm");
            if (!seenRelativePaths.Contains(formPath))
            {
                throw new InvalidOperationException(
                    $"Binary source '{sidecar.RelativePath}' requires same-directory form '{formPath}'.");
            }
        }

        if (snapshot.ActiveSource is { } activeSource &&
            (activeSource.Line < 0 ||
             activeSource.Character < 0 ||
             !validated.Any(source => source.Text is not null &&
                 source.SourceUri!.Equals(
                     activeSource.SourceUri,
                     StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "The transported active source must identify a nonnegative position in one persistent source URI.");
        }
        var breakpointIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var breakpoint in snapshot.Breakpoints)
        {
            if (breakpoint.Line < 0 ||
                !validated.Any(source => source.Text is not null &&
                    source.SourceUri!.Equals(
                        breakpoint.SourceUri,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Each transported breakpoint must identify a nonnegative line in one persistent source URI.");
            }
            if (!breakpointIdentities.Add($"{breakpoint.SourceUri}\n{breakpoint.Line}"))
            {
                throw new InvalidOperationException(
                    $"The transported source snapshot contains duplicate breakpoint " +
                    $"'{breakpoint.SourceUri}:{breakpoint.Line + 1}'.");
            }
        }

        return new ValidatedTransportedDebugSourceSnapshot(
            snapshot.SchemaVersion,
            validated,
            snapshot.ActiveSource,
            snapshot.Breakpoints);
    }

    private static string ValidatePersistentSourceIdentity(
        string relativePath,
        Uri sourceUri)
    {
        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(sourceUri.LocalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Text source '{relativePath}' has an invalid sourceUri.",
                exception);
        }

        var sourceSetRoot = Path.GetDirectoryName(sourcePath);
        var directoryDepth = relativePath.Count(character => character == '/');
        for (var index = 0; index < directoryDepth && sourceSetRoot is not null; index++)
        {
            sourceSetRoot = Path.GetDirectoryName(sourceSetRoot);
        }
        if (sourceSetRoot is null ||
            !Path.GetRelativePath(sourceSetRoot, sourcePath)
                .Replace('\\', '/')
                .Equals(relativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Text source '{relativePath}' sourceUri does not identify that persistent relative path.");
        }

        return Path.GetFullPath(sourceSetRoot);
    }

    private string StrictDecode(
        string relativePath,
        string encodingToken,
        byte[] bytes)
    {
        try
        {
            var (encoding, preambleLength) = ResolveStrictEncoding(encodingToken, bytes);
            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            var encodedBody = encoding.GetBytes(text);
            var reconstructed = preambleLength == 0
                ? encodedBody
                : [.. bytes.AsSpan(0, preambleLength), .. encodedBody];
            if (!bytes.AsSpan().SequenceEqual(reconstructed))
            {
                throw new InvalidOperationException(
                    $"Transported text source '{relativePath}' does not round-trip as {encodingToken}.");
            }
            return text;
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException or EncoderFallbackException or
                ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Transported text source '{relativePath}' does not strictly decode as {encodingToken}.",
                exception);
        }
    }

    private (Encoding Encoding, int PreambleLength) ResolveStrictEncoding(
        string encodingToken,
        byte[] bytes)
    {
        if (UnsupportedUnicodePreambles.Any(preamble => bytes.AsSpan().StartsWith(preamble)))
        {
            throw new InvalidOperationException(
                $"Declared encoding {encodingToken} does not support the transported Unicode BOM.");
        }
        if (bytes.Length != 0 &&
            !SupportedUnicodePreambles.Any(preamble => bytes.AsSpan().StartsWith(preamble)) &&
            SupportedUnicodePreambles.Concat(UnsupportedUnicodePreambles).Any(preamble =>
                bytes.Length < preamble.Length && preamble.AsSpan().StartsWith(bytes)))
        {
            throw new InvalidOperationException(
                $"Declared encoding {encodingToken} does not support a truncated Unicode BOM.");
        }
        if (encodingToken == "utf8")
        {
            if (activeWindowsCodePage != 65001)
            {
                throw new InvalidOperationException(
                    $"Declared encoding utf8 does not match the canonical active Windows " +
                    $"encoding for code page {activeWindowsCodePage}.");
            }
            RequirePreamble(bytes, [], encodingToken);
            return (new UTF8Encoding(false, true), 0);
        }
        if (encodingToken == "utf8bom")
        {
            var encoding = new UTF8Encoding(true, true);
            RequirePreamble(bytes, encoding.GetPreamble(), encodingToken);
            return (encoding, encoding.GetPreamble().Length);
        }
        if (encodingToken == "utf16le")
        {
            var encoding = new UnicodeEncoding(false, true, true);
            RequirePreamble(bytes, encoding.GetPreamble(), encodingToken);
            return (encoding, encoding.GetPreamble().Length);
        }
        if (encodingToken == "utf16be")
        {
            var encoding = new UnicodeEncoding(true, true, true);
            RequirePreamble(bytes, encoding.GetPreamble(), encodingToken);
            return (encoding, encoding.GetPreamble().Length);
        }
        if (!encodingToken.StartsWith("windows-", StringComparison.Ordinal) ||
            !int.TryParse(
                encodingToken.AsSpan("windows-".Length),
                System.Globalization.CultureInfo.InvariantCulture,
                out var codePage) ||
            codePage <= 0 ||
            codePage == 65001 ||
            codePage != activeWindowsCodePage)
        {
            throw new InvalidOperationException(
                $"Declared encoding {encodingToken} does not match the canonical active Windows " +
                $"encoding for code page {activeWindowsCodePage}.");
        }

        RequirePreamble(bytes, [], encodingToken);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return (
            Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback),
            0);
    }

    private static void RequirePreamble(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> requiredPreamble,
        string encodingToken)
    {
        var hasKnownPreamble = bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ||
            bytes.StartsWith(new byte[] { 0xff, 0xfe }) ||
            bytes.StartsWith(new byte[] { 0xfe, 0xff });
        if ((requiredPreamble.Length == 0 && hasKnownPreamble) ||
            (requiredPreamble.Length != 0 && !bytes.StartsWith(requiredPreamble)))
        {
            throw new InvalidOperationException(
                $"Transported text bytes do not have the BOM required by {encodingToken}.");
        }
    }

    private static string ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException(
                $"The transported source path must be relative: '{relativePath}'.");
        }
        var portablePath = relativePath.Replace('\\', '/');
        var segments = portablePath.Split('/');
        if (segments.Any(segment =>
                !WindowsVbaDebugWorkspacePath.IsUnambiguousEntryName(segment)))
        {
            throw new InvalidOperationException(
                $"The transported source path must use unambiguous Windows path components: '{relativePath}'.");
        }
        return portablePath;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetACP")]
    private static extern uint GetAcp();
}

internal sealed record ValidatedTransportedDebugSourceSnapshot(
    int SchemaVersion,
    IReadOnlyList<ValidatedTransportedDebugSource> Sources,
    TransportedDebugSourcePosition? ActiveSource,
    IReadOnlyList<TransportedDebugSourceBreakpoint> Breakpoints);

internal sealed record ValidatedTransportedDebugSource(
    string RelativePath,
    string? SourceUri,
    string? Encoding,
    byte[] Bytes,
    string? Text);
