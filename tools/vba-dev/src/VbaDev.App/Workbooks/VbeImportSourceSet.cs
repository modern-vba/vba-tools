using System.Runtime.InteropServices;
using System.Text;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Creates invocation-private source mirrors accepted losslessly by VBIDE's active code page.
/// </summary>
public sealed class VbeImportSourceSetFactory
{
    private readonly Func<int> getActiveCodePage;
    private readonly Action<VbeImportSourceSet>? sourceSetCreated;

    /// <summary>
    /// Creates a factory that reads the active Windows ANSI code page directly from GetACP.
    /// </summary>
    public VbeImportSourceSetFactory()
        : this(ActiveWindowsAnsiCodePage.Get)
    {
    }

    internal VbeImportSourceSetFactory(
        Func<int> getActiveCodePage,
        Action<VbeImportSourceSet>? sourceSetCreated = null)
    {
        this.getActiveCodePage = getActiveCodePage ?? throw new ArgumentNullException(nameof(getActiveCodePage));
        this.sourceSetCreated = sourceSetCreated;
    }

    /// <summary>
    /// Captures the active code page once and stages every supplied source for one invocation.
    /// </summary>
    public VbeImportSourceSet Create(IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        var sourceSet = VbeImportSourceSet.Create(sourceFiles, getActiveCodePage());
        try
        {
            sourceSetCreated?.Invoke(sourceSet);
            return sourceSet;
        }
        catch
        {
            sourceSet.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Owns the temporary VBE-facing mirror for one import invocation.
/// </summary>
public sealed class VbeImportSourceSet : IDisposable
{
    private static readonly UTF8Encoding Utf8Strict = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16LeStrict = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BeStrict = new(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
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

    private VbeImportSourceSet(
        string stagingPath,
        IReadOnlyList<VbeImportSourceFile> sourceFiles,
        int activeCodePage)
    {
        StagingPath = stagingPath;
        SourceFiles = sourceFiles;
        ActiveCodePage = activeCodePage;
    }

    /// <summary>
    /// Gets the invocation-private staging directory.
    /// </summary>
    public string StagingPath { get; }

    /// <summary>
    /// Gets the exact active code page fixed for this source set.
    /// </summary>
    public int ActiveCodePage { get; }

    /// <summary>
    /// Gets the staged sources that may be passed to VBComponents.Import.
    /// </summary>
    public IReadOnlyList<VbeImportSourceFile> SourceFiles { get; }

    /// <summary>
    /// Strictly validates and stages one complete import source set.
    /// </summary>
    internal static VbeImportSourceSet Create(
        IReadOnlyList<VbaSourceFile> sourceFiles,
        int activeCodePage)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var activeEncoding = CreateStrictActiveEncoding(activeCodePage);
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            "vba-dev-vbe-import",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingPath);
        try
        {
            var stagedSources = new List<VbeImportSourceFile>(sourceFiles.Count);
            foreach (var sourceFile in sourceFiles)
            {
                var diagnosticSourcePath = sourceFile.DiagnosticSourcePath
                    ?? sourceFile.SourcePath;
                var originalBytes = File.ReadAllBytes(sourceFile.SourcePath);
                var decoded = DecodeStrictly(
                    originalBytes,
                    activeEncoding,
                    activeCodePage,
                    diagnosticSourcePath);
                if (sourceFile.ExpectedUnicodeText is not null &&
                    !decoded.Text.Equals(sourceFile.ExpectedUnicodeText, StringComparison.Ordinal))
                {
                    var diagnosticPath = sourceFile.ExpectedUnicodeTextSourcePath
                        ?? diagnosticSourcePath;
                    throw new InvalidOperationException(
                        $"VBA source content changed after it was selected for materialization: '{diagnosticPath}'.");
                }

                var importBytes = EncodeForVbe(
                    decoded.Text,
                    activeEncoding,
                    activeCodePage,
                    diagnosticSourcePath);
                var stagedSourcePath = Path.Combine(stagingPath, sourceFile.FileName);
                File.WriteAllBytes(stagedSourcePath, importBytes);
                var stagedBinaryPath = StageBinarySidecar(sourceFile, stagingPath);
                var projection = VbaCodeModuleProjection.Create(
                    VbaSyntaxTree.ParseModule(
                        new Uri(Path.GetFullPath(sourceFile.SourcePath)).AbsoluteUri,
                        decoded.Text));
                var projectedKind = MapModuleKind(projection.ModuleKind);
                if (projectedKind != sourceFile.Kind)
                {
                    throw new InvalidOperationException(
                        $"VBA source '{diagnosticSourcePath}' declares component kind '{projectedKind}' instead of expected '{sourceFile.Kind}'.");
                }

                var moduleIdentityAuthority = VbeModuleIdentityMetadataReader.Read(
                    decoded.Text,
                    sourceFile.Kind);

                stagedSources.Add(new VbeImportSourceFile(
                    stagedSourcePath,
                    sourceFile.Kind,
                    stagedBinaryPath,
                    new VbeImportVerification(
                        moduleIdentityAuthority.Name ?? projection.ModuleName,
                        projectedKind,
                        projection.CodeModuleLines,
                        decoded.EncodingToken),
                    diagnosticSourcePath,
                    moduleIdentityAuthority));
            }

            return new VbeImportSourceSet(
                stagingPath,
                stagedSources.AsReadOnly(),
                activeCodePage);
        }
        catch (Exception stagingError)
        {
            try
            {
                DeleteStagingDirectory(stagingPath);
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{stagingError.Message} The VBE import staging directory could not be removed: '{stagingPath}'.",
                    new AggregateException(stagingError, cleanupError));
            }

            throw;
        }
    }

    internal static string DecodeSourceText(
        byte[] bytes,
        int activeCodePage,
        string sourcePath)
        => DecodeStrictly(
            bytes,
            CreateStrictActiveEncoding(activeCodePage),
            activeCodePage,
            sourcePath).Text;

    /// <inheritdoc />
    public void Dispose() => DeleteStagingDirectory(StagingPath);

    private static DecodedSource DecodeStrictly(
        byte[] bytes,
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
            return DecodeBomSource(bytes, Utf8Preamble, Utf8Strict, "utf8bom", sourcePath);
        }

        if (bytes.AsSpan().StartsWith(Utf16LePreamble))
        {
            return DecodeBomSource(bytes, Utf16LePreamble, Utf16LeStrict, "utf16le", sourcePath);
        }

        if (bytes.AsSpan().StartsWith(Utf16BePreamble))
        {
            return DecodeBomSource(bytes, Utf16BePreamble, Utf16BeStrict, "utf16be", sourcePath);
        }

        if (TryDecodeAndRoundTrip(bytes, Utf8Strict, out var utf8Text))
        {
            return new DecodedSource(utf8Text, "utf8");
        }

        if (TryDecodeAndRoundTrip(bytes, activeEncoding, out var activeText))
        {
            return new DecodedSource(
                activeText,
                activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}");
        }

        throw new InvalidOperationException(
            $"VBA source '{sourcePath}' cannot be strictly decoded as UTF-8 or Windows code page {activeCodePage} without changing its bytes.");
    }

