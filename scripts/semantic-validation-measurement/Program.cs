using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VbaTools.Semantics;
using VbaTools.Syntax;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
var options = MeasurementOptions.Parse(args);
var corpus = GeneratedCorpus.Create(options.CallsPerDocument);
var syntaxStarted = Stopwatch.GetTimestamp();
var syntaxTrees = corpus.Sources.Select(source =>
    VbaSyntaxTree.ParseModule(source.Uri, source.Text)).ToArray();
var syntaxMilliseconds = Stopwatch.GetElapsedTime(syntaxStarted).TotalMilliseconds;
var selection = VbaReferenceSelection.Capture([GeneratedCorpus.ReferenceName], null);
var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(corpus.Catalog);
var inputs = VbaProjectSemanticInputs.Capture(selection, catalogs);
var trials = Enumerable.Range(0, 4).Select(index =>
    RunTrial(index, corpus, syntaxTrees, selection, catalogs, inputs, options.LookupRepetitions)).ToArray();
var measured = trials.Skip(1).ToArray();
var allPassed = trials.All(trial => trial.Success);
var assemblies = new[]
{
    typeof(GeneratedCorpus).Assembly,
    typeof(VbaProjectSourceAnalysis).Assembly,
    typeof(VbaSyntaxTree).Assembly,
    typeof(object).Assembly
}.Select(assembly => new
{
    name = assembly.GetName().Name,
    version = assembly.GetName().Version?.ToString(),
    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
    location = assembly.Location,
    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
}).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    schemaVersion = "1.0",
    options.Variant,
    suppliedRevision = options.Revision,
    measuredAtUtc = DateTimeOffset.UtcNow,
    success = allPassed,
    environment = new
    {
        os = RuntimeInformation.OSDescription,
        framework = RuntimeInformation.FrameworkDescription,
        runtime = Environment.Version.ToString(),
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        logicalProcessors = Environment.ProcessorCount,
        processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        serverGc = GCSettings.IsServerGC,
        buildSdkVersion = typeof(GeneratedCorpus).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildSdkVersion").Value
    },
    assemblies,
    corpus = new
    {
        name = "fixed-eight-document-source-and-reference-lookup-v1",
        documentCount = corpus.Sources.Length,
        options.CallsPerDocument,
        callGroupsPerDocument = options.CallsPerDocument,
        explicitCallsPerGroup = 6,
        negativeSentinelCalls = 4,
        sourceCharacters = corpus.Sources.Sum(source => source.Text.Length),
        sourceLines = corpus.Sources.Sum(source => source.Text.Count(character => character == '\n')),
        sourcesSha256 = Fingerprint(corpus.Sources),
        catalogSha256 = Fingerprint(corpus.Catalog),
        catalogDefinitions = corpus.Catalog.Definitions.Count,
        referenceSelectionSha256 = Fingerprint(selection),
        publicResolutionFixtureSha256 = Fingerprint(corpus.ResolutionDocuments),
        options.LookupRepetitions,
        queriesPerRepetition = corpus.Queries.Length
    },
    measurement = new
    {
        warmups = 1,
        measuredTrials = 3,
        outlierRemoval = "none",
        syntaxPreparationMilliseconds = syntaxMilliseconds,
        analysisScope = "Public Analyze on preparsed complete generated sources; each invocation creates fresh semantic inventory.",
        resolutionScope = "Public Resolve on fresh service over explicit public source-definition fixture; construction timed separately; no per-service excluded warmup.",
        processFreshness = "One process per invocation; one excluded complete warmup; no forced collections.",
        competingLoad = "caller-controlled; run variants serially without tests, builds or profilers",
        notMeasured = "Excel, workbook Build, editor publication, source/catalog acquisition, private work counters, retained-capacity policy"
    },
    trials,
    medians = !allPassed ? null : new
    {
        analysisMilliseconds = Median(measured.Select(trial => trial.AnalysisMilliseconds!.Value)),
        resolutionInventoryMilliseconds = Median(measured.Select(trial => trial.ResolutionInventoryMilliseconds!.Value)),
        repeatedResolutionMilliseconds = Median(measured.Select(trial => trial.RepeatedResolutionMilliseconds!.Value))
    }
}, jsonOptions));
return allPassed ? 0 : 1;

static double Median(IEnumerable<double> values) => values.Order().ElementAt(1);
static string Fingerprint<T>(T value)
    => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

