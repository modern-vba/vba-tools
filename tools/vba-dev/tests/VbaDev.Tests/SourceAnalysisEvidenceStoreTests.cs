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
}
