using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Build;
using VbaDev.App.HostEvents;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.References;
using VbaDev.Infrastructure.Workbooks;
using VbaTools.Semantics;
using VbaTools.Syntax;
using VbaTools.TypeLibRegistry;
using Xunit;
using Xunit.Abstractions;

namespace VbaDev.Tests;

/// <summary>
/// Compares the exact installed-TypeLib BFW analysis with a fresh-process replay that
/// reads only captured metadata and the original source tree. Neither test opens Excel.
/// Run the facts separately: the unconditioned replay requires a fresh process
/// with no installed-TypeLib acquisition history.
/// </summary>
[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class SourceAnalysisTypeLibReplayWindowsProbeTests(ITestOutputHelper output)
{
    private const string StandardLibraryGuid = "000204ef-0000-0000-c000-000000000046";
    private const string ProjectVariable = "VBA_TOOLS_SOURCE_ANALYSIS_URI_PROBE_PROJECT";
    private const string CaptureRootVariable = "VBA_TOOLS_SOURCE_ANALYSIS_TYPELIB_REPLAY_ROOT";
    private const string TrialsVariable = "VBA_TOOLS_SOURCE_ANALYSIS_URI_PROBE_TRIALS";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new ExactStringConverter() }
    };

    [SourceAnalysisTypeLibReplayProbeFact]
    [Trait("Category", "SourceAnalysisTypeLibReplayProbe")]
    public async Task CaptureInstalledBaselineAndSixMetadataInputs()
    {
        var projectRoot = RequireProjectRoot();
        var captureRoot = RequireCaptureRoot(projectRoot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(captureRoot));
        await RunReadOnlyAsync(projectRoot, async () =>
        {
            var (context, syntaxTrees) = CaptureSources(projectRoot);
            var (inputs, metadataReader) = await AcquireInstalledInputsAsync(context, syntaxTrees);
            var catalogInputs = SelectCatalogInputs(inputs, metadataReader);
            var snapshots = new List<CatalogSnapshot>();
            for (var index = 0; index < catalogInputs.Length; index++)
            {
                var directoryName = $"catalog-{index:D2}";
                var directory = Path.Combine(captureRoot, directoryName);
                Directory.CreateDirectory(directory);
                var bytes = CatalogInputBytes(catalogInputs[index]);
                var path = Path.Combine(directory, "input.json");
                using (var stream = new FileStream(path, FileMode.CreateNew)) stream.Write(bytes);
                snapshots.Add(new CatalogSnapshot(
                    directoryName + "/input.json",
                    Hash(bytes),
                    catalogInputs[index].Identity));
            }

            var definitionCount = inputs.ReferenceCatalogs
                .GetActiveDefinitions(inputs.ReferenceSelection).Count;
            var diagnostics = VbaProjectSourceAnalysis.Analyze(syntaxTrees, inputs);
            var baseline = new ReplayBaseline(
                SchemaVersion: "1.0",
                ProjectRoot: context.ProjectRoot,
                SourceRoot: context.DocumentSourceSetPath,
                Sources: SourceFingerprints(syntaxTrees),
                Selection: inputs.ReferenceSelection!.References.Select(reference => reference.Name).ToArray(),
                MainReferenceName: inputs.ReferenceSelection.MainVbaProjectReference?.Name,
                ContainingProjectName: inputs.ProjectNamespaces!.ContainingProjectName!,
                ReferencedNamespaces: inputs.ProjectNamespaces.References.ToArray(),
                Catalogs: snapshots.ToArray(),
                ActiveDefinitions: definitionCount,
                DiagnosticCount: diagnostics.Count,
                DiagnosticSha256: DiagnosticHash(diagnostics));
            var baselinePath = Path.Combine(captureRoot, "baseline.json");
            using (var stream = new FileStream(baselinePath, FileMode.CreateNew))
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(baseline, JsonOptions));
            output.WriteLine($"baseline={baselinePath}, sources={baseline.Sources.Length}, catalogs={snapshots.Count}, activeDefinitions={definitionCount}, diagnostics={diagnostics.Count}, diagnosticSha256={baseline.DiagnosticSha256}");
        });
    }

    [SourceAnalysisTypeLibReplayProbeFact]
    [Trait("Category", "SourceAnalysisTypeLibReplayProbe")]
    public async Task ReplayCapturedMetadataWithoutComOrRegistry()
    {
        var projectRoot = RequireProjectRoot();
        var captureRoot = RequireCaptureRoot(projectRoot);
        await RunReadOnlyAsync(projectRoot, () =>
        {
            var baseline = ReadBaseline(captureRoot);
            var (context, syntaxTrees) = CaptureSources(projectRoot);
            VerifySourcesAndManifest(baseline, context, syntaxTrees);
            var inputs = ReadFrozenInputs(captureRoot, baseline);
            RunFrozenTrials("COM-free", baseline, syntaxTrees, inputs, null);
            return Task.CompletedTask;
        });
    }

    [SourceAnalysisTypeLibReplayProbeFact]
    [Trait("Category", "SourceAnalysisTypeLibReplayProbe")]
    public async Task ReplayFrozenMetadataAfterInstalledTypeLibAcquisition()
    {
        var projectRoot = RequireProjectRoot();
        var captureRoot = RequireCaptureRoot(projectRoot);
        await RunReadOnlyAsync(projectRoot, async () =>
        {
            var baselinePath = Path.Combine(captureRoot, "baseline.json");
            var baselineBytes = File.ReadAllBytes(baselinePath);
            var baseline = ReadBaseline(captureRoot);
            var mode = "installed-then-frozen-replay";
            var trials = ReadTrials();
            var receiptRoot = Path.Combine(captureRoot, $"conditioned-{Guid.NewGuid():N}");
            Directory.CreateDirectory(receiptRoot);
            WriteReceipt(receiptRoot, "prepared.json", new
            {
                schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                trials, baselineSha256 = Hash(baselineBytes), projectRoot,
                startedAtUtc = DateTimeOffset.UtcNow
            });
            var completed = 0;
            try
            {
                var (context, syntaxTrees) = CaptureSources(projectRoot);
                VerifySourcesAndManifest(baseline, context, syntaxTrees);
                WriteReceipt(receiptRoot, "sources.json", new
                {
                    schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                    count = syntaxTrees.Length,
                    orderedSourceSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(SourceFingerprints(syntaxTrees), JsonOptions)),
                    capturedAtUtc = DateTimeOffset.UtcNow
                });

                // This helper returns only identity/hash values. Its live catalogs and
                // metadata reader are out of scope before frozen inputs are constructed.
                var acquired = await AcquireInstalledSnapshotFingerprintsAsync(context, syntaxTrees);
                Assert.Equal(baseline.Catalogs, acquired);
                WriteReceipt(receiptRoot, "acquired.json", new
                {
                    schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                    catalogCount = acquired.Length, catalogs = acquired,
                    acquiredAtUtc = DateTimeOffset.UtcNow
                });

                var frozenInputs = ReadFrozenInputs(captureRoot, baseline);
                completed = RunFrozenTrials(mode, baseline, syntaxTrees, frozenInputs, receiptRoot,
                    trial => completed = trial);
                WriteReceipt(receiptRoot, "outcome.json", new
                {
                    schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                    status = "succeeded", requestedTrials = trials, completedTrials = completed,
                    diagnosticSha256 = baseline.DiagnosticSha256, completedAtUtc = DateTimeOffset.UtcNow
                });
                output.WriteLine($"conditionedReceipt={receiptRoot}, processId={Environment.ProcessId}, completedTrials={completed}");
            }
            catch (Exception error)
            {
                TryWriteReceipt(receiptRoot, "failure.json", new
                {
                    schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                    status = "failed", requestedTrials = trials, completedTrials = completed,
                    exception = error.ToString(), failedAtUtc = DateTimeOffset.UtcNow
                });
                output.WriteLine($"conditionedReceipt={receiptRoot}, processId={Environment.ProcessId}, failedAfterTrials={completed}");
                throw;
            }
        });
    }

    private static ReplayBaseline ReadBaseline(string captureRoot)
    {
        var baseline = JsonSerializer.Deserialize<ReplayBaseline>(
            File.ReadAllBytes(Path.Combine(captureRoot, "baseline.json")), JsonOptions);
        Assert.NotNull(baseline);
        Assert.Equal("1.0", baseline.SchemaVersion);
        Assert.Equal(6, baseline.Catalogs.Length);
        return baseline;
    }

    private static void VerifySourcesAndManifest(ReplayBaseline baseline,
        ResolvedProjectContext context, IReadOnlyList<VbaSyntaxTree> syntaxTrees)
    {
        Assert.Equal(baseline.ProjectRoot, context.ProjectRoot);
        Assert.Equal(baseline.SourceRoot, context.DocumentSourceSetPath);
        Assert.Equal(35, syntaxTrees.Count);
        Assert.Equal(baseline.Sources, SourceFingerprints(syntaxTrees));
        Assert.False(VbaProjectSourceAnalysis.MayRequireIntrinsicHostEventCatalog(syntaxTrees));
        var manifestSelection = VbaProjectReferenceSelection.Create(
            context.Document.Kind, context.Document.References);
        Assert.Equal(manifestSelection.References.Select(reference => reference.Name), baseline.Selection);
        Assert.Equal(manifestSelection.MainVbaProjectReference?.Name, baseline.MainReferenceName);
    }

    private static VbaProjectSemanticInputs ReadFrozenInputs(string captureRoot, ReplayBaseline baseline)
    {
        var selection = VbaReferenceSelection.Capture(baseline.Selection, baseline.MainReferenceName);
        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var identities = new Dictionary<string, VbaProjectReferenceCatalogIdentity>(VbaReferenceName.Comparer);
        var origins = new Dictionary<string, VbaProjectReferenceCatalogSource>(VbaReferenceName.Comparer);
        foreach (var snapshot in baseline.Catalogs)
        {
            var inputPath = Path.GetFullPath(Path.Combine(captureRoot,
                snapshot.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(IsWithin(captureRoot, inputPath), "A catalog snapshot must stay within the capture root.");
            var bytes = File.ReadAllBytes(inputPath);
            Assert.Equal(snapshot.InputSha256, Hash(bytes));
            using var input = JsonDocument.Parse(bytes);
            Assert.Equal("1.0", input.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("vba-dev-typelib-build-input", input.RootElement.GetProperty("kind").GetString());
            var identity = input.RootElement.GetProperty("identity")
                .Deserialize<VbaProjectReferenceCatalogIdentity>(JsonOptions);
            var metadata = input.RootElement.GetProperty("metadata")
                .Deserialize<TypeLibCatalogMetadata>(JsonOptions);
            Assert.Equal(snapshot.Identity, identity);
            Assert.NotNull(metadata);
            var catalog = TypeLibReferenceCatalogBuilder.Build(identity!.ReferenceName, metadata);
            Assert.False(string.IsNullOrWhiteSpace(catalog.ReferencedVbaProjectName));
            catalogs = catalogs.WithCatalog(catalog);
            identities.Add(identity.ReferenceName, identity);
            origins.Add(identity.ReferenceName, VbaProjectReferenceCatalogSource.Generated);
        }

        Assert.Equal(6, identities.Count);
        Assert.Equal(baseline.Catalogs.Select(snapshot => snapshot.Identity.ReferenceName),
            identities.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(catalogs.GetMissingCatalogReferenceNames(selection));
        var namespaces = VbaProjectNamespaceIdentity.Capture(
            baseline.ContainingProjectName, baseline.ReferencedNamespaces);
        var inputs = VbaProjectSemanticInputs.Capture(selection, catalogs,
            identities: identities, sources: origins, projectNamespaces: namespaces);
        Assert.Equal(baseline.ActiveDefinitions, catalogs.GetActiveDefinitions(selection).Count);
        return inputs;
    }

    private int RunFrozenTrials(string mode, ReplayBaseline baseline,
        IReadOnlyList<VbaSyntaxTree> syntaxTrees, VbaProjectSemanticInputs inputs,
        string? receiptRoot, Action<int>? afterCompleted = null)
    {
        string? firstHash = null;
        var trials = ReadTrials();
        for (var trial = 1; trial <= trials; trial++)
        {
            try
            {
                var started = Stopwatch.GetTimestamp();
                var diagnostics = VbaProjectSourceAnalysis.Analyze(syntaxTrees, inputs);
                var hash = DiagnosticHash(diagnostics);
                Assert.Equal(baseline.DiagnosticCount, diagnostics.Count);
                Assert.Equal(baseline.DiagnosticSha256, hash);
                if (firstHash is not null) Assert.Equal(firstHash, hash);
                firstHash = hash;
                afterCompleted?.Invoke(trial);
                var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (receiptRoot is not null)
                    WriteReceipt(receiptRoot, $"trial-{trial:D2}.json", new
                    {
                        schemaVersion = "1.0", mode, processId = Environment.ProcessId,
                        trial, elapsedMs, diagnostics = diagnostics.Count, diagnosticSha256 = hash,
                        completedAtUtc = DateTimeOffset.UtcNow
                    });
                output.WriteLine($"mode={mode}, processId={Environment.ProcessId}, trial={trial}, elapsedMs={elapsedMs:F3}, diagnostics={diagnostics.Count}, diagnosticSha256={hash}");
            }
            catch (Exception error)
            {
                WriteBoundedFailureEvidence(mode, trial, error);
                throw;
            }
        }
        return trials;
    }

    private void WriteBoundedFailureEvidence(string mode, int trial, Exception error)
    {
        try
        {
            output.WriteLine($"mode={mode}, processId={Environment.ProcessId}, failedTrial={trial}");
        }
        catch (Exception) { /* Probe output must not replace the original analysis failure. */ }
        WriteBoundedFailureEvidence("probeSyntaxPosition", SourceAnalysisSyntaxProbeEvidenceFormatter.Format, error);
        WriteBoundedFailureEvidence("probeLexer", SourceAnalysisLexerProbeEvidenceFormatter.Format, error);
        WriteBoundedFailureEvidence("probeUriIdentification", SourceAnalysisUriProbeEvidenceFormatter.Format, error);
    }

    private void WriteBoundedFailureEvidence(
        string label, Func<Exception, string?> formatter, Exception error)
    {
        try
        {
            var evidence = formatter(error);
            if (evidence is not null) output.WriteLine($"{label}={evidence}");
        }
        catch (Exception) { /* Evidence output must not replace the original analysis failure. */ }
    }

    private static async Task<(VbaProjectSemanticInputs Inputs, CapturingMetadataReader MetadataReader)>
        AcquireInstalledInputsAsync(ResolvedProjectContext context, IReadOnlyList<VbaSyntaxTree> syntaxTrees)
    {
        var template = CapturedWorkbookTemplate.Capture(context.TemplateDocumentPath);
        var packageMetadata = template.ReadMetadata(CancellationToken.None).Metadata;
        Assert.NotNull(packageMetadata);
        var metadataReader = new CapturingMetadataReader(new ComTypeLibCatalogMetadataReader());
        var inputs = await new ProjectSemanticInputProvider(
                new VbaProjectReferencePlanner(new RegistryVbaProjectReferenceResolver()),
                new RegistryTypeLibRegistryCatalogReader(),
                metadataReader,
                new RegistryOfficeClickToRunTypeLibEvidenceReader(),
                new RejectingHostEventCatalogAutomation(),
                new CapturedProjectIdentityProbe(packageMetadata.ProjectName, [ReadInstalledStandardLibrary()]))
            .AcquireAsync(context, template, syntaxTrees, CancellationToken.None);
        Assert.False(VbaProjectSourceAnalysis.MayRequireIntrinsicHostEventCatalog(syntaxTrees));
        Assert.Equal(6, inputs.ReferenceCatalogIdentities.Count);
        Assert.Empty(inputs.ReferenceCatalogs.GetMissingCatalogReferenceNames(
            Assert.IsType<VbaReferenceSelection>(inputs.ReferenceSelection)));
        return (inputs, metadataReader);
    }

    private static CatalogInput[] SelectCatalogInputs(VbaProjectSemanticInputs inputs,
        CapturingMetadataReader metadataReader)
        => inputs.ReferenceCatalogIdentities.Values
            .OrderBy(identity => identity.ReferenceName, StringComparer.Ordinal)
            .Select(identity => new CatalogInput(identity, metadataReader.GetMetadata(identity)))
            .ToArray();

    private static async Task<CatalogSnapshot[]> AcquireInstalledSnapshotFingerprintsAsync(
        ResolvedProjectContext context, IReadOnlyList<VbaSyntaxTree> syntaxTrees)
    {
        var (liveInputs, metadataReader) = await AcquireInstalledInputsAsync(context, syntaxTrees);
        var catalogInputs = SelectCatalogInputs(liveInputs, metadataReader);
        return catalogInputs.Select((entry, index) => new CatalogSnapshot(
            $"catalog-{index:D2}/input.json", Hash(CatalogInputBytes(entry)), entry.Identity)).ToArray();
    }

    private static byte[] CatalogInputBytes(CatalogInput catalogInput)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "1.0",
            kind = "vba-dev-typelib-build-input",
            identity = catalogInput.Identity,
            metadata = catalogInput.Metadata
        }, JsonOptions);

    private static void WriteReceipt(string root, string name, object receipt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        using var stream = new FileStream(Path.Combine(root, name), FileMode.CreateNew);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryWriteReceipt(string root, string name, object receipt)
    {
        try { WriteReceipt(root, name, receipt); }
        catch (Exception) { /* Evidence failures must not replace the original test failure. */ }
    }

    private static (ResolvedProjectContext Context, VbaSyntaxTree[] SyntaxTrees) CaptureSources(string projectRoot)
    {
        var context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
            new ProjectResolutionRequest(projectRoot, null, projectRoot));
        var (_, analysis, projectFatal) = new VbaSourceAdmission(() => 932)
            .ReadSnapshotAnalysis(context.DocumentSourceSetPath, CancellationToken.None);
        var report = analysis.ToReport();
        var failureDetails = string.Join(Environment.NewLine,
            report.Failures.Select(failure =>
                $"scope={failure.Scope}, phase={failure.Phase}, activeSourcePath={failure.ActiveSourcePath ?? "<none>"}, message={failure.Message}{Environment.NewLine}{failure.ExceptionDetails}"));
        Assert.False(projectFatal, failureDetails);
        Assert.True(report.Complete, failureDetails);
        return (context, analysis.CapturedSyntaxTrees.ToArray());
    }

    private async Task RunReadOnlyAsync(string projectRoot, Func<Task> action)
    {
        var beforeFiles = TreeHash(projectRoot);
        var beforeExcel = ExcelProcessIds();
        ExceptionDispatchInfo? failure = null;
        try { await action(); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        Exception? protectionFailure = null;
        string? afterFiles = null;
        HashSet<int>? afterExcel = null;
        try
        {
            afterFiles = TreeHash(projectRoot);
            afterExcel = ExcelProcessIds();
            Assert.Equal(beforeFiles, afterFiles);
            Assert.True(beforeExcel.SetEquals(afterExcel),
                "The replay probe must not start or stop Excel.");
        }
        catch (Exception error) { protectionFailure = error; }
        try
        {
            output.WriteLine(
                $"postState beforeTreeSha256={beforeFiles}, afterTreeSha256={afterFiles ?? "unavailable"}, "
                + $"treeUnchanged={afterFiles is not null && beforeFiles == afterFiles}, "
                + $"beforeExcelCount={beforeExcel.Count}, beforeExcelIds={FormatProcessIds(beforeExcel)}, "
                + $"afterExcelCount={afterExcel?.Count.ToString() ?? "unavailable"}, "
                + $"afterExcelIds={FormatProcessIds(afterExcel)}, "
                + $"excelUnchanged={afterExcel is not null && beforeExcel.SetEquals(afterExcel)}, "
                + $"protectionCheck={(protectionFailure is null ? "passed" : "failed")}");
        }
        catch (Exception) { /* Post-state output must not replace the primary failure. */ }
        if (failure is not null)
        {
            if (protectionFailure is not null)
            {
                try { failure.SourceException.Data["protectionFailure"] = protectionFailure.ToString(); }
                catch (Exception) { /* Retain the primary analysis failure. */ }
            }
            failure.Throw();
        }
        if (protectionFailure is not null) ExceptionDispatchInfo.Capture(protectionFailure).Throw();
    }

    private static string FormatProcessIds(HashSet<int>? ids)
        => ids is null ? "unavailable" : string.Join(",", ids.Order().Take(32))
            + (ids.Count > 32 ? ",..." : string.Empty);

    private static SourceFingerprint[] SourceFingerprints(IReadOnlyList<VbaSyntaxTree> trees)
    {
        Assert.True(BitConverter.IsLittleEndian);
        return trees.Select(tree => new SourceFingerprint(tree.Uri,
            Hash(MemoryMarshal.AsBytes(tree.Text.AsSpan())))).ToArray();
    }

    private static string DiagnosticHash(IReadOnlyList<VbaSourceSemanticDiagnostic> diagnostics)
        => Hash(JsonSerializer.SerializeToUtf8Bytes(diagnostics.Select(diagnostic => new
        {
            diagnostic.SourceUri,
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.Message,
            diagnostic.Range.Start.Line,
            diagnostic.Range.Start.Character,
            endLine = diagnostic.Range.End.Line,
            endCharacter = diagnostic.Range.End.Character
        }).ToArray(), JsonOptions));

    private static string TreeHash(string root)
    {
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetRelativePath(root, path)}\0{Hash(File.ReadAllBytes(path))}");
        return Hash(Encoding.UTF8.GetBytes(string.Join('\n', entries)));
    }

    private static HashSet<int> ExcelProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try { ids.Add(process.Id); }
                catch (InvalidOperationException) { }
            }
        }
        return ids;
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsWithin(string parent, string path)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string RequireProjectRoot()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        var configured = Environment.GetEnvironmentVariable(ProjectVariable);
        Assert.False(string.IsNullOrWhiteSpace(configured), $"Set {ProjectVariable} to the affected project root.");
        var root = Path.GetFullPath(configured!);
        Assert.True(Directory.Exists(root), $"Project root does not exist: {root}");
        return root;
    }

    private static string RequireCaptureRoot(string projectRoot)
    {
        var configured = Environment.GetEnvironmentVariable(CaptureRootVariable);
        Assert.False(string.IsNullOrWhiteSpace(configured), $"Set {CaptureRootVariable} to an existing local capture directory.");
        Assert.True(Path.IsPathFullyQualified(configured) && !configured!.StartsWith("\\\\", StringComparison.Ordinal));
        var root = Path.GetFullPath(configured!);
        Assert.True(Directory.Exists(root), $"Capture root does not exist: {root}");
        Assert.False(IsWithin(projectRoot, root), "Capture root must not be inside the source project.");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            Assert.True((current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0,
                $"Capture root traverses a linked or device directory: {current.FullName}");
        return root;
    }

    private static int ReadTrials()
    {
        var configured = Environment.GetEnvironmentVariable(TrialsVariable);
        if (string.IsNullOrWhiteSpace(configured)) return 3;
        Assert.True(int.TryParse(configured, out var trials) && trials is >= 1 and <= 10,
            $"{TrialsVariable} must be from 1 through 10.");
        return trials;
    }

    private static WorkbookReference ReadInstalledStandardLibrary()
    {
        var evidence = new RegistryOfficeClickToRunTypeLibEvidenceReader().Read();
        Assert.True(evidence.Complete, evidence.Diagnostic);
        var match = Assert.Single(evidence.Installations.SelectMany(installation =>
                installation.Registrations.Select(registration => (installation, registration))),
            item => item.installation.Platform.Equals("x64", StringComparison.OrdinalIgnoreCase)
                && item.registration.Platform.Equals("win64", StringComparison.OrdinalIgnoreCase)
                && item.registration.Guid.Equals(StandardLibraryGuid, StringComparison.OrdinalIgnoreCase)
                && item.registration.Major == 4 && item.registration.Minor == 2);
        var path = Path.Combine(match.installation.InstallationPath, "root", "vfs",
            "ProgramFilesCommonX64", "Microsoft Shared", "VBA", "VBA7.1", "VBE7.DLL");
        Assert.True(File.Exists(path));
        return new WorkbookReference(
            VbaProjectReferenceCatalogSet.StandardLibraryReferenceName,
            IsRemovable: false, NamespaceName: "VBA", Guid: StandardLibraryGuid,
            Major: 4, Minor: 2, FullPath: path);
    }

    private sealed class CapturingMetadataReader(ITypeLibCatalogMetadataReader inner)
        : ITypeLibCatalogMetadataReader
    {
        private readonly List<CatalogInput> captured = [];

        public TypeLibCatalogMetadata ReadMetadata(VbaProjectReferenceCatalogIdentity identity)
        {
            var metadata = inner.ReadMetadata(identity);
            captured.Add(new CatalogInput(identity, metadata));
            return metadata;
        }

        public AcquiredTypeLibCatalogMetadata ReadMetadataFromPath(string referenceName, string path)
        {
            var acquired = inner.ReadMetadataFromPath(referenceName, path);
            captured.Add(new CatalogInput(acquired.Identity, acquired.Metadata));
            return acquired;
        }

        internal TypeLibCatalogMetadata GetMetadata(VbaProjectReferenceCatalogIdentity identity)
            => Assert.Single(captured, entry => entry.Identity == identity).Metadata;
    }

    private sealed class CapturedProjectIdentityProbe(
        string projectName, IReadOnlyList<WorkbookReference> references)
        : IWorkbookProjectIdentityProbe
    {
        public Task<WorkbookProjectIdentity> ReadAsync(CapturedWorkbookTemplate template,
            IReadOnlyList<string> requiredReferenceNames, CancellationToken cancellationToken)
            => Task.FromResult(new WorkbookProjectIdentity(projectName, references));
    }

    private sealed class RejectingHostEventCatalogAutomation : IHostEventCatalogAutomation
    {
        public Task<IntrinsicHostEventCatalog> ReadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The BFW replay must not acquire an Excel-backed host Event catalog.");
    }

    private sealed class ExactStringConverter : JsonConverter<string>
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            _ = StrictUtf8.GetByteCount(value);
            writer.WriteStringValue(value);
        }
    }

    private sealed record CatalogInput(VbaProjectReferenceCatalogIdentity Identity, TypeLibCatalogMetadata Metadata);
    private sealed record SourceFingerprint(string Uri, string Utf16Sha256);
    private sealed record CatalogSnapshot(string RelativePath, string InputSha256,
        VbaProjectReferenceCatalogIdentity Identity);
    private sealed record ReplayBaseline(string SchemaVersion, string ProjectRoot, string SourceRoot,
        SourceFingerprint[] Sources, string[] Selection, string? MainReferenceName,
        string ContainingProjectName, VbaReferencedProjectNamespace[] ReferencedNamespaces,
        CatalogSnapshot[] Catalogs, int ActiveDefinitions, int DiagnosticCount, string DiagnosticSha256);
}

public sealed class SourceAnalysisTypeLibReplayProbeFactAttribute : FactAttribute
{
    public SourceAnalysisTypeLibReplayProbeFactAttribute()
    {
        Timeout = 360_000;
        if (!string.Equals(Environment.GetEnvironmentVariable("VBA_TOOLS_RUN_SOURCE_ANALYSIS_TYPELIB_REPLAY"),
                "1", StringComparison.Ordinal))
            Skip = "Set VBA_TOOLS_RUN_SOURCE_ANALYSIS_TYPELIB_REPLAY=1 to run the TypeLib metadata replay probe.";
    }
}
