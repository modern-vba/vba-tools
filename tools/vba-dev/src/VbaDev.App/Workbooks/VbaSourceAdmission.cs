using System.Collections.Immutable;
using System.Text;
using VbaDev.Domain;
using VbaTools.Syntax;
using VbaTools.Semantics;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Captures one operation's authoring bytes and immutable source facts.
/// </summary>
internal sealed class VbaSourceAdmission
{
    private enum AdmissionPurpose
    {
        ProjectBuild,
        ProjectPublish,
        SourceSnapshotBuild,
        ExplicitImport
    }

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
        => DoctorSourceAdmissionRun.Begin(this, cancellationToken);

    internal (int ActiveCodePage, Encoding Encoding) ReadDoctorEncoding(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeCodePage = getActiveCodePage();
        cancellationToken.ThrowIfCancellationRequested();
        var encoding = CreateStrictActiveEncoding(activeCodePage);
        return (activeCodePage, encoding);
    }

    internal DoctorSourceCaptureData ReadDoctorDocumentCapture(
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
            return new DoctorSourceCaptureData(root, activeCodePage, exists, [], [], [], error);
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
        return new DoctorSourceCaptureData(root, activeCodePage, exists, paths, capturedFiles, facts);
    }

    internal AdmittedVbaSourceSet AdmitProjectBuild(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitProjectBuild(this, sourceDirectory, commonModules, cancellationToken);

    internal AdmittedVbaSourceSet AdmitAnalyzedProjectBuild(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => AdmittedVbaSourceSet.AdmitAnalyzedProjectBuild(this, sourceDirectory, commonModules, cancellationToken);

    internal (AdmittedVbaSourceData Admission, VbaSourceAnalysisReport Report) ReadAnalyzedProjectBuild(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => ReadAnalyzedProjectBuildAsync(sourceDirectory, commonModules, null, cancellationToken).GetAwaiter().GetResult();

    internal Task<AdmittedVbaSourceSet> AdmitAnalyzedProjectBuildAsync(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
        => AdmittedVbaSourceSet.AdmitAnalyzedProjectBuildAsync(this, sourceDirectory, commonModules,
            acquireInputs, cancellationToken);

    internal Task<(AdmittedVbaSourceData Admission, VbaSourceAnalysisReport Report)> ReadAnalyzedProjectBuildAsync(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>>? acquireInputs,
        CancellationToken cancellationToken)
        => ReadAnalyzedProjectAsync(sourceDirectory, AdmissionPurpose.ProjectBuild, commonModules, acquireInputs, cancellationToken);

    internal Task<AdmittedVbaSourceSet> AdmitAnalyzedProjectPublishAsync(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
        => AdmittedVbaSourceSet.AdmitAnalyzedProjectPublishAsync(this, sourceDirectory, commonModules, acquireInputs, cancellationToken);

    internal Task<(AdmittedVbaSourceData Admission, VbaSourceAnalysisReport Report)> ReadAnalyzedProjectPublishAsync(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
        => ReadAnalyzedProjectAsync(sourceDirectory, AdmissionPurpose.ProjectPublish, commonModules, acquireInputs, cancellationToken);

    private async Task<(AdmittedVbaSourceData Admission, VbaSourceAnalysisReport Report)> ReadAnalyzedProjectAsync(
        string sourceDirectory,
        AdmissionPurpose purpose,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>>? acquireInputs,
        CancellationToken cancellationToken)
    {
        var analysis = new VbaSourceAnalysisReport.Builder();
        AdmittedVbaSourceData data = default;
        var projectFatal = false;
        try
        {
            data = AdmitCore(sourceDirectory, purpose, commonModules, cancellationToken, analysis);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            projectFatal = true;
            analysis.FailProject(error);
        }
        var report = await CompleteAnalysisAsync(analysis, projectFatal, acquireInputs, cancellationToken)
            .ConfigureAwait(false);
        return (data, report);
    }

    internal static async Task<VbaSourceAnalysisReport> CompleteAnalysisAsync(
        VbaSourceAnalysisReport.Builder analysis,
        bool projectFatal,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>>? acquireInputs,
        CancellationToken cancellationToken)
    {
        VbaProjectSemanticInputs? inputs = null;
        Exception? operationalFailure = null;
        if (!projectFatal && acquireInputs is not null)
        {
            try
            {
                inputs = await acquireInputs(analysis.CapturedSyntaxTrees, cancellationToken).ConfigureAwait(false);
                ArgumentNullException.ThrowIfNull(inputs);
            }
            catch (Exception error)
            {
                operationalFailure = error;
                analysis.FailProject(error);
            }
        }
        try
        {
            if (operationalFailure is null || !cancellationToken.IsCancellationRequested)
            {
                analysis.AnalyzeProjectSources(cancellationToken, inputs);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || operationalFailure is not null)
        {
            analysis.FailProject(error);
            if (operationalFailure is not null)
            {
                operationalFailure = new AggregateException(operationalFailure, error);
            }
        }
        var report = analysis.ToReport();
        if (!report.Complete || report.HasErrors)
        {
            throw new VbaSourceAnalysisException(report, operationalFailure);
        }
        return report;
    }

    internal AdmittedVbaSourceSet AdmitProjectPublish(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitProjectPublish(this, sourceDirectory, commonModules, cancellationToken);

    internal AdmittedVbaSourceSet AdmitSourceSnapshotBuild(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitSourceSnapshotBuild(this, sourceDirectory, cancellationToken);

    internal AdmittedVbaSourceSet AdmitExplicitImport(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitExplicitImport(this, sourceDirectory, cancellationToken);

    internal AdmittedVbaSourceData ReadProjectBuild(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => AdmitCore(sourceDirectory, AdmissionPurpose.ProjectBuild, commonModules, cancellationToken);

    internal AdmittedVbaSourceData ReadProjectPublish(
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => AdmitCore(sourceDirectory, AdmissionPurpose.ProjectPublish, commonModules, cancellationToken);

    internal AdmittedVbaSourceData ReadSourceSnapshotBuild(
        string sourceDirectory,
        CancellationToken cancellationToken)
        => AdmitCore(sourceDirectory, AdmissionPurpose.SourceSnapshotBuild, [], cancellationToken);

    internal (AdmittedVbaSourceData Data, VbaSourceAnalysisReport.Builder Analysis, bool ProjectFatal) ReadSnapshotAnalysis(
        string sourceDirectory, CancellationToken cancellationToken)
    {
        var analysis = new VbaSourceAnalysisReport.Builder();
        try
        {
            return (AdmitCore(sourceDirectory, AdmissionPurpose.SourceSnapshotBuild, [], cancellationToken, analysis), analysis, false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            analysis.FailProject(error);
            return (new(0, []), analysis, true);
        }
    }

    internal AdmittedVbaSourceData ReadExplicitImport(
        string sourceDirectory,
        CancellationToken cancellationToken)
        => AdmitCore(sourceDirectory, AdmissionPurpose.ExplicitImport, [], cancellationToken);

    private AdmittedVbaSourceData AdmitCore(
        string sourceDirectory,
        AdmissionPurpose purpose,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken,
        VbaSourceAnalysisReport.Builder? analysis = null)
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
        return SelectSources(root, activeCodePage,
            sources.Select(source => (SourceSelectionInput)new LiveSourceInput(
                this, source, encoding, activeCodePage, analysis is not null)).ToArray(),
            purpose, commonModules, cancellationToken, analysis);
    }

    internal static AdmittedVbaSourceData ReadCapturedProjectBuild(
        string sourceDirectory,
        int activeCodePage,
        ImmutableArray<CapturedDoctorSource> sources,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => ReadCapturedProject(sourceDirectory, activeCodePage, sources,
            AdmissionPurpose.ProjectBuild, commonModules, cancellationToken);

    internal static AdmittedVbaSourceData ReadCapturedProjectPublish(
        string sourceDirectory,
        int activeCodePage,
        ImmutableArray<CapturedDoctorSource> sources,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => ReadCapturedProject(sourceDirectory, activeCodePage, sources,
            AdmissionPurpose.ProjectPublish, commonModules, cancellationToken);

    private static AdmittedVbaSourceData ReadCapturedProject(
        string sourceDirectory,
        int activeCodePage,
        ImmutableArray<CapturedDoctorSource> sources,
        AdmissionPurpose purpose,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => SelectSources(sourceDirectory, activeCodePage,
            sources.Select(source => (SourceSelectionInput)new CapturedSourceInput(source)).ToArray(),
            purpose, commonModules, cancellationToken);

    private static AdmittedVbaSourceData SelectSources(
        string root,
        int activeCodePage,
        SourceSelectionInput[] sources,
        AdmissionPurpose purpose,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken,
        VbaSourceAnalysisReport.Builder? analysis = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sources.Length == 0 && purpose == AdmissionPurpose.ExplicitImport)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {root}");
        }

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(root,
            sources.Select(source => source.SourceFile).ToArray());
        var commonNames = commonModules.Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includedCommonNames = commonModules.Where(entry => !entry.TestOnly)
            .Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var admitted = new List<AdmittedVbaSource>(sources.Length);
        foreach (var source in sources.OrderBy(source => source.SourceFile.FileName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isCommonModule = commonNames.Contains(source.SourceFile.FileName);
            if (purpose == AdmissionPurpose.ProjectPublish
                && isCommonModule && !includedCommonNames.Contains(source.SourceFile.FileName))
            {
                continue;
            }
            string text;
            try
            {
                text = source.ReadDecodedText(cancellationToken);
            }
            catch (Exception error) when (analysis is not null
                && error is IOException or UnauthorizedAccessException or SourceFileProcessingException)
            {
                analysis.FailSource(source.SourceFile.SourcePath, error);
                continue;
            }
            if (purpose == AdmissionPurpose.ProjectPublish && !isCommonModule && VbaPublishExclusionMarker.IsPresent(text))
            {
                continue;
            }
            if (analysis is not null)
            {
                analysis.Add(source.ReadSyntax(cancellationToken));
            }
            try
            {
                admitted.Add(source.AdmitSelectedSource(cancellationToken));
            }
            catch (SourceFileProcessingException error) when (analysis is not null)
            {
                analysis.FailSource(source.SourceFile.SourcePath, error);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ordered = purpose is AdmissionPurpose.ProjectBuild or AdmissionPurpose.ProjectPublish
            ? OrderProjectSources(admitted, commonModules)
            : admitted;
        return new AdmittedVbaSourceData(activeCodePage, ordered.ToImmutableArray());
    }

    private static IReadOnlyList<AdmittedVbaSource> OrderProjectSources(
        IReadOnlyList<AdmittedVbaSource> sources,
        IReadOnlyList<InstalledCommonModule> commonModules)
    {
        var byName = sources.ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);
        var commonNames = commonModules.Select(entry => entry.ModuleFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AdmittedVbaSource>(sources.Count);
        foreach (var entry in commonModules)
        {
            if (byName.TryGetValue(entry.ModuleFile, out var source))
            {
                ordered.Add(source);
            }
        }
        ordered.AddRange(sources.Where(source => !commonNames.Contains(source.FileName))
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));
        return ordered;
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
        CancellationToken cancellationToken,
        VbaSyntaxTree? capturedSyntax = null)
    {
        var syntax = capturedSyntax ?? VbaSyntaxTree.ParseModule(new Uri(source.SourcePath).AbsoluteUri, decoded.Text);
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

    // Decoding and selected admission are separate demands: a proved local marker
    // can exclude kind/sidecar failures, but never a whole-file decoding failure.
    private abstract class SourceSelectionInput(VbaSourceFile sourceFile)
    {
        internal VbaSourceFile SourceFile { get; } = sourceFile;
        internal abstract string ReadDecodedText(CancellationToken cancellationToken);
        internal abstract VbaSyntaxTree ReadSyntax(CancellationToken cancellationToken);
        internal abstract AdmittedVbaSource AdmitSelectedSource(CancellationToken cancellationToken);
    }

    private sealed class LiveSourceInput(
        VbaSourceAdmission admission,
        VbaSourceFile sourceFile,
        Encoding encoding,
        int activeCodePage,
        bool reportFileFailures) : SourceSelectionInput(sourceFile)
    {
        private ImmutableArray<byte> bytes;
        private DecodedSource? decoded;
        private VbaSyntaxTree? syntax;

        internal override string ReadDecodedText(CancellationToken cancellationToken)
            => GetDecoded(cancellationToken).Text;

        internal override VbaSyntaxTree ReadSyntax(CancellationToken cancellationToken)
            => syntax ??= VbaSyntaxTree.ParseModule(
                new Uri(SourceFile.SourcePath).AbsoluteUri, GetDecoded(cancellationToken).Text);

        internal override AdmittedVbaSource AdmitSelectedSource(CancellationToken cancellationToken)
        {
            var decodedSource = GetDecoded(cancellationToken);
            return AdmitSource(SourceFile, bytes, decodedSource,
                ReadBinaryBytes, cancellationToken, ReadSyntax(cancellationToken));
        }

        private ImmutableArray<byte> ReadBinaryBytes(string path)
        {
            try
            {
                return ImmutableArray.CreateRange(admission.readAllBytes(path));
            }
            catch (Exception error) when (reportFileFailures && error is IOException or UnauthorizedAccessException)
            {
                throw new SourceFileProcessingException(error);
            }
        }

        private DecodedSource GetDecoded(CancellationToken cancellationToken)
        {
            if (decoded is null)
            {
                bytes = ImmutableArray.CreateRange(admission.readAllBytes(SourceFile.SourcePath));
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    decoded = Decode(bytes, encoding, activeCodePage, SourceFile.SourcePath);
                }
                catch (InvalidOperationException error) when (reportFileFailures)
                {
                    throw new SourceFileProcessingException(error);
                }
            }
            return decoded;
        }
    }

    private sealed class CapturedSourceInput(CapturedDoctorSource source) : SourceSelectionInput(source.SourceFile)
    {
        internal override VbaSyntaxTree ReadSyntax(CancellationToken cancellationToken)
            => AdmitSelectedSource(cancellationToken).Syntax;

        internal override string ReadDecodedText(CancellationToken cancellationToken)
        {
            if (source.DecodeFailure is not null)
            {
                throw source.DecodeFailure;
            }
            return source.DecodedText!;
        }

        internal override AdmittedVbaSource AdmitSelectedSource(CancellationToken cancellationToken)
        {
            if (source.AdmissionFailure is not null)
            {
                throw source.AdmissionFailure;
            }
            return source.Admission!;
        }
    }

    private sealed record DecodedSource(string Text, string EncodingToken);

    private sealed class SourceFileProcessingException(Exception error) : Exception(error.Message, error);
}

internal sealed class DoctorSourceAdmissionRun
{
    private readonly VbaSourceAdmission admission;
    private readonly Encoding encoding;

    private DoctorSourceAdmissionRun(VbaSourceAdmission admission, int activeCodePage, Encoding encoding)
    {
        this.admission = admission;
        ActiveCodePage = activeCodePage;
        this.encoding = encoding;
    }

    internal int ActiveCodePage { get; }

    internal static DoctorSourceAdmissionRun Begin(
        VbaSourceAdmission admission,
        CancellationToken cancellationToken)
    {
        var (activeCodePage, encoding) = admission.ReadDoctorEncoding(cancellationToken);
        return new DoctorSourceAdmissionRun(admission, activeCodePage, encoding);
    }

    internal CapturedDoctorSourceSet CaptureDocument(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
        => CapturedDoctorSourceSet.Capture(this, sourceDirectory, cancellationToken);

    internal DoctorSourceCaptureData ReadDocumentCapture(
        string sourceDirectory,
        CancellationToken cancellationToken)
        => admission.ReadDoctorDocumentCapture(sourceDirectory, ActiveCodePage, encoding, cancellationToken);
}

internal sealed class CapturedDoctorSourceSet
{
    private readonly ImmutableDictionary<string, CapturedDoctorFile> capturedFiles;
    private readonly ImmutableArray<CapturedDoctorSource> sources;

    private CapturedDoctorSourceSet(DoctorSourceCaptureData capture)
    {
        SourceDirectory = capture.SourceDirectory;
        ActiveCodePage = capture.ActiveCodePage;
        SourceDirectoryExists = capture.SourceDirectoryExists;
        InventoryPaths = capture.InventoryPaths;
        CaptureFailure = capture.Failure;
        capturedFiles = capture.Files.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        sources = capture.Sources.ToImmutableArray();
    }

    internal static CapturedDoctorSourceSet Capture(
        DoctorSourceAdmissionRun run,
        string sourceDirectory,
        CancellationToken cancellationToken)
        => new(run.ReadDocumentCapture(sourceDirectory, cancellationToken));

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

    internal AdmittedVbaSourceSet AdmitProjectBuild(
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitProjectBuild(this, commonModules, cancellationToken);

    internal AdmittedVbaSourceSet AdmitProjectPublish(
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken = default)
        => AdmittedVbaSourceSet.AdmitProjectPublish(this, commonModules, cancellationToken);

    internal AdmittedVbaSourceData ReadProjectBuild(
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCaptureFailed();
        return VbaSourceAdmission.ReadCapturedProjectBuild(
            SourceDirectory, ActiveCodePage, sources, commonModules, cancellationToken);
    }

    internal AdmittedVbaSourceData ReadProjectPublish(
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCaptureFailed();
        return VbaSourceAdmission.ReadCapturedProjectPublish(
            SourceDirectory, ActiveCodePage, sources, commonModules, cancellationToken);
    }

    private void ThrowIfCaptureFailed()
    {
        if (CaptureFailure is not null)
        {
            throw CaptureFailure;
        }
    }
}

internal sealed record DoctorSourceCaptureData(
    string SourceDirectory,
    int ActiveCodePage,
    bool SourceDirectoryExists,
    ImmutableArray<string> InventoryPaths,
    IEnumerable<KeyValuePair<string, CapturedDoctorFile>> Files,
    IEnumerable<CapturedDoctorSource> Sources,
    Exception? Failure = null);

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
    private AdmittedVbaSourceSet(AdmittedVbaSourceData admission, VbaSourceAnalysisReport? analysis = null)
    {
        ActiveCodePage = admission.ActiveCodePage;
        Sources = admission.Sources;
        Analysis = analysis;
    }

    internal static AdmittedVbaSourceSet AdmitAnalyzedProjectBuild(
        VbaSourceAdmission admission,
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
    {
        var result = admission.ReadAnalyzedProjectBuild(sourceDirectory, commonModules, cancellationToken);
        return new(result.Admission, result.Report);
    }

    internal static async Task<AdmittedVbaSourceSet> AdmitAnalyzedProjectBuildAsync(
        VbaSourceAdmission admission,
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
    {
        var result = await admission.ReadAnalyzedProjectBuildAsync(sourceDirectory, commonModules,
            acquireInputs, cancellationToken).ConfigureAwait(false);
        return new(result.Admission, result.Report);
    }

    internal static AdmittedVbaSourceSet AdmitProjectBuild(
        VbaSourceAdmission admission,
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => new(admission.ReadProjectBuild(sourceDirectory, commonModules, cancellationToken));

    internal static async Task<AdmittedVbaSourceSet> AdmitAnalyzedProjectPublishAsync(
        VbaSourceAdmission admission,
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
    {
        var result = await admission.ReadAnalyzedProjectPublishAsync(sourceDirectory, commonModules,
            acquireInputs, cancellationToken).ConfigureAwait(false);
        return new(result.Admission, result.Report);
    }

    internal static AdmittedVbaSourceSet AdmitProjectPublish(
        VbaSourceAdmission admission,
        string sourceDirectory,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => new(admission.ReadProjectPublish(sourceDirectory, commonModules, cancellationToken));

    internal static AdmittedVbaSourceSet AdmitSourceSnapshotBuild(
        VbaSourceAdmission admission,
        string sourceDirectory,
        CancellationToken cancellationToken)
        => new(admission.ReadSourceSnapshotBuild(sourceDirectory, cancellationToken));

    internal static AdmittedVbaSourceSet AdmitExplicitImport(
        VbaSourceAdmission admission,
        string sourceDirectory,
        CancellationToken cancellationToken)
        => new(admission.ReadExplicitImport(sourceDirectory, cancellationToken));

    internal static AdmittedVbaSourceSet AdmitProjectBuild(
        CapturedDoctorSourceSet capture,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => new(capture.ReadProjectBuild(commonModules, cancellationToken));

    internal static AdmittedVbaSourceSet AdmitProjectPublish(
        CapturedDoctorSourceSet capture,
        IReadOnlyList<InstalledCommonModule> commonModules,
        CancellationToken cancellationToken)
        => new(capture.ReadProjectPublish(commonModules, cancellationToken));

    internal int ActiveCodePage { get; }
    internal ImmutableArray<AdmittedVbaSource> Sources { get; }
    internal VbaSourceAnalysisReport? Analysis { get; }

    internal static async Task<AdmittedVbaSourceSet> AdmitAnalyzedSnapshotAsync(
        CapturedVbaSourceAnalysis capture,
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
    {
        var result = await capture.ReadAnalyzedAsync(acquireInputs, cancellationToken).ConfigureAwait(false);
        return new(result.Data, result.Report);
    }
}

/// <summary>Retains recoverable admission findings without granting workbook-generation authority.</summary>
internal sealed class CapturedVbaSourceAnalysis
{
    private readonly AdmittedVbaSourceData data;
    private readonly VbaSourceAnalysisReport.Builder analysis;
    private readonly bool projectFatal;

    private CapturedVbaSourceAnalysis(AdmittedVbaSourceData data, VbaSourceAnalysisReport.Builder analysis, bool projectFatal)
    {
        this.data = data;
        this.analysis = analysis;
        this.projectFatal = projectFatal;
    }

    internal static CapturedVbaSourceAnalysis CaptureSnapshot(VbaSourceAdmission admission, string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var result = admission.ReadSnapshotAnalysis(sourceDirectory, cancellationToken);
        return new(result.Data, result.Analysis, result.ProjectFatal);
    }

    internal ImmutableArray<AdmittedVbaSource> Sources => data.Sources;

    internal async Task<(AdmittedVbaSourceData Data, VbaSourceAnalysisReport Report)> ReadAnalyzedAsync(
        Func<IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquireInputs,
        CancellationToken cancellationToken)
    {
        var report = await VbaSourceAdmission.CompleteAnalysisAsync(analysis.Clone(), projectFatal, acquireInputs,
            cancellationToken).ConfigureAwait(false);
        return (data, report);
    }
}

internal readonly record struct AdmittedVbaSourceData(
    int ActiveCodePage,
    ImmutableArray<AdmittedVbaSource> Sources);

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