static Trial RunTrial(int index, GeneratedCorpus corpus, VbaSyntaxTree[] syntaxTrees,
    VbaReferenceSelection selection, VbaProjectReferenceCatalogSet catalogs,
    VbaProjectSemanticInputs inputs, int repetitions)
{
    var trial = new Trial { Index = index, ExcludedWarmup = index == 0 };
    try
    {
        var start = Stopwatch.GetTimestamp();
        var findings = VbaProjectSourceAnalysis.Analyze(syntaxTrees, inputs);
        trial.AnalysisMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        trial.DiagnosticCount = findings.Count;
        trial.DiagnosticsSha256 = Fingerprint(findings);
        // Four deliberately missing required arguments must be the only findings.
        // Positive calls include source shadowing and explicitly evidenced reference calls.
        if (findings.Count != corpus.ExpectedDiagnosticLines.Count
            || findings.Any(finding => finding.Code != "validation.incompatibleCallArgumentList"
                || finding.Severity != "error"
                || !corpus.ExpectedDiagnosticLines.TryGetValue(finding.SourceUri, out var expectedLine)
                || finding.Range.Start.Line != expectedLine)
            || findings.Select(finding => finding.SourceUri).Distinct(StringComparer.Ordinal).Count() != 4)
        {
            throw new InvalidOperationException("Generated-corpus diagnostic oracle failed: "
                + JsonSerializer.Serialize(findings));
        }

        start = Stopwatch.GetTimestamp();
        var resolver = new VbaNameResolutionService(corpus.ResolutionDocuments, selection, catalogs);
        trial.ResolutionInventoryMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var results = new VbaSourceDefinition?[checked(repetitions * corpus.Queries.Length)];
        start = Stopwatch.GetTimestamp();
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            for (var queryIndex = 0; queryIndex < corpus.Queries.Length; queryIndex++)
            {
                var query = corpus.Queries[queryIndex];
                results[repetition * corpus.Queries.Length + queryIndex] = resolver.Resolve(
                    query.Uri, new VbaPosition(5, 8), query.Qualifier, query.Identifier);
            }
        }
        trial.RepeatedResolutionMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        trial.ResolutionCount = results.Length;
        for (var resultIndex = 0; resultIndex < results.Length; resultIndex++)
        {
            var query = corpus.Queries[resultIndex % corpus.Queries.Length];
            var result = results[resultIndex];
            var matches = query.ExpectedOrigin is null
                ? result is null
                : result is not null && result.Identity.Origin == query.ExpectedOrigin
                    && result.Name == query.Identifier
                    && (query.ExpectedSourceUri is null || result.Uri == query.ExpectedSourceUri);
            if (!matches)
            {
                throw new InvalidOperationException($"Resolution oracle failed for {query.Uri}, "
                    + $"{query.Qualifier ?? "<unqualified>"}.{query.Identifier}.");
            }
        }
        trial.ResolutionsSha256 = Fingerprint(results.Take(corpus.Queries.Length).Select(result =>
            result is null ? null : new { result.Name, result.Uri, result.Range, result.Identity.Origin }));
        trial.Success = true;
    }
    catch (Exception exception)
    {
        trial.Failure = new { type = exception.GetType().FullName, exception.Message };
    }
    return trial;
}

sealed class Trial
{
    public int Index { get; init; }
    public bool ExcludedWarmup { get; init; }
    public bool Success { get; set; }
    public double? AnalysisMilliseconds { get; set; }
    public double? ResolutionInventoryMilliseconds { get; set; }
    public double? RepeatedResolutionMilliseconds { get; set; }
    public int DiagnosticCount { get; set; }
    public string? DiagnosticsSha256 { get; set; }
    public int ResolutionCount { get; set; }
    public string? ResolutionsSha256 { get; set; }
    public object? Failure { get; set; }
}

sealed record MeasurementOptions(string Variant, string Revision, int CallsPerDocument, int LookupRepetitions)
{
    public static MeasurementOptions Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length
                || arguments[index] is not ("--variant" or "--revision" or "--calls-per-document" or "--lookup-repetitions")
                || !values.TryAdd(arguments[index], arguments[index + 1]))
                throw new ArgumentException("Expected unique --variant, --revision, --calls-per-document, or --lookup-repetitions value pairs.");
        }
        var variant = values.GetValueOrDefault("--variant") ?? "unspecified";
        var revision = values.GetValueOrDefault("--revision") ?? "not-supplied";
        return new(variant, revision, ReadPositive("--calls-per-document", 128), ReadPositive("--lookup-repetitions", 2000));

        int ReadPositive(string name, int defaultValue)
        {
            if (!values.TryGetValue(name, out var text)) return defaultValue;
            if (!int.TryParse(text, out var value) || value < 1 || value > 100_000)
                throw new ArgumentException($"{name} must be an integer between 1 and 100000.");
            return value;
        }
    }
}

sealed record SourceFixture(string Uri, string Text);
sealed record ResolutionQuery(string Uri, string? Qualifier, string Identifier,
    VbaDefinitionOrigin? ExpectedOrigin, string? ExpectedSourceUri = null);

