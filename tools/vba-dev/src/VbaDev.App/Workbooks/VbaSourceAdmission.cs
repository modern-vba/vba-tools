using System.Collections.Immutable;
using System.Text;
using VbaTools.Syntax;

namespace VbaDev.App.Workbooks;

internal enum VbaSourceAdmissionIntent
{
    ExplicitImport
}

/// <summary>
/// Captures one explicit import's authoring bytes and immutable source facts.
/// </summary>
internal sealed class VbaSourceAdmission
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private static readonly UnicodeEncoding Utf16LeStrict = new(false, false, true);
    private static readonly UnicodeEncoding Utf16BeStrict = new(true, false, true);
    private static readonly byte[] Utf8Preamble = [0xef, 0xbb, 0xbf];
    private static readonly byte[] Utf16LePreamble = [0xff, 0xfe];
    private static readonly byte[] Utf16BePreamble = [0xfe, 0xff];
    private static readonly byte[][] UnsupportedUnicodePreambles =
    [
        [0xff, 0xfe, 0x00, 0x00],
        [0x00, 0x00, 0xfe, 0xff],
        [0x2b, 0x2f, 0x76, 0x38],
        [0x2b, 0x2f, 0x76, 0x39],
        [0x2b, 0x2f, 0x76, 0x2b],
        [0x2b, 0x2f, 0x76, 0x2f]
    ];

    private readonly Func<int> getActiveCodePage;
    private readonly Func<string, IReadOnlyList<string>> inventory;
    private readonly Func<string, byte[]> readAllBytes;

    internal VbaSourceAdmission(
        Func<int> getActiveCodePage,
        Func<string, IReadOnlyList<string>>? inventory = null,
        Func<string, byte[]>? readAllBytes = null)
    {
        this.getActiveCodePage = getActiveCodePage
            ?? throw new ArgumentNullException(nameof(getActiveCodePage));
        this.inventory = inventory ?? (root => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToArray());
        this.readAllBytes = readAllBytes ?? File.ReadAllBytes;
    }

    internal AdmittedVbaSourceSet Admit(string sourceDirectory, VbaSourceAdmissionIntent intent)
    {
        if (intent != VbaSourceAdmissionIntent.ExplicitImport)
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        var activeCodePage = getActiveCodePage();
        var encoding = CreateStrictActiveEncoding(activeCodePage);
        var root = Path.GetFullPath(sourceDirectory);
        if (File.Exists(root))
        {
            throw new InvalidOperationException($"Import source path is not a directory: {root}");
        }

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Import source directory was not found: {root}");
        }

        var paths = inventory(root).Select(Path.GetFullPath).ToArray();
        var sidecars = paths
            .Where(path => Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase))
            .ToLookup(SidecarIdentity, StringComparer.OrdinalIgnoreCase);
        var sources = paths
            .Where(DocumentSourceSetLayout.IsVbaSourceFile)
            .Select(path => new VbaSourceFile(
                path,
                KindFromExtension(path),
                DocumentSourceSetLayout.IsFormFile(path)
                    ? sidecars[SidecarIdentity(path)]
                        .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault()
                    : null))
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {root}");
        }

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(root, sources);
        var admitted = new List<AdmittedVbaSource>(sources.Length);
        foreach (var source in sources.OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = ImmutableArray.CreateRange(readAllBytes(source.SourcePath));
            var decoded = Decode(bytes, encoding, activeCodePage, source.SourcePath);
            var text = decoded.Text;
            var syntax = VbaSyntaxTree.ParseModule(new Uri(source.SourcePath).AbsoluteUri, text);
            var projection = VbaCodeModuleProjection.Create(syntax);
            var projectedKind = KindFromSyntax(projection.ModuleKind);
            if (projectedKind != source.Kind)
            {
                throw new InvalidOperationException(
                    $"VBA source '{source.SourcePath}' declares component kind '{projectedKind}' instead of expected '{source.Kind}'.");
            }

            var binaryBytes = source.BinaryPath is null
                ? (ImmutableArray<byte>?)null
                : ImmutableArray.CreateRange(readAllBytes(source.BinaryPath));
            admitted.Add(new AdmittedVbaSource(
                source.SourcePath,
                source.Kind,
                bytes,
                text,
                decoded.EncodingToken,
                source.BinaryPath,
                binaryBytes,
                syntax,
                projection,
                VbeModuleIdentityMetadataReader.Read(text, source.Kind)));
        }

        return new AdmittedVbaSourceSet(intent, activeCodePage, admitted);
    }

    private static DecodedSource Decode(
        ImmutableArray<byte> bytes,
        Encoding activeEncoding,
        int activeCodePage,
        string sourcePath)
    {
        if (UnsupportedUnicodePreambles.Any(preamble => bytes.AsSpan().StartsWith(preamble)))
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' uses an unsupported Unicode byte-order mark and cannot be strictly decoded.");
        }

        if (bytes.AsSpan().StartsWith(Utf8Preamble))
        {
            return DecodeBom(bytes, Utf8Preamble, Utf8Strict, "utf8bom", sourcePath);
        }

        if (bytes.AsSpan().StartsWith(Utf16LePreamble))
        {
            return DecodeBom(bytes, Utf16LePreamble, Utf16LeStrict, "utf16le", sourcePath);
        }

        if (bytes.AsSpan().StartsWith(Utf16BePreamble))
        {
            return DecodeBom(bytes, Utf16BePreamble, Utf16BeStrict, "utf16be", sourcePath);
        }

        if (!bytes.IsEmpty
            && new[] { Utf8Preamble, Utf16LePreamble, Utf16BePreamble }
                .Concat(UnsupportedUnicodePreambles)
                .Any(preamble => bytes.Length < preamble.Length && preamble.AsSpan().StartsWith(bytes.AsSpan())))
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' contains a truncated Unicode byte-order mark and cannot be strictly decoded.");
        }

        return new DecodedSource(
            DecodeActiveCodePage(bytes, activeEncoding, activeCodePage, sourcePath),
            activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}");
    }

    private static DecodedSource DecodeBom(
        ImmutableArray<byte> bytes,
        byte[] preamble,
        Encoding encoding,
        string encodingToken,
        string sourcePath)
    {
        try
        {
            var text = encoding.GetString(bytes.AsSpan()[preamble.Length..]);
            var reproduced = preamble.Concat(encoding.GetBytes(text)).ToArray();
            if (!bytes.AsSpan().SequenceEqual(reproduced))
            {
                throw new InvalidOperationException(
                    $"VBA source '{sourcePath}' cannot reproduce its original {encodingToken} bytes.");
            }

            return new DecodedSource(text, encodingToken);
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot be strictly decoded as {encodingToken} without changing its bytes.",
                error);
        }
    }

    private static string DecodeActiveCodePage(
        ImmutableArray<byte> bytes,
        Encoding encoding,
        int activeCodePage,
        string sourcePath)
    {
        try
        {
            var text = encoding.GetString(bytes.AsSpan());
            if (!bytes.AsSpan().SequenceEqual(encoding.GetBytes(text)))
            {
                throw new InvalidOperationException(
                    $"VBA source '{sourcePath}' cannot be strictly decoded as Windows code page {activeCodePage} without changing its bytes.");
            }

            return text;
        }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot be strictly decoded as Windows code page {activeCodePage} without changing its bytes.",
                error);
        }
    }

    private static Encoding CreateStrictActiveEncoding(int activeCodePage)
    {
        if (activeCodePage <= 0)
        {
            throw new InvalidOperationException($"The active Windows ANSI code page '{activeCodePage}' is invalid.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return activeCodePage == 65001
                ? Utf8Strict
                : Encoding.GetEncoding(activeCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException($"The active Windows ANSI code page '{activeCodePage}' is not available.", error);
        }
    }

    private static VbaSourceKind KindFromExtension(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bas" => VbaSourceKind.StandardModule,
            ".cls" => VbaSourceKind.ClassModule,
            ".frm" => VbaSourceKind.Form,
            _ => throw new InvalidOperationException($"Unsupported VBA source file: {path}")
        };

    private static string SidecarIdentity(string path)
        => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

    private static VbaSourceKind KindFromSyntax(VbaModuleKind kind)
        => kind switch
        {
            VbaModuleKind.StandardModule => VbaSourceKind.StandardModule,
            VbaModuleKind.ClassModule => VbaSourceKind.ClassModule,
            VbaModuleKind.FormModule => VbaSourceKind.Form,
            _ => throw new InvalidOperationException($"Unsupported exported VBA module kind '{kind}'.")
        };

    private sealed record DecodedSource(string Text, string EncodingToken);
}

internal sealed class AdmittedVbaSourceSet
{
    internal AdmittedVbaSourceSet(
        VbaSourceAdmissionIntent intent,
        int activeCodePage,
        IEnumerable<AdmittedVbaSource> sources)
    {
        Intent = intent;
        ActiveCodePage = activeCodePage;
        Sources = sources.ToImmutableArray();
    }

    internal VbaSourceAdmissionIntent Intent { get; }
    internal int ActiveCodePage { get; }
    internal ImmutableArray<AdmittedVbaSource> Sources { get; }
}

internal sealed class AdmittedVbaSource
{
    internal AdmittedVbaSource(
        string sourcePath,
        VbaSourceKind kind,
        ImmutableArray<byte> originalBytes,
        string text,
        string originalEncoding,
        string? binaryPath,
        ImmutableArray<byte>? binaryBytes,
        VbaSyntaxTree syntax,
        VbaCodeModuleProjection projection,
        VbeModuleIdentityAuthority moduleIdentityAuthority)
    {
        SourcePath = sourcePath;
        Kind = kind;
        OriginalBytes = originalBytes;
        Text = text;
        OriginalEncoding = originalEncoding;
        BinaryPath = binaryPath;
        BinaryBytes = binaryBytes;
        Syntax = syntax;
        Projection = projection;
        ModuleIdentityAuthority = moduleIdentityAuthority;
    }

    internal string SourcePath { get; }
    internal string DiagnosticSourcePath => SourcePath;
    internal string FileName => Path.GetFileName(SourcePath);
    internal VbaSourceKind Kind { get; }
    internal ImmutableArray<byte> OriginalBytes { get; }
    internal string Text { get; }
    internal string OriginalEncoding { get; }
    internal string? BinaryPath { get; }
    internal ImmutableArray<byte>? BinaryBytes { get; }
    internal VbaSyntaxTree Syntax { get; }
    internal VbaCodeModuleProjection Projection { get; }
    internal VbeModuleIdentityAuthority ModuleIdentityAuthority { get; }
}