    private static DecodedSource DecodeBomSource(
        byte[] bytes,
        byte[] preamble,
        Encoding encoding,
        string encodingToken,
        string sourcePath)
    {
        try
        {
            var payload = bytes.AsSpan(preamble.Length).ToArray();
            var text = encoding.GetString(payload);
            var reproduced = preamble.Concat(encoding.GetBytes(text)).ToArray();
            if (!bytes.AsSpan().SequenceEqual(reproduced))
            {
                throw new InvalidOperationException(
                    $"VBA source '{sourcePath}' cannot reproduce its original {encodingToken} bytes.");
            }

            return new DecodedSource(text, encodingToken);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot be strictly decoded as {encodingToken}.",
                ex);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot reproduce its original {encodingToken} bytes.",
                ex);
        }
    }

    private static bool TryDecodeAndRoundTrip(
        byte[] bytes,
        Encoding encoding,
        out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return bytes.AsSpan().SequenceEqual(encoding.GetBytes(text));
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
        catch (EncoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static byte[] EncodeForVbe(
        string text,
        Encoding activeEncoding,
        int activeCodePage,
        string sourcePath)
    {
        try
        {
            var bytes = activeEncoding.GetBytes(text);
            var reproducedText = activeEncoding.GetString(bytes);
            if (!text.Equals(reproducedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"VBA source '{sourcePath}' changes text when converted through Windows code page {activeCodePage}.");
            }

            return bytes;
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot be represented losslessly in Windows code page {activeCodePage}.",
                ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"VBA source '{sourcePath}' cannot round-trip losslessly through Windows code page {activeCodePage}.",
                ex);
        }
    }

    private static Encoding CreateStrictActiveEncoding(int activeCodePage)
    {
        if (activeCodePage <= 0)
        {
            throw new InvalidOperationException(
                $"The active Windows ANSI code page '{activeCodePage}' is invalid.");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return activeCodePage == 65001
                ? Utf8Strict
                : Encoding.GetEncoding(
                    activeCodePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"The active Windows ANSI code page '{activeCodePage}' is not available.",
                ex);
        }
    }

    private static string? StageBinarySidecar(
        VbaSourceFile sourceFile,
        string stagingPath)
    {
        if (sourceFile.BinaryPath is null)
        {
            return null;
        }

        var stagedBinaryPath = Path.Combine(
            stagingPath,
            Path.GetFileNameWithoutExtension(sourceFile.FileName) + ".frx");
        File.WriteAllBytes(stagedBinaryPath, File.ReadAllBytes(sourceFile.BinaryPath));
        return stagedBinaryPath;
    }

    private static VbaSourceKind MapModuleKind(VbaModuleKind moduleKind)
        => moduleKind switch
        {
            VbaModuleKind.StandardModule => VbaSourceKind.StandardModule,
            VbaModuleKind.ClassModule => VbaSourceKind.ClassModule,
            VbaModuleKind.FormModule => VbaSourceKind.Form,
            _ => throw new InvalidOperationException(
                $"Unsupported exported VBA module kind '{moduleKind}'.")
        };

    private static void DeleteStagingDirectory(string stagingPath)
    {
        try
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The VBE import staging directory could not be removed: '{stagingPath}'.",
                ex);
        }
    }

    private sealed record DecodedSource(string Text, string EncodingToken);
}

internal static class ActiveWindowsAnsiCodePage
{
    public static int Get()
        => OperatingSystem.IsWindows()
            ? checked((int)GetACP())
            : 65001;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
