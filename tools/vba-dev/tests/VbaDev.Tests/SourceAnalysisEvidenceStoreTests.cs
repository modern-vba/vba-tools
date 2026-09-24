using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class SourceAnalysisEvidenceStoreTests
{
    // [DEBUG-415-uri-v1] Temporary observation tests, not a reproduction of the runtime NRE.
    [Fact]
    public void UriIdentificationFailureRetainsOriginalExceptionAndExactLocalEvidence()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("uri-parser-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var uri = scheme + "://host/日本語%20Module.bas";
        const string text = "Attribute VB_Name = \"Module1\"\n' private-source-sentinel\nPublic Sub Run()\nEnd Sub\n";
        var tree = VbaSyntaxTree.ParseModule(uri, text);

        var failure = Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([tree]));

        Assert.Same(sentinel, failure);
        Assert.Contains(nameof(FailingUriParser), failure.StackTrace);
        var builder = new VbaSourceAnalysisReport.Builder();
        builder.Add(tree);
        builder.Phase = "projectSemanticAnalysis";
        builder.FailProject(failure);
        var report = builder.ToReport();
        Assert.False(report.Complete);
        var directory = Path.Combine(temp.Path, "diagnostics");
        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", report);
        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        var raw = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
        Assert.DoesNotContain("private-source-sentinel", raw, StringComparison.Ordinal);
        using var saved = JsonDocument.Parse(raw);
        var savedFailure = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray());
        Assert.Equal("projectSemanticAnalysis", savedFailure.GetProperty("phase").GetString());
        var evidence = savedFailure.GetProperty("exception").GetProperty("uriIdentification");
        Assert.Equal("Uri.TryCreate", evidence.GetProperty("phase").GetString());
        Assert.Equal("inventory.admittedIdentities", evidence.GetProperty("stage").GetString());
        Assert.Equal("documentUri,sourceDefinitionUri", evidence.GetProperty("origins").GetString());
        Assert.Equal(0, evidence.GetProperty("inventoryIndex").GetInt32());
        Assert.Equal(uri.Length, evidence.GetProperty("uriCodeUnitLength").GetInt32());
        Assert.True(evidence.GetProperty("uriCaptureComplete").GetBoolean());
        Assert.Equal(string.Concat(uri.Select(character => ((int)character).ToString("X4"))),
            evidence.GetProperty("uriUtf16Hex").GetString());
        Assert.Same(sentinel, report.Failures[0].Exception);
        Assert.True(evidence.GetProperty("originDetailsComplete").GetBoolean());
        var details = evidence.GetProperty("originDetails").EnumerateArray().ToArray();
        var document = Assert.Single(details, item => item.GetProperty("category").GetString() == "documentUri");
        Assert.Equal(0, document.GetProperty("documentIndex").GetInt32());
        Assert.Equal(uri, document.GetProperty("documentUri").GetString());
        Assert.Equal("Module1", document.GetProperty("moduleName").GetString());
        var procedure = Assert.Single(details, item => item.TryGetProperty("definitionName", out var name)
            && name.GetString() == "Run");
        Assert.Equal("sourceDefinitionUri", procedure.GetProperty("category").GetString());
        Assert.Equal("Source", procedure.GetProperty("identityOrigin").GetString());
        Assert.Equal("Run", procedure.GetProperty("identityName").GetString());
        Assert.Equal(uri, procedure.GetProperty("identitySourceUri").GetString());
        Assert.Equal(2, procedure.GetProperty("startLine").GetInt32());
        Assert.True(procedure.GetProperty("fieldsComplete").GetBoolean());
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5000, false)]
    public void UriIdentificationEvidencePreservesCodeUnitsAndMarksCaptureBounds(int padding, bool complete)
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("bounded-uri-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        // Include an unpaired surrogate: text encoders must not silently replace these code units.
        var uri = scheme + "://host/日本語\ud800%2520" + new string('x', padding) + ".bas";
        var tree = VbaSyntaxTree.ParseModule(uri, "Attribute VB_Name = \"Module1\"\n");
        var failure = Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([tree]));
        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));

        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        var captured = Math.Min(4096, uri.Length);
        Assert.Equal(uri.Length, evidence.GetProperty("uriCodeUnitLength").GetInt32());
        Assert.Equal(captured, evidence.GetProperty("uriCapturedCodeUnits").GetInt32());
        Assert.Equal(complete, evidence.GetProperty("uriCaptureComplete").GetBoolean());
        Assert.Equal(string.Concat(uri.Take(captured).Select(character => ((int)character).ToString("X4"))),
            evidence.GetProperty("uriUtf16Hex").GetString());
        // JSON text may replace the unpaired surrogate in provenance identifiers; never claim fidelity.
        Assert.False(evidence.GetProperty("originDetailsComplete").GetBoolean());
    }

    [Fact]
    public void UriEvidenceRetainsEveryMatchingReferenceOwnerAndConcreteSourceLocation()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("origin-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var uri = scheme + "://host/Shared";
        var range = new VbaRange(new VbaPosition(4, 11), new VbaPosition(4, 17));
        var identityRange = new VbaRange(new VbaPosition(7, 2), new VbaPosition(7, 16));
        var source = new VbaSourceDefinition(VbaDefinitionIdentity.ForSource("file:///C:/identity/Owner.bas", "IdentityShared", identityRange),
            new(uri, range), "Shared", VbaSourceDefinitionKind.Procedure,
            VbaSourceDefinitionVisibility.Public, "DefinitionModule");
        var references = new[] { "FirstOwner", "SecondOwner" }.Select(owner => new VbaSourceDefinition(
            VbaDefinitionIdentity.ForProjectReference("ExampleLibrary", owner, VbaSourceDefinitionKind.Property, "Shared"),
            new(uri, range), "Shared", VbaSourceDefinitionKind.Property,
            VbaSourceDefinitionVisibility.Public, "ExampleLibrary", ParentTypeName: owner)).ToArray();
        var document = new VbaSourceDocument("file:///C:/origin/LocalModule.bas", "private-source-sentinel",
            "LocalModule", [source]);

        var failure = Assert.Throws<InvalidOperationException>(() => new VbaNameResolutionService(
            [document], null, VbaProjectReferenceCatalogSet.Empty, references));

        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        var raw = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
        Assert.DoesNotContain("private-source-sentinel", raw, StringComparison.Ordinal);
        using var saved = JsonDocument.Parse(raw);
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        Assert.True(evidence.GetProperty("originDetailsComplete").GetBoolean());
        var details = evidence.GetProperty("originDetails").EnumerateArray().ToArray();
        Assert.Equal(3, details.Length);
        Assert.Equal(document.Uri, details[0].GetProperty("documentUri").GetString());
        Assert.Equal(4, details[0].GetProperty("startLine").GetInt32());
        Assert.Equal(11, details[0].GetProperty("startCharacter").GetInt32());
        Assert.Equal("LocalModule", details[0].GetProperty("moduleName").GetString());
        Assert.Equal("DefinitionModule", details[0].GetProperty("definitionModuleName").GetString());
        Assert.Equal("file:///C:/identity/Owner.bas", details[0].GetProperty("identitySourceUri").GetString());
        Assert.Equal("IdentityShared", details[0].GetProperty("identityName").GetString());
        Assert.Equal(7, details[0].GetProperty("identityStartLine").GetInt32());
        Assert.Equal(2, details[0].GetProperty("identityStartCharacter").GetInt32());
        Assert.Equal(7, details[0].GetProperty("identityEndLine").GetInt32());
        Assert.Equal(16, details[0].GetProperty("identityEndCharacter").GetInt32());
        Assert.Equal(new[] { "FirstOwner", "SecondOwner" }, details.Skip(1)
            .Select(item => item.GetProperty("parentTypeName").GetString()));
        foreach (var detail in details.Skip(1))
        {
            Assert.Equal("activeReferenceDefinitionUri", detail.GetProperty("category").GetString());
            Assert.Equal("ExampleLibrary", detail.GetProperty("referenceName").GetString());
            Assert.Equal("Shared", detail.GetProperty("identityName").GetString());
            Assert.Equal("ProjectReference", detail.GetProperty("identityOrigin").GetString());
            Assert.Equal("Property", detail.GetProperty("definitionKind").GetString());
            Assert.Equal("Property", detail.GetProperty("identityKind").GetString());
        }
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(40, 32, true)]
    [InlineData(100001, 32, false)]
    public void UriOriginEvidenceBoundsMatchesAndDisclosesAnIncompleteScan(int count, int captured, bool scanComplete)
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("bounded-origins-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var document = new VbaSourceDocument(scheme + "://host/matching.bas", "not-captured", "Module", []);
        var documents = Enumerable.Repeat(document, count).ToArray();
        var failure = Assert.Throws<InvalidOperationException>(() => new VbaNameResolutionService(
            documents, null, VbaProjectReferenceCatalogSet.Empty, []));
        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        Assert.Equal(captured, evidence.GetProperty("originDetails").GetArrayLength());
        Assert.Equal(scanComplete, evidence.GetProperty("originScanComplete").GetBoolean());
        Assert.Equal(scanComplete, evidence.GetProperty("originsComplete").GetBoolean());
        Assert.Equal(count <= captured, evidence.GetProperty("originDetailsComplete").GetBoolean());
        Assert.Equal(Math.Min(100000, count), evidence.GetProperty("originMatchesObserved").GetInt32());
    }

    [Fact]
    public void ReusedParserExceptionReportsTheCurrentUriInsteadOfMixingInvocations()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("reused-parser-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var first = VbaSyntaxTree.ParseModule(scheme + "://host/first.bas", "Attribute VB_Name = \"First\"\n");
        Assert.Same(sentinel, Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([first])));
        var uri = scheme + "://host/current.bas";
        var current = VbaSyntaxTree.ParseModule(uri, "Attribute VB_Name = \"Current\"\n");
        var valid = VbaSyntaxTree.ParseModule("file:///C:/uri-probe/Valid.bas", "Attribute VB_Name = \"Valid\"\n");

        var failure = Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([valid, current]));

        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        Assert.Equal(string.Concat(uri.Select(character => ((int)character).ToString("X4"))),
            evidence.GetProperty("uriUtf16Hex").GetString());
        Assert.Equal(1, evidence.GetProperty("inventoryIndex").GetInt32());
    }

    [Fact]
    public void UnavailableExceptionDataDoesNotReplaceUriFailureOrPreventItsReport()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new UnavailableDataException();
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var tree = VbaSyntaxTree.ParseModule(scheme + "://host/Module.bas", "Attribute VB_Name = \"Module1\"\n");

        var failure = Assert.Throws<UnavailableDataException>(() => VbaProjectSourceAnalysis.Analyze([tree]));

        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var exception = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray()).GetProperty("exception");
        Assert.Equal(typeof(UnavailableDataException).FullName, exception.GetProperty("type").GetString());
        Assert.Contains(nameof(FailingUriParser), exception.GetProperty("stackTrace").GetString());
        Assert.Equal("unavailable", exception.GetProperty("uriIdentification").GetProperty("status").GetString());
    }

    [Fact]
    public void UriEvidenceStorageFailurePreservesTheFailureWithoutPrintingItsPrivateMetadata()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("uri-storage-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var uri = scheme + "://host/private-name.bas";
        var tree = VbaSyntaxTree.ParseModule(uri, "Attribute VB_Name = \"Module1\"\n");
        var failure = Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([tree]));
        var report = CreateReport(failure);
        var directory = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(directory, "preserve-existing-file");

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", report);

        Assert.Contains("could not be saved", output, StringComparison.Ordinal);
        Assert.Contains("uri-storage-sentinel", output, StringComparison.Ordinal);
        Assert.Contains(nameof(FailingUriParser), output, StringComparison.Ordinal);
        Assert.DoesNotContain(uri, output, StringComparison.Ordinal);
        Assert.DoesNotContain(string.Concat(uri.Select(character => ((int)character).ToString("X4"))), output, StringComparison.Ordinal);
        Assert.Same(sentinel, report.Failures[0].Exception);
        Assert.False(report.Complete);
        Assert.Equal("preserve-existing-file", File.ReadAllText(directory));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void UriEvidenceIgnoresUnrelatedExceptionDataAndReportsSharedTextBudgetTruncation()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException(new string('x', 20000));
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var tree = VbaSyntaxTree.ParseModule(scheme + "://host/" + new string('u', 5000), "Attribute VB_Name = \"Module1\"\n");
        var failure = Assert.Throws<InvalidOperationException>(() => VbaProjectSourceAnalysis.Analyze([tree]));
        failure.Data["unrelated-private-key"] = new UnrenderablePrivateValue();
        var owned = Assert.IsType<Dictionary<string, object>>(failure.Data["DEBUG-415-uri-v1"]);
        owned["unrelated-private-field"] = new UnrenderablePrivateValue();
        var builder = new VbaSourceAnalysisReport.Builder();
        for (var index = 0; index < 16; index++) builder.FailProject(failure);
        var directory = Path.Combine(temp.Path, "diagnostics");

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", builder.ToReport());

        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        var path = Assert.Single(Directory.GetFiles(directory));
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("unrelated-private", raw, StringComparison.Ordinal);
        Assert.True(new FileInfo(path).Length < SourceAnalysisEvidenceStore.MaximumReportBytes);
        using var saved = JsonDocument.Parse(raw);
        Assert.True(saved.RootElement.GetProperty("truncation").GetProperty("textTruncated").GetBoolean());
        var records = saved.RootElement.GetProperty("failures").EnumerateArray()
            .Select(item => item.GetProperty("exception").GetProperty("uriIdentification")).ToArray();
        Assert.Contains(records, record => record.GetProperty("uriCapturedCodeUnits").GetInt32() == 0);
        Assert.DoesNotContain(records.SelectMany(record => record.GetProperty("originDetails").EnumerateArray())
            .SelectMany(detail => detail.EnumerateObject()).Where(property => property.Value.ValueKind == JsonValueKind.String),
            property => property.Value.GetString()!.EndsWith(" [truncated]", StringComparison.Ordinal));
        foreach (var record in records)
        {
            Assert.False(record.GetProperty("uriCaptureComplete").GetBoolean());
            Assert.False(record.GetProperty("originDetailsComplete").GetBoolean());
            var hex = record.GetProperty("uriUtf16Hex").GetString()!;
            Assert.Equal(0, hex.Length % 4);
            Assert.Equal(hex.Length / 4, record.GetProperty("uriCapturedCodeUnits").GetInt32());
        }
    }

    [Fact]
    public void UriOriginIdentifierTextIsBoundedWithoutSerializingOtherDefinitionContent()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("origin-text-bound-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var uri = scheme + "://host/short.bas";
        var document = new VbaSourceDocument(uri, "private-source-content", new string('M', 5000), []);
        var failure = Assert.Throws<InvalidOperationException>(() => new VbaNameResolutionService(
            Enumerable.Repeat(document, 32).ToArray(), null, VbaProjectReferenceCatalogSet.Empty, []));
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        var raw = File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
        Assert.DoesNotContain("private-source-content", raw, StringComparison.Ordinal);
        using var saved = JsonDocument.Parse(raw);
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        Assert.True(evidence.GetProperty("uriCaptureComplete").GetBoolean());
        Assert.True(evidence.GetProperty("originScanComplete").GetBoolean());
        Assert.False(evidence.GetProperty("originDetailsComplete").GetBoolean());
        var details = evidence.GetProperty("originDetails").EnumerateArray().ToArray();
        Assert.Equal(32, details.Length);
        Assert.Equal(2048, details[0].GetProperty("moduleName").GetString()!.Length);
        Assert.Equal(string.Empty, details[^1].GetProperty("moduleName").GetString());
        Assert.All(details, detail => Assert.False(detail.GetProperty("fieldsComplete").GetBoolean()));
        Assert.True(details.Sum(detail => detail.GetProperty("moduleName").GetString()!.Length
            + detail.GetProperty("documentUri").GetString()!.Length) <= 16384);
    }

    [Fact]
    public void UriOriginExtractionFailureCannotClaimThatItsPartialRecordIsComplete()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new InvalidOperationException("partial-origin-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var uri = scheme + "://host/definition";
        var range = new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, 3));
        var definition = new VbaSourceDefinition(VbaDefinitionIdentity.ForSource(uri, "Run", range),
            new(uri, null!), "Run", VbaSourceDefinitionKind.Procedure,
            VbaSourceDefinitionVisibility.Public, "Module");
        var document = new VbaSourceDocument("file:///C:/partial/Module.bas", "", "Module", [definition]);
        var failure = Assert.Throws<InvalidOperationException>(() => new VbaNameResolutionService(
            [document], null, VbaProjectReferenceCatalogSet.Empty, []));
        Assert.Same(sentinel, failure);
        var directory = Path.Combine(temp.Path, "diagnostics");
        new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        using var saved = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var evidence = Assert.Single(saved.RootElement.GetProperty("failures").EnumerateArray())
            .GetProperty("exception").GetProperty("uriIdentification");
        Assert.False(evidence.GetProperty("originScanComplete").GetBoolean());
        Assert.False(evidence.GetProperty("originDetailsComplete").GetBoolean());
        Assert.False(Assert.Single(evidence.GetProperty("originDetails").EnumerateArray())
            .GetProperty("fieldsComplete").GetBoolean());
    }

    [Fact]
    public void UriParserCancellationRemainsCancellationWithoutCreatingEvidence()
    {
        using var temp = TempDirectory.Create();
        var sentinel = new OperationCanceledException("cancelled-uri-sentinel");
        var scheme = "vba415-" + Guid.NewGuid().ToString("N");
        UriParser.Register(new FailingUriParser(sentinel), scheme, -1);
        var tree = VbaSyntaxTree.ParseModule(scheme + "://host/Module.bas", "Attribute VB_Name = \"Module1\"\n");

        var failure = Assert.Throws<OperationCanceledException>(() => VbaProjectSourceAnalysis.Analyze([tree]));

        Assert.Same(sentinel, failure);
        Assert.False(failure.Data.Contains("DEBUG-415-uri-v1"));
        var directory = Path.Combine(temp.Path, "diagnostics");
        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(failure));
        Assert.Empty(output);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void FailureRetainsStackInnerCausePhaseAndCapturedUnicodeTextIdentityWithoutSourceText()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var directory = Path.Combine(temp.Path, "diagnostics");
        const string text = "Attribute VB_Name = \"SecretSource\"\n' 非公開内容 😀 secret-source-sentinel\nPublic Sub Run()\nEnd Sub\n";
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "SecretSource.bas");
        var sourceUri = new Uri(sourcePath).AbsoluteUri;
        var cause = CaptureFailure();
        var builder = new VbaSourceAnalysisReport.Builder
        {
            SourceDirectory = context.DocumentSourceSetPath,
            AdmissionPurpose = "ProjectBuild",
            ActiveCodePage = 932,
            ActiveSourcePath = sourcePath,
            SemanticInputs = CreateInputs()
        };
        builder.Add(VbaSyntaxTree.ParseModule(sourceUri, text));
        builder.Phase = "projectSemantics";
        builder.FailProject(cause);

        var output = new SourceAnalysisEvidenceStore(directory).Save(context, "build", builder.ToReport());

        var savedPath = Assert.Single(Directory.GetFiles(directory));
        Assert.Equal($"Source-analysis failure evidence saved: {savedPath}{Environment.NewLine}", output);
        Assert.False(File.Exists(sourcePath));
        var raw = File.ReadAllText(savedPath);
        Assert.DoesNotContain("secret-source-sentinel", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Attribute VB_Name", raw, StringComparison.Ordinal);
        Assert.True(new FileInfo(savedPath).Length < SourceAnalysisEvidenceStore.MaximumReportBytes);
        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.True(Guid.TryParseExact(root.GetProperty("invocationId").GetString(), "N", out _));
        Assert.Equal(TimeSpan.Zero, root.GetProperty("timestampUtc").GetDateTimeOffset().Offset);
        var project = root.GetProperty("project");
        Assert.Equal(context.ProjectRoot, project.GetProperty("root").GetString());
        Assert.Equal("Book1", project.GetProperty("document").GetString());
        Assert.Equal("build", project.GetProperty("operation").GetString());
        Assert.Equal("ProjectBuild", project.GetProperty("admissionPurpose").GetString());
        Assert.Equal(932, project.GetProperty("activeCodePage").GetInt32());
        var failure = Assert.Single(root.GetProperty("failures").EnumerateArray());
        Assert.Equal("projectSemantics", failure.GetProperty("phase").GetString());
        Assert.Equal(sourcePath, failure.GetProperty("activeSourcePath").GetString());
        var exception = failure.GetProperty("exception");
        Assert.Equal(typeof(InvalidOperationException).FullName, exception.GetProperty("type").GetString());
        Assert.Contains(nameof(CaptureFailure), exception.GetProperty("stackTrace").GetString());
        Assert.Contains(typeof(NullReferenceException).FullName!, exception.GetProperty("details").GetString());
        Assert.Contains("root-cause-sentinel", exception.GetProperty("details").GetString());
        var source = Assert.Single(root.GetProperty("sources").EnumerateArray());
        Assert.Equal(sourceUri, source.GetProperty("uri").GetString());
        Assert.Equal(text.Length, source.GetProperty("characterCount").GetInt32());
        Assert.Equal("captured-parser-text:utf8", source.GetProperty("hashDomain").GetString());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), source.GetProperty("sha256").GetString());
        var inputs = root.GetProperty("semanticInputs");
        Assert.True(inputs.GetProperty("acquired").GetBoolean());
        Assert.Equal(1, inputs.GetProperty("referenceCount").GetInt32());
        var reference = Assert.Single(inputs.GetProperty("references").EnumerateArray());
        Assert.Equal("Scripting", reference.GetProperty("name").GetString());
        Assert.Equal("420B2830-E718-11CF-893D-00A0C9054228", reference.GetProperty("guid").GetString());
        var runtime = root.GetProperty("runtime");
        Assert.NotEmpty(runtime.GetProperty("framework").GetString()!);
        Assert.Contains(runtime.GetProperty("assemblies").EnumerateArray(), assembly =>
            assembly.TryGetProperty("name", out var name) && name.GetString() == "VbaDev.App"
                && Guid.TryParse(assembly.GetProperty("moduleVersionId").GetString(), out _));
        Assert.False(root.GetProperty("truncation").GetProperty("textTruncated").GetBoolean());
    }

    [Fact]
    public void StorageFailureReturnsOriginalStackAndActionableWarningWithoutThrowing()
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(directory, "existing-file");
        var cause = CaptureFailure();

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "publish", CreateReport(cause));

        Assert.Contains("could not be saved", output, StringComparison.Ordinal);
        Assert.Contains("permissions and available disk space", output, StringComparison.Ordinal);
        Assert.Contains(nameof(CaptureFailure), output, StringComparison.Ordinal);
        Assert.Contains("root-cause-sentinel", output, StringComparison.Ordinal);
        Assert.Contains("projectSemantics", output, StringComparison.Ordinal);
        Assert.Equal("existing-file", File.ReadAllText(directory));
        Assert.Equal(new[] { directory }, Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessOrCancellationOnlyCreatesNoDirectoryAndNoOutput(bool cancellation)
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, "unused");
        var builder = new VbaSourceAnalysisReport.Builder();
        if (cancellation) builder.FailProject(new AggregateException(new OperationCanceledException("cancelled")));

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", builder.ToReport());

        Assert.Empty(output);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void CancellationAlongsideRealFailureStillProducesEvidence()
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, "diagnostics");
        var builder = new VbaSourceAnalysisReport.Builder();
        builder.FailProject(new OperationCanceledException("cancelled"));
        builder.FailProject(CaptureFailure());

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", builder.ToReport());

        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task ConcurrentSavesUseUniqueCompleteFilesAndRetainOnlyNewestOwnReports()
    {
        using var temp = TempDirectory.Create();
        var directory = temp.CreateDirectory("diagnostics");
        var unrelated = Path.Combine(directory, "source-analysis-user-notes.json");
        File.WriteAllText(unrelated, "preserve");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "source-analysis-20000101T0000000000000Z-00000000000000000000000000000000.json"), "preserve");
        var context = CreateContext(temp.Path);
        var report = CreateReport(CaptureFailure());
        var outputs = await Task.WhenAll(Enumerable.Range(0, SourceAnalysisEvidenceStore.MaximumReports + 5)
            .Select(_ => Task.Run(() => new SourceAnalysisEvidenceStore(directory).Save(context, "build", report))));

        Assert.Equal(outputs.Length, outputs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(outputs, output => Assert.StartsWith("Source-analysis failure evidence saved: ", output));
        Assert.DoesNotContain(outputs, output => output.Contains("Warning:", StringComparison.Ordinal));
        var paths = Directory.GetFiles(directory).Where(path => path != unrelated).ToArray();
        Assert.Equal(SourceAnalysisEvidenceStore.MaximumReports, paths.Length);
        var ids = paths.Select(path =>
        {
            Assert.EndsWith(".json", path, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("invocationId").GetString();
        }).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var expectedNewest = outputs.Select(output => output["Source-analysis failure evidence saved: ".Length..].TrimEnd('\r', '\n'))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(SourceAnalysisEvidenceStore.MaximumReports)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedNewest, paths.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.Single(Directory.GetFiles(nested));
    }

    [Fact]
    public void OversizedFailuresAndSourceInventoriesAreBoundedAndExplicitlyTruncated()
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, "diagnostics");
        var builder = new VbaSourceAnalysisReport.Builder();
        var source = VbaSyntaxTree.ParseModule("file:///C:/fixtures/Module1.bas", "Attribute VB_Name = \"Module1\"\n");
        for (var index = 0; index < SourceAnalysisEvidenceStore.MaximumSources + 1; index++) builder.Add(source);
        for (var index = 0; index < SourceAnalysisEvidenceStore.MaximumFailures + 2; index++)
            builder.FailProject(new InvalidOperationException(new string('x', 20000)));

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", builder.ToReport());

        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        var path = Assert.Single(Directory.GetFiles(directory));
        Assert.True(new FileInfo(path).Length < SourceAnalysisEvidenceStore.MaximumReportBytes);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(temp.Path, root.GetProperty("project").GetProperty("root").GetString());
        Assert.Equal(SourceAnalysisEvidenceStore.MaximumFailures, root.GetProperty("failures").GetArrayLength());
        Assert.Equal(SourceAnalysisEvidenceStore.MaximumSources, root.GetProperty("sources").GetArrayLength());
        var truncation = root.GetProperty("truncation");
        Assert.Equal(2, truncation.GetProperty("failuresOmitted").GetInt32());
        Assert.Equal(1, truncation.GetProperty("sourcesOmitted").GetInt32());
        Assert.True(truncation.GetProperty("textTruncated").GetBoolean());
    }

    [Fact]
    public void DiagnosticsDirectoryCannotTraverseASymbolicLink()
    {
        using var temp = TempDirectory.Create();
        var actual = temp.CreateDirectory("actual");
        var link = Path.Combine(temp.Path, "alias");
        Directory.CreateSymbolicLink(link, actual);
        try
        {
            var output = new SourceAnalysisEvidenceStore(Path.Combine(link, "diagnostics"))
                .Save(CreateContext(temp.Path), "build", CreateReport(CaptureFailure()));

            Assert.Contains("could not be saved", output, StringComparison.Ordinal);
            Assert.Contains("reparse point", output, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFileSystemEntries(actual));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void RetentionDoesNotFollowAnOwnNamedReportSymbolicLink()
    {
        using var temp = TempDirectory.Create();
        var directory = temp.CreateDirectory("diagnostics");
        var sentinel = Path.Combine(temp.Path, "sentinel.json");
        File.WriteAllText(sentinel, "preserve");
        var link = Path.Combine(directory, "source-analysis-20000101T0000000000000Z-00000000000000000000000000000000.json");
        File.CreateSymbolicLink(link, sentinel);
        try
        {
            var store = new SourceAnalysisEvidenceStore(directory);
            var context = CreateContext(temp.Path);
            var report = CreateReport(CaptureFailure());
            for (var index = 0; index < SourceAnalysisEvidenceStore.MaximumReports + 1; index++)
                Assert.DoesNotContain("Warning:", store.Save(context, "build", report), StringComparison.Ordinal);

            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(SourceAnalysisEvidenceStore.MaximumReports + 1, Directory.GetFiles(directory).Length);
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public void ExceptionTextRenderingFailureDoesNotPreventEvidenceFromBeingSaved()
    {
        using var temp = TempDirectory.Create();
        var directory = Path.Combine(temp.Path, "diagnostics");

        var output = new SourceAnalysisEvidenceStore(directory).Save(CreateContext(temp.Path), "build", CreateReport(new UnrenderableException()));

        Assert.Contains("evidence saved:", output, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        var exception = Assert.Single(document.RootElement.GetProperty("failures").EnumerateArray()).GetProperty("exception");
        Assert.Equal(typeof(UnrenderableException).FullName, exception.GetProperty("type").GetString());
        Assert.Contains("exception text unavailable", exception.GetProperty("details").GetString());
    }

    private static VbaSourceAnalysisReport CreateReport(Exception error)
    {
        var builder = new VbaSourceAnalysisReport.Builder { Phase = "projectSemantics" };
        builder.FailProject(error);
        return builder.ToReport();
    }

    private static InvalidOperationException CaptureFailure()
    {
        try
        {
            try { throw new NullReferenceException("root-cause-sentinel"); }
            catch (Exception inner) { throw new InvalidOperationException("analysis-sentinel", inner); }
        }
        catch (InvalidOperationException error) { return error; }
    }

    private static VbaProjectSemanticInputs CreateInputs()
        => VbaProjectSemanticInputs.Capture(VbaReferenceSelection.Capture(["Scripting"], null),
            VbaProjectReferenceCatalogSet.Empty,
            identities: new Dictionary<string, VbaProjectReferenceCatalogIdentity>
            {
                ["Scripting"] = new("Scripting", "420B2830-E718-11CF-893D-00A0C9054228", 1, 0, 0, "C:/Windows/System32/scrrun.dll")
            }, sources: new Dictionary<string, VbaProjectReferenceCatalogSource> { ["Scripting"] = VbaProjectReferenceCatalogSource.Generated });

    private static ResolvedProjectContext CreateContext(string root)
    {
        var manifest = ProjectManifest.CreateDefault("EvidenceFixture", "Book1", root, null);
        var document = manifest.Documents["Book1"];
        return new(root, Path.Combine(root, "vba-project.json"), manifest, "Book1", document,
            Path.Combine(root, document.SourcePath), Path.Combine(root, document.TemplatePath),
            Path.Combine(root, document.BinPath), Path.Combine(root, document.PublishPath), null);
    }

    private sealed class UnrenderableException : Exception
    {
        public override string ToString() => throw new InvalidOperationException("cannot render exception");
    }

    private sealed class FailingUriParser(Exception failure) : UriParser
    {
        protected override UriParser OnNewUri() => throw failure;
    }

    private sealed class UnavailableDataException : InvalidOperationException
    {
        public override System.Collections.IDictionary Data => throw new InvalidOperationException("Data unavailable");
    }

    private sealed class UnrenderablePrivateValue
    {
        public override string ToString() => throw new InvalidOperationException("unrelated-private-value");
    }
}