sealed record GeneratedCorpus(SourceFixture[] Sources, VbaSourceDocument[] ResolutionDocuments,
    VbaProjectReferenceCatalog Catalog, ResolutionQuery[] Queries,
    IReadOnlyDictionary<string, int> ExpectedDiagnosticLines)
{
    public const string ReferenceName = "Generated Automation Reference";

    public static GeneratedCorpus Create(int callGroups)
    {
        var sources = new List<SourceFixture>();
        var documents = new List<VbaSourceDocument>();
        var queries = new List<ResolutionQuery>();
        var expectedLines = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < 4; index++)
        {
            var name = $"Shared{index}";
            var text = $"Attribute VB_Name = \"{name}\"\nOption Explicit\nPublic Sub Ping{index}(ByVal value As Long)\nEnd Sub\nPrivate Sub Hidden()\nEnd Sub\n";
            var uri = SourceUri(name);
            sources.Add(new(uri, text));
            documents.Add(new(uri, text, name,
            [
                Definition(uri, name, name, VbaSourceDefinitionKind.Module, VbaSourceDefinitionVisibility.Public, 0, 21),
                Definition(uri, name, $"Ping{index}", VbaSourceDefinitionKind.Procedure, VbaSourceDefinitionVisibility.Public, 2, 11),
                Definition(uri, name, "Hidden", VbaSourceDefinitionKind.Procedure, VbaSourceDefinitionVisibility.Private, 4, 12)
            ]));
        }
        for (var index = 0; index < 4; index++)
        {
            var name = $"Caller{index}";
            var uri = SourceUri(name);
            var lines = new List<string>
            {
                $"Attribute VB_Name = \"{name}\"", "Option Explicit", $"Public Sub Run{index}()",
                "    Dim value As Long", "    Dim external As RefLib.Widget"
            };
            for (var group = 0; group < callGroups; group++)
                lines.AddRange([
                    "    Call Shared0.Ping0(value)", "    Call Ping1(value)",
                    "    Call Shadowed(value)", "    Call ReferenceTouch(value)",
                    "    Call RefLib.ReferenceTouch(value)", "    Call external.Touch(value)"
                ]);
            expectedLines.Add(uri, lines.Count);
            lines.Add("    Call Shared0.Ping0()");
            lines.Add("End Sub");
            var privateLine = lines.Count;
            lines.Add("Private Sub Shadowed(ByVal value As Long)");
            lines.Add("End Sub");
            var text = string.Join('\n', lines) + "\n";
            sources.Add(new(uri, text));
            documents.Add(new(uri, text, name,
            [
                Definition(uri, name, name, VbaSourceDefinitionKind.Module, VbaSourceDefinitionVisibility.Public, 0, 21),
                Definition(uri, name, $"Run{index}", VbaSourceDefinitionKind.Procedure, VbaSourceDefinitionVisibility.Public, 2, 11),
                Definition(uri, name, "Shadowed", VbaSourceDefinitionKind.Procedure, VbaSourceDefinitionVisibility.Private, privateLine, 12)
            ]));
            queries.AddRange([
                new(uri, null, "Ping0", VbaDefinitionOrigin.Source, SourceUri("Shared0")),
                new(uri, "Shared1", "Ping1", VbaDefinitionOrigin.Source, SourceUri("Shared1")),
                new(uri, null, "Shadowed", VbaDefinitionOrigin.Source, uri),
                new(uri, name, "Shadowed", VbaDefinitionOrigin.Source, uri),
                new(uri, null, "ReferenceTouch", VbaDefinitionOrigin.ProjectReference),
                new(uri, "RefLib", "ReferenceTouch", VbaDefinitionOrigin.ProjectReference),
                new(uri, null, "Missing", null),
                new(uri, "Shared0", "Hidden", null)
            ]);
        }
        return new(sources.ToArray(), documents.ToArray(), CreateCatalog(), queries.ToArray(), expectedLines);
    }

    private static string SourceUri(string name)
        => $"file:///C:/semantic-measurement/%E5%8B%A4%E5%8B%99%E8%A1%A8/{name}.bas";

    private static VbaSourceDefinition Definition(string uri, string module, string name,
        VbaSourceDefinitionKind kind, VbaSourceDefinitionVisibility visibility, int line, int character)
    {
        var range = new VbaRange(new(line, character), new(line, character + name.Length));
        return new(VbaDefinitionIdentity.ForSource(uri, name, range), new(uri, range), name, kind, visibility, module);
    }

    private static VbaProjectReferenceCatalog CreateCatalog()
    {
        var input = new VbaCallableParameter("value", TypeReference: new("Long"), IsByRef: false)
        {
            TypeLibPassing = new(VbaTypeLibParameterDirection.Input, 0)
        };
        VbaCallableSignature Signature(string name, bool secondRequired = false)
            => new($"Sub {name}(ByVal value As Long)", secondRequired ? [input, input with { Name = "second" }] : [input],
                CallableKind: VbaCallableKind.Sub, SupportsNamedArguments: true)
            {
                PassingConvention = VbaCallablePassingConvention.AutomationDispatch
            };
        return new(ReferenceName, ["RefLib"],
        [
            new(ReferenceName, "Widget", VbaSourceDefinitionKind.Class),
            new(ReferenceName, "Touch", VbaSourceDefinitionKind.Procedure, Signature: Signature("Touch"), ParentTypeName: "Widget"),
            new(ReferenceName, "ReferenceTouch", VbaSourceDefinitionKind.Procedure, Signature: Signature("ReferenceTouch"),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal),
            new(ReferenceName, "Shadowed", VbaSourceDefinitionKind.Procedure, Signature: Signature("Shadowed", secondRequired: true),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
        ]);
    }
}
