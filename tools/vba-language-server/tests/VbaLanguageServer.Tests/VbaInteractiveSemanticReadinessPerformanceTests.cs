using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

[Collection(VbaDocumentAnalysisPerformanceTestCollection.Name)]
public sealed class VbaInteractiveSemanticReadinessPerformanceTests
{
    private const string ActiveSourceEnvironmentVariable =
        "VBA_TOOLS_COMMON_MODULES_ACTIVE_SOURCE";
    private const double EagerValidationBaselineSeconds = 49.907;
    private const double MaximumInteractiveSemanticReadinessSeconds = 9.9814;
    private readonly ITestOutputHelper output;

    public VbaInteractiveSemanticReadinessPerformanceTests(
        ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Release_CommonModules_cold_interactive_semantic_readiness_is_within_budget()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine(
                "BENCHMARK NOT RUN: the CommonModules benchmark is Windows-only.");
            return;
        }

        if (!IsReleaseBuild)
        {
            output.WriteLine(
                $"BENCHMARK NOT RUN: Release is required; current build is {BuildConfiguration}.");
            return;
        }

        var configuredSourcePath = Environment.GetEnvironmentVariable(
            ActiveSourceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredSourcePath))
        {
            output.WriteLine(
                $"BENCHMARK NOT RUN: set {ActiveSourceEnvironmentVariable} to an active exported VBA source path.");
            return;
        }

        var activeSourcePath = Path.GetFullPath(configuredSourcePath);
        Assert.True(
            File.Exists(activeSourcePath),
            $"Configured active VBA source does not exist: {activeSourcePath}");

        var activeUri = new Uri(activeSourcePath).AbsoluteUri;
        var activeText = DiskSourceDecoding.ForCurrentProcess.Decode(
            File.ReadAllBytes(activeSourcePath),
            activeSourcePath);
        var observer = new InteractiveSemanticReadinessTimingObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            observer);

        var openDocumentStarted = Stopwatch.GetTimestamp();
        workspace.OpenDocument(activeUri, version: 1, activeText);
        var openDocumentElapsed = Stopwatch.GetElapsedTime(
            openDocumentStarted);

        observer.MarkRequestStarted();
        var snapshot = workspace.CreateProjectSnapshot(activeUri);
        observer.MarkSnapshotReturned();

        var semanticTokenProjectionStarted = Stopwatch.GetTimestamp();
        var semanticTokenData = snapshot.SemanticInventory
            .GetSemanticTokenData(activeUri);
        var semanticTokenProjectionElapsed = Stopwatch.GetElapsedTime(
            semanticTokenProjectionStarted);

        var lineCount = snapshot.SourceDocuments.Sum(pair =>
            CountPhysicalLines(pair.Value));
        var argumentListCount = snapshot.SourceDocuments.Sum(pair =>
            VbaSyntaxTree.ParseModule(pair.Key, pair.Value)
                .Module
                .ArgumentLists
                .Count);
        var timings = observer.GetTimings();
        var improvement = 1d
            - (timings.InteractiveSemanticReadiness.TotalSeconds
                / EagerValidationBaselineSeconds);

        WriteEnvironment(activeSourcePath);
        output.WriteLine(
            FormattableString.Invariant(
                $"project manifestDocuments=1, sourceDocuments={snapshot.SourceDocuments.Count}, lines={lineCount}, argumentLists={argumentListCount}, semanticTokens={semanticTokenData.Count / 5}"));
        output.WriteLine(
            "measurement warmups=0, samples=1, outlierRemoval=none, "
            + "workspaceCache=fresh, osFileSystemCache=uncontrolled");
        output.WriteLine(
            FormattableString.Invariant(
                $"phase openDocument={openDocumentElapsed.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase capture={timings.Capture.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase scopeCapture={timings.ScopeCapture.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase snapshotAdmission={timings.SnapshotAdmission.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase diskInventory={timings.DiskInventory.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase semanticInventory={timings.SemanticInventory.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase storeReturn={timings.StoreReturn.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"cold interactiveSemanticReadiness={timings.InteractiveSemanticReadiness.TotalSeconds:F4} s"));
        output.WriteLine(
            FormattableString.Invariant(
                $"phase semanticTokenProjection={semanticTokenProjectionElapsed.TotalMilliseconds:F3} ms"));
        output.WriteLine(
            FormattableString.Invariant(
                $"baseline={EagerValidationBaselineSeconds:F3} s, improvement={improvement:P2}, budget={MaximumInteractiveSemanticReadinessSeconds:F4} s"));
        output.WriteLine(
            "separateProjections eventualProjectValidation=verified-by-deterministic-suite, supplementalLspPhases=not measured");
        output.WriteLine(
            "correctness activeSourceRevision=exact-open-text, semanticTokenContent=nonempty-and-well-formed, finalProjectDiagnostics=verified-by-regression-suite");

        Assert.NotEmpty(semanticTokenData);
        Assert.Equal(0, semanticTokenData.Count % 5);
        Assert.Equal(
            VbaProjectResolutionKind.ManifestDocument,
            snapshot.Resolution.Kind);
        Assert.Equal(94, snapshot.SourceDocuments.Count);
        Assert.Equal(49_097, argumentListCount);
        Assert.True(snapshot.SourceDocuments.TryGetValue(
            activeUri,
            out var capturedActiveText));
        Assert.Equal(activeText, capturedActiveText);
        Assert.Equal(0, observer.ProjectValidationBuildCount);
        Assert.True(
            timings.InteractiveSemanticReadiness <= TimeSpan.FromSeconds(10),
            FormattableString.Invariant(
                $"Cold Interactive Semantic Readiness was {timings.InteractiveSemanticReadiness.TotalSeconds:F4} s; required <= 10.0000 s."));
        Assert.True(
            timings.InteractiveSemanticReadiness
                <= TimeSpan.FromSeconds(
                    MaximumInteractiveSemanticReadinessSeconds),
            FormattableString.Invariant(
                $"Cold Interactive Semantic Readiness was {timings.InteractiveSemanticReadiness.TotalSeconds:F4} s; required <= {MaximumInteractiveSemanticReadinessSeconds:F4} s (at least 80% faster than {EagerValidationBaselineSeconds:F3} s)."));
    }

    private void WriteEnvironment(string activeSourcePath)
    {
        output.WriteLine($"activeSource={activeSourcePath}");
        output.WriteLine(
            "source commit=not measured, worktree=not measured, corpusRevision=not measured");
        output.WriteLine(
            "command=dotnet test VbaLanguageServer.Tests.csproj -c Release -m:1 --filter FullyQualifiedName~VbaInteractiveSemanticReadinessPerformanceTests");
        output.WriteLine($"os={RuntimeInformation.OSDescription}");
        output.WriteLine(
            $"framework={RuntimeInformation.FrameworkDescription}");
        output.WriteLine(
            $"architecture os={RuntimeInformation.OSArchitecture}, process={RuntimeInformation.ProcessArchitecture}");
        output.WriteLine(
            $"cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"}, logicalProcessors={Environment.ProcessorCount}");
        output.WriteLine(
            $"build={BuildConfiguration}, targetFramework={AppContext.TargetFrameworkName ?? "unknown"}, runtime={Environment.Version}, sdk=not measured, serverGC={GCSettings.IsServerGC}");
        output.WriteLine(
            "ram=not measured, powerMode=not measured, competingLoad=not measured, processFreshness=caller-controlled");
    }

    private static string BuildConfiguration
        => typeof(VbaInteractiveSemanticReadinessPerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
            ?? "Debug";

    private static bool IsReleaseBuild
        => BuildConfiguration.Equals(
            "Release",
            StringComparison.OrdinalIgnoreCase);

    private static int CountPhysicalLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lineCount = text.Count(character => character == '\n');
        return text[^1] == '\n'
            ? lineCount
            : lineCount + 1;
    }

    private sealed class InteractiveSemanticReadinessTimingObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private long requestStartedTick;
        private long beforeCaptureTick;
        private long beforeBuildProjectSnapshotTick;
        private long beforeBuildSemanticInventoryTick;
        private long beforeStoreTick;
        private long snapshotReturnedTick;
        private int projectValidationBuildCount;

        public int ProjectValidationBuildCount
            => Volatile.Read(ref projectValidationBuildCount);

        public void MarkRequestStarted()
            => RecordOnce(
                ref requestStartedTick,
                "interactive request start");

        public void BeforeCapture(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOnce(ref beforeCaptureTick, nameof(BeforeCapture));
        }

        public void BeforeBuildProjectSnapshot(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOnce(
                ref beforeBuildProjectSnapshotTick,
                nameof(BeforeBuildProjectSnapshot));
        }

        public void BeforeBuildSemanticInventory(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOnce(
                ref beforeBuildSemanticInventoryTick,
                nameof(BeforeBuildSemanticInventory));
        }

        public void BeforeBuildProjectValidation(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref projectValidationBuildCount);
        }

        public void AfterBuildProjectValidation(string activeUri)
            => Interlocked.Increment(ref projectValidationBuildCount);

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOnce(ref beforeStoreTick, nameof(BeforeStore));
        }

        public void MarkSnapshotReturned()
            => RecordOnce(
                ref snapshotReturnedTick,
                "project snapshot return");

        public TimingBreakdown GetTimings()
        {
            var requestStarted = RequireTick(
                requestStartedTick,
                "interactive request start");
            var beforeCapture = RequireTick(
                beforeCaptureTick,
                nameof(BeforeCapture));
            var beforeBuildProjectSnapshot = RequireTick(
                beforeBuildProjectSnapshotTick,
                nameof(BeforeBuildProjectSnapshot));
            var beforeBuildSemanticInventory = RequireTick(
                beforeBuildSemanticInventoryTick,
                nameof(BeforeBuildSemanticInventory));
            var beforeStore = RequireTick(
                beforeStoreTick,
                nameof(BeforeStore));
            var snapshotReturned = RequireTick(
                snapshotReturnedTick,
                "project snapshot return");
            Assert.True(
                requestStarted <= beforeCapture
                && beforeCapture <= beforeBuildProjectSnapshot
                && beforeBuildProjectSnapshot
                    <= beforeBuildSemanticInventory
                && beforeBuildSemanticInventory <= beforeStore
                && beforeStore <= snapshotReturned,
                "Project snapshot timing hooks were observed out of order.");

            return new TimingBreakdown(
                Capture: Stopwatch.GetElapsedTime(
                    requestStarted,
                    beforeBuildProjectSnapshot),
                ScopeCapture: Stopwatch.GetElapsedTime(
                    requestStarted,
                    beforeCapture),
                SnapshotAdmission: Stopwatch.GetElapsedTime(
                    beforeCapture,
                    beforeBuildProjectSnapshot),
                DiskInventory: Stopwatch.GetElapsedTime(
                    beforeBuildProjectSnapshot,
                    beforeBuildSemanticInventory),
                SemanticInventory: Stopwatch.GetElapsedTime(
                    beforeBuildSemanticInventory,
                    beforeStore),
                StoreReturn: Stopwatch.GetElapsedTime(
                    beforeStore,
                    snapshotReturned),
                InteractiveSemanticReadiness: Stopwatch.GetElapsedTime(
                    requestStarted,
                    snapshotReturned));
        }

        private static void RecordOnce(ref long target, string phase)
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (Interlocked.CompareExchange(ref target, timestamp, 0) != 0)
            {
                throw new InvalidOperationException(
                    $"Timing phase '{phase}' was observed more than once.");
            }
        }

        private static long RequireTick(long tick, string phase)
        {
            var observed = Volatile.Read(ref tick);
            Assert.True(
                observed > 0,
                $"Timing phase '{phase}' was not observed.");
            return observed;
        }
    }

    private readonly record struct TimingBreakdown(
        TimeSpan Capture,
        TimeSpan ScopeCapture,
        TimeSpan SnapshotAdmission,
        TimeSpan DiskInventory,
        TimeSpan SemanticInventory,
        TimeSpan StoreReturn,
        TimeSpan InteractiveSemanticReadiness);
}
