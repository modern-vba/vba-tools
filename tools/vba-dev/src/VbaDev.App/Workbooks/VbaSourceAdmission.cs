using System.Collections.Immutable;
using System.Text;
using VbaDev.Domain;
using VbaTools.Syntax;

namespace VbaDev.App.Workbooks;

internal enum VbaSourceAdmissionIntent
{
    ExplicitImport,
    Build,
    Publish
}

/// <summary>
/// Captures one operation's authoring bytes and immutable source facts.
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

    internal DoctorSourceAdmissionRun BeginDoctorRun(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeCodePage = getActiveCodePage();
        cancellationToken.ThrowIfCancellationRequested();
        var encoding = CreateStrictActiveEncoding(activeCodePage);
        return new DoctorSourceAdmissionRun(this, activeCodePage, encoding);
    }

    internal CapturedDoctorSourceSet CaptureDoctorDocument(
        string sourceDirectory,
        int activeCodePage,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sourceDirectory);
        var exists = Directory.Exists(root);
        ImmutableArray<string> paths;
        try
        {
            if (File.Exists(root))
            {
                throw new InvalidOperationException($"Import source path is not a directory: {root}");
            }
            if (!exists)
            {
                throw new InvalidOperationException($"Import source directory was not found: {root}");
            }
            paths = inventory(root).Select(Path.GetFullPath).ToImmutableArray();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new CapturedDoctorSourceSet(root, activeCodePage, exists, [], [], [], error);
        }

        var sources = ResolveSourceFiles(paths);
        var capturedPaths = sources.Select(source => source.SourcePath)
            .Concat(sources.Select(source => source.BinaryPath)
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var capturedFiles = new Dictionary<string, CapturedDoctorFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in capturedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                capturedFiles.Add(path, new(ImmutableArray.CreateRange(readAllBytes(path)), null));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                capturedFiles.Add(path, new(default, error));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        var facts = new List<CapturedDoctorSource>(sources.Length);
        foreach (var source in sources.OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodedSource decoded;
            ImmutableArray<byte> bytes;
            try
            {
                bytes = capturedFiles[source.SourcePath].GetBytes();
                decoded = Decode(bytes, encoding, activeCodePage, source.SourcePath);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                facts.Add(new(source, null, error, null, null));
                continue;
            }

            try
            {
                var admitted = AdmitSource(source, bytes, decoded,
                    path => capturedFiles[path].GetBytes(), cancellationToken);
                facts.Add(new(source, decoded.Text, null, admitted, null));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                facts.Add(new(source, decoded.Text, null, null, error));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CapturedDoctorSourceSet(root, activeCodePage, exists, paths, capturedFiles, facts);
    }

    internal AdmittedVbaSourceSet Admit(
        string sourceDirectory,
        VbaSourceAdmissionIntent intent,
        CancellationToken cancellationToken = default)
    {
        if (intent is not VbaSourceAdmissionIntent.ExplicitImport and not VbaSourceAdmissionIntent.Build)
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        return AdmitCore(sourceDirectory, intent, [], cancellationToken);
    }

    internal AdmittedVbaSourceSet AdmitPublish(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => AdmitCore(sourceDirectory, VbaSourceAdmissionIntent.Publish, commonModules, cancellationToken);

    private AdmittedVbaSourceSet AdmitCore(
        string sourceDirectory,
        VbaSourceAdmissionIntent intent,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeCodePage = getActiveCodePage();
        cancellationToken.ThrowIfCancellationRequested();
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

        cancellationToken.ThrowIfCancellationRequested();
        var paths = inventory(root).Select(Path.GetFullPath).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var sources = ResolveSourceFiles(paths);
        if (sources.Length == 0 && intent == VbaSourceAdmissionIntent.ExplicitImport)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {root}");
        }

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(root, sources);
        var commonNames = commonModules.Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedCommonNames = commonModules.Where(entry => !entry.TestOnly)
            .Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var admitted = new List<AdmittedVbaSource>(sources.Length);
        foreach (var source in sources.OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCommonModule = commonNames.Contains(source.FileName);
            if (isCommonModule && !includedCommonNames.Contains(source.FileName))
            {
                continue;
            }
            var bytes = ImmutableArray.CreateRange(readAllBytes(source.SourcePath));
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = Decode(bytes, encoding, activeCodePage, source.SourcePath);
            var text = decoded.Text;
            if (intent == VbaSourceAdmissionIntent.Publish && !isCommonModule && VbaPublishExclusionMarker.IsPresent(text))
            {
                continue;
            }
            admitted.Add(AdmitSource(source, bytes, decoded,
                path => ImmutableArray.CreateRange(readAllBytes(path)), cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AdmittedVbaSourceSet(intent, activeCodePage, admitted);
    }

    private static VbaSourceFile[] ResolveSourceFiles(IReadOnlyList<string> paths)
    {
        var sidecars = paths
            .Where(path => Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase))
            .ToLookup(SidecarIdentity, StringComparer.OrdinalIgnoreCase);
        return paths
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
    }

    private static AdmittedVbaSource AdmitSource(
        VbaSourceFile source,
        ImmutableArray<byte> bytes,
        DecodedSource decoded,
        Func<string, ImmutableArray<byte>> readBinaryBytes,
        CancellationToken cancellationToken)
    {
        var syntax = VbaSyntaxTree.ParseModule(new Uri(source.SourcePath).AbsoluteUri, decoded.Text);
        var projection = VbaCodeModuleProjection.Create(syntax);
        var projectedKind = KindFromSyntax(projection.ModuleKind);
        if (projectedKind != source.Kind)
        {
            throw new InvalidOperationException(
                $"VBA source '{source.SourcePath}' declares component kind '{projectedKind}' instead of expected '{source.Kind}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var binaryBytes = source.BinaryPath is null
            ? (ImmutableArray<byte>?)null
            : readBinaryBytes(source.BinaryPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new AdmittedVbaSource(
            source.SourcePath,
            source.Kind,
            bytes,
            decoded.Text,
            decoded.EncodingToken,
            source.BinaryPath,
            binaryBytes,
            syntax,
            projection,
            VbeModuleIdentityMetadataReader.Read(decoded.Text, source.Kind));
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

internal sealed class DoctorSourceAdmissionRun(
    VbaSourceAdmission admission,
    int activeCodePage,
    Encoding encoding)
{
    internal int ActiveCodePage { get; } = activeCodePage;

    internal CapturedDoctorSourceSet CaptureDocument(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
        => admission.CaptureDoctorDocument(sourceDirectory, ActiveCodePage, encoding, cancellationToken);
}

internal sealed class CapturedDoctorSourceSet
{
    private readonly ImmutableDictionary<string, CapturedDoctorFile> capturedFiles;
    private readonly ImmutableArray<CapturedDoctorSource> sources;

    internal CapturedDoctorSourceSet(
        string sourceDirectory,
        int activeCodePage,
        bool sourceDirectoryExists,
        ImmutableArray<string> inventoryPaths,
        IEnumerable<KeyValuePair<string, CapturedDoctorFile>> capturedFiles,
        IEnumerable<CapturedDoctorSource> sources,
        Exception? captureFailure = null)
    {
        SourceDirectory = sourceDirectory;
        ActiveCodePage = activeCodePage;
        SourceDirectoryExists = sourceDirectoryExists;
        InventoryPaths = inventoryPaths;
        CaptureFailure = captureFailure;
        this.capturedFiles = capturedFiles.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        this.sources = sources.ToImmutableArray();
    }

    internal string SourceDirectory { get; }
    internal int ActiveCodePage { get; }
    internal bool SourceDirectoryExists { get; }
    internal ImmutableArray<string> InventoryPaths { get; }
    internal Exception? CaptureFailure { get; }

    internal ImmutableArray<byte> GetOriginalBytes(string inventoriedPath)
    {
        if (CaptureFailure is not null)
        {
            throw CaptureFailure;
        }
        if (!capturedFiles.TryGetValue(Path.GetFullPath(inventoriedPath), out var file))
        {
            throw new InvalidOperationException($"Doctor did not capture source bytes for '{inventoriedPath}'.");
        }
        return file.GetBytes();
    }

    internal AdmittedVbaSourceSet AdmitBuild(CancellationToken cancellationToken = default)
        => Admit(VbaSourceAdmissionIntent.Build, [], cancellationToken);

    internal AdmittedVbaSourceSet AdmitPublish(
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => Admit(VbaSourceAdmissionIntent.Publish, commonModules, cancellationToken);

    private AdmittedVbaSourceSet Admit(
        VbaSourceAdmissionIntent intent,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCaptureFailed();
        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(SourceDirectory,
            sources.Select(source => source.SourceFile).ToArray());
        var commonNames = commonModules.Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedCommonNames = commonModules.Where(entry => !entry.TestOnly)
            .Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var admitted = new List<AdmittedVbaSource>(sources.Length);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCommonModule = commonNames.Contains(source.SourceFile.FileName);
            if (isCommonModule && !includedCommonNames.Contains(source.SourceFile.FileName))
            {
                continue;
            }
            if (source.DecodeFailure is not null)
            {
                throw source.DecodeFailure;
            }
            if (intent == VbaSourceAdmissionIntent.Publish && !isCommonModule
                && VbaPublishExclusionMarker.IsPresent(source.DecodedText!))
            {
                continue;
            }
            if (source.AdmissionFailure is not null)
            {
                throw source.AdmissionFailure;
            }
            admitted.Add(source.Admission!);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new AdmittedVbaSourceSet(intent, ActiveCodePage, admitted);
    }

    private void ThrowIfCaptureFailed()
    {
        if (CaptureFailure is not null)
        {
            throw CaptureFailure;
        }
    }
}

internal sealed record CapturedDoctorFile(ImmutableArray<byte> Bytes, Exception? Failure)
{
    internal ImmutableArray<byte> GetBytes()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        return Bytes;
    }
}

internal sealed record CapturedDoctorSource(
    VbaSourceFile SourceFile,
    string? DecodedText,
    Exception? DecodeFailure,
    AdmittedVbaSource? Admission,
    Exception? AdmissionFailure);

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
