using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceDiagnosticsTests
{
    [Fact]
    public async Task OrdinaryBuildReportsAllCapturedSyntaxAndDocumentErrorsBeforeGeneration()
    {
        using var fixture = new BuildFixture();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var laterSource = fixture.SourcePath("ZLater.bas");
        File.WriteAllText(firstSource, string.Join('\n',
        [
            "Attribute VB_Name = \"AFirst\"",
            "Public Sub Run(ByVal name As String, ByVal name As Long)",
            "    value = \"unterminated",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        File.WriteAllText(laterSource, string.Join('\n',
        [
            "Attribute VB_Name = \"ZLater\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, ARG1:=2)",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        var result = await fixture.RunAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("failures").EnumerateArray());
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "syntax.unterminatedStringLiteral", "String literal is missing a closing double quote.",
                    "error", 2, 12, 2, 25),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "validation.duplicateCallableParameterName", "Duplicate callable parameter name 'name'.",
                    "error", 1, 43, 1, 47),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(laterSource).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 2, 21, 2, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildContinuesAfterSourceReadFailureAndReportsIncompleteAnalysis()
    {
        using var fixture = new BuildFixture();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var laterSource = fixture.SourcePath("ZLater.bas");
        File.WriteAllText(firstSource, "Attribute VB_Name = \"AFirst\"\n", new UTF8Encoding(false));
        File.WriteAllText(laterSource, string.Join('\n',
        [
            "Attribute VB_Name = \"ZLater\"",
            "Public Sub Run()",
            "    value = \"unterminated",
            "    Example(Arg1:=1, ARG1:=2)",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        var reads = new List<string>();
        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, firstSource))
            {
                throw new IOException("Earlier source read failed.");
            }
            return File.ReadAllBytes(path);
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("source", failure.GetProperty("scope").GetString());
        Assert.Equal(new Uri(firstSource).AbsoluteUri, failure.GetProperty("uri").GetString());
        Assert.Equal("Earlier source read failed.", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("range", out _));
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(laterSource).AbsoluteUri,
                    "syntax.unterminatedStringLiteral", "String literal is missing a closing double quote.",
                    "error", 2, 12, 2, 25),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(laterSource).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 3, 21, 3, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        Assert.Equal(new[] { firstSource, laterSource }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildKeepsFormSyntaxFindingsAfterSidecarReadFailureAndAnalyzesLaterSources()
    {
        using var fixture = new BuildFixture();
        var formPath = fixture.SourcePath("ADialog.frm");
        var sidecarPath = fixture.SourcePath("ADialog.frx");
        var laterPath = fixture.SourcePath("ZLater.bas");
        File.WriteAllText(formPath, string.Join('\n',
        [
            "VERSION 5.00",
            "Begin VB.Form ADialog",
            "End",
            "Attribute VB_Name = \"ADialog\"",
            "Public Sub Run()",
            "    value = \"unterminated",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        File.WriteAllBytes(sidecarPath, [0, 1, 127, 255]);
        File.WriteAllText(laterPath, string.Join('\n',
        [
            "Attribute VB_Name = \"ZLater\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, ARG1:=2)",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        var reads = new List<string>();
        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, sidecarPath))
            {
                throw new IOException("Form sidecar read failed.");
            }
            return File.ReadAllBytes(path);
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(formPath).AbsoluteUri,
                    "syntax.unterminatedStringLiteral", "String literal is missing a closing double quote.",
                    "error", 5, 12, 5, 25),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(laterPath).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 2, 21, 2, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("source", failure.GetProperty("scope").GetString());
        Assert.Equal(new Uri(formPath).AbsoluteUri, failure.GetProperty("uri").GetString());
        Assert.Equal("Form sidecar read failed.", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("range", out _));
        Assert.Equal(new[] { formPath, sidecarPath, laterPath }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildContinuesAfterStrictSourceDecodeFailure()
    {
        using var fixture = new BuildFixture();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var laterSource = fixture.SourcePath("ZLater.bas");
        File.WriteAllBytes(firstSource, [0xef, 0xbb, 0xbf, 0xff]);
        File.WriteAllText(laterSource, string.Join('\n',
        [
            "Attribute VB_Name = \"ZLater\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, ARG1:=2)",
            "End Sub",
            string.Empty
        ]), new UTF8Encoding(false));
        var reads = new List<string>();
        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            return File.ReadAllBytes(path);
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(laterSource).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 2, 21, 2, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("source", failure.GetProperty("scope").GetString());
        Assert.Equal(new Uri(firstSource).AbsoluteUri, failure.GetProperty("uri").GetString());
        Assert.Equal($"VBA source '{firstSource}' cannot be strictly decoded as utf8bom without changing its bytes.",
            failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("range", out _));
        Assert.Equal(new[] { firstSource, laterSource }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildKeepsEarlierFindingsAndStopsAfterProjectFatalProcessingFailure()
    {
        using var fixture = new BuildFixture();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var failedSource = fixture.SourcePath("ZFailure.bas");
        var unreadSource = fixture.SourcePath("ZZAfter.bas");
        File.WriteAllText(firstSource,
            "Attribute VB_Name = \"AFirst\"\nPublic Sub Run()\n    Example(Arg1:=1, ARG1:=2)\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(failedSource, "Attribute VB_Name = \"ZFailure\"\n", new UTF8Encoding(false));
        File.WriteAllText(unreadSource, "Attribute VB_Name = \"ZZAfter\"\n", new UTF8Encoding(false));
        var reads = new List<string>();

        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, failedSource))
            {
                throw new NullReferenceException("Unexpected source reader defect.");
            }
            return File.ReadAllBytes(path);
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 2, 21, 2, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("project", failure.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("uri").ValueKind);
        Assert.Equal("Unexpected source reader defect.", failure.GetProperty("message").GetString());
        Assert.False(failure.TryGetProperty("range", out _));
        Assert.Equal(new[] { firstSource, failedSource }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildPreservesActualCallerCancellationWithoutReportingAnalysisFailure()
    {
        using var fixture = new BuildFixture();
        using var cancellation = new CancellationTokenSource();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var cancelledSource = fixture.SourcePath("ZCancel.bas");
        var unreadSource = fixture.SourcePath("ZZAfter.bas");
        File.WriteAllText(firstSource,
            "Attribute VB_Name = \"AFirst\"\nPublic Sub Run()\n    Example(Arg1:=1, ARG1:=2)\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(cancelledSource, "Attribute VB_Name = \"ZCancel\"\n", new UTF8Encoding(false));
        File.WriteAllText(unreadSource, "Attribute VB_Name = \"ZZAfter\"\n", new UTF8Encoding(false));
        var reads = new List<string>();

        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, cancelledSource))
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return File.ReadAllBytes(path);
        }, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal("Workbook automation was cancelled during the active generation stage." + Environment.NewLine,
            result.StandardError);
        Assert.DoesNotContain("sourceAnalysis", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(new[] { firstSource, cancelledSource }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryBuildReportsANonemptyReasonWhenSourceProcessingExceptionMessageIsEmpty(bool projectFatal)
    {
        using var fixture = new BuildFixture();
        var firstSource = fixture.SourcePath("AFirst.bas");
        var failedSource = fixture.SourcePath("ZFailure.bas");
        var laterSource = fixture.SourcePath("ZZAfter.bas");
        File.WriteAllText(firstSource,
            "Attribute VB_Name = \"AFirst\"\nPublic Sub Run()\n    Example(Arg1:=1, ARG1:=2)\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(failedSource, "Attribute VB_Name = \"ZFailure\"\n", new UTF8Encoding(false));
        File.WriteAllText(laterSource, "Attribute VB_Name = \"ZZAfter\"\n", new UTF8Encoding(false));
        var reads = new List<string>();

        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, failedSource))
            {
                if (projectFatal) throw new NullReferenceException(string.Empty);
                throw new IOException(string.Empty);
            }
            return File.ReadAllBytes(path);
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
        Assert.Equal(projectFatal ? "project" : "source", failure.GetProperty("scope").GetString());
        if (projectFatal)
        {
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("uri").ValueKind);
        }
        else
        {
            Assert.Equal(new Uri(failedSource).AbsoluteUri, failure.GetProperty("uri").GetString());
        }
        Assert.False(failure.TryGetProperty("range", out _));
        Assert.Equal(
            new[]
            {
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "validation.duplicateNamedCallArgument", "Duplicate named call argument 'ARG1'.",
                    "error", 2, 21, 2, 25)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        Assert.Equal(projectFatal
            ? new[] { firstSource, failedSource }
            : new[] { firstSource, failedSource, laterSource }, reads);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildAnalyzesAndImportsCapturedBytesAfterAuthoringSourcesChangeOrDisappear()
    {
        using var fixture = new BuildFixture();
        var formPath = fixture.SourcePath("ADialog.frm");
        var sidecarPath = fixture.SourcePath("ADialog.frx");
        var laterPath = fixture.SourcePath("ZLater.bas");
        const string formText =
            "VERSION 5.00\nBegin VB.Form ADialog\nEnd\nAttribute VB_Name = \"ADialog\"\nOption Explicit\nPublic Sub Run()\nEnd Sub\n";
        const string laterText =
            "Attribute VB_Name = \"ZLater\"\nOption Explicit\nPublic Sub Run()\nEnd Sub\n";
        const string changedFormText =
            "Attribute VB_Name = \"Changed\"\nPublic Sub Run()\n    value = \"unterminated\nEnd Sub\n";
        byte[] sidecarBytes = [0, 1, 127, 255];
        File.WriteAllText(formPath, formText, new UTF8Encoding(false));
        File.WriteAllBytes(sidecarPath, sidecarBytes);
        File.WriteAllText(laterPath, laterText, new UTF8Encoding(false));
        var reads = new List<string>();
        var importedBytes = new List<(VbeImportSourceFile Source, byte[] Text, byte[]? Binary)>();
        fixture.Automation.OnImport = () =>
        {
            var source = fixture.Automation.ImportedSources[^1];
            importedBytes.Add((source, File.ReadAllBytes(source.SourcePath),
                source.BinaryPath is null ? null : File.ReadAllBytes(source.BinaryPath)));
        };

        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            var bytes = File.ReadAllBytes(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, laterPath))
            {
                File.WriteAllText(formPath, changedFormText, new UTF8Encoding(false));
                File.Delete(sidecarPath);
                File.Delete(laterPath);
            }
            return bytes;
        });

        fixture.AssertSuccessfulBuild(result, importedSourceCount: 2, sourcesUnchanged: false);
        Assert.Equal(new[] { formPath, sidecarPath, laterPath }, reads);
        Assert.Equal(new[] { "ADialog.frm", "ZLater.bas" }, importedBytes.Select(item => item.Source.FileName));
        var form = importedBytes[0];
        Assert.Equal(new UTF8Encoding(false).GetBytes(formText), form.Text);
        Assert.Equal(sidecarBytes, form.Binary);
        Assert.Equal(formPath, form.Source.DiagnosticSourcePath);
        Assert.Equal("ADialog", form.Source.ImportVerification.ComponentName);
        Assert.Equal(VbaSourceKind.Form, form.Source.ImportVerification.ComponentKind);
        Assert.Equal(new[] { string.Empty, "Option Explicit", "Public Sub Run()", "End Sub" },
            form.Source.ImportVerification.CodeModuleLines);
        var later = importedBytes[1];
        Assert.Equal(new UTF8Encoding(false).GetBytes(laterText), later.Text);
        Assert.Null(later.Binary);
        Assert.Equal(laterPath, later.Source.DiagnosticSourcePath);
        Assert.Equal("ZLater", later.Source.ImportVerification.ComponentName);
        Assert.Equal(VbaSourceKind.StandardModule, later.Source.ImportVerification.ComponentKind);
        Assert.Equal(new[] { "Option Explicit", "Public Sub Run()", "End Sub" },
            later.Source.ImportVerification.CodeModuleLines);
        Assert.Equal(changedFormText, File.ReadAllText(formPath));
        Assert.False(File.Exists(sidecarPath));
        Assert.False(File.Exists(laterPath));
    }

    public static IEnumerable<object[]> ConformanceCases()
        => ReadConformanceCases().Select(fixture => new object[] { fixture.GetProperty("id").GetString()! });

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public async Task OrdinaryBuildDiagnosticsMatchTheNeutralLiteralCorpus(string caseId)
    {
        var corpus = ReadConformanceCases().Single(item => item.GetProperty("id").GetString() == caseId);
        using var fixture = new BuildFixture();
        var sourcePath = fixture.SourcePath(corpus.GetProperty("fileName").GetString()!);
        File.WriteAllText(sourcePath, corpus.GetProperty("source").GetString()!, new UTF8Encoding(false));
        var expected = corpus.GetProperty("syntaxDiagnostics").EnumerateArray()
            .Concat(corpus.GetProperty("documentValidationDiagnostics").EnumerateArray())
            .Select(diagnostic => ReadDiagnostic(diagnostic, "diagnostic", "vba-dev", new Uri(sourcePath).AbsoluteUri))
            .ToArray();

        var result = await fixture.RunAsync();

        if (caseId == "valid-control")
        {
            Assert.Empty(expected);
            fixture.AssertSuccessfulBuild(result, importedSourceCount: 1);
            return;
        }

        Assert.NotEmpty(expected);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("failures").EnumerateArray());
        Assert.Equal(expected, report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    private static JsonElement[] ReadConformanceCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "document-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(fixture => fixture.Clone()).ToArray();
    }

    private static JsonElement ReadSourceAnalysisReport(string standardError)
    {
        var report = Assert.Single(standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith('{'))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
            record => record.TryGetProperty("type", out var type) && type.GetString() == "sourceAnalysis");
        Assert.Equal("2.0", report.GetProperty("schemaVersion").GetString());
        return report;
    }

    private sealed class BuildFixture : IDisposable
    {
        private readonly TempDirectory temp = TempDirectory.Create();
        private readonly RecordingOutputTransactionFactory transactions = new();
        private readonly ResolvedProjectContext context;
        private readonly List<string> mirrorPaths = [];
        private Dictionary<string, byte[]> originalFiles = [];
        private int mirrorCreations;

        public BuildFixture()
        {
            var root = temp.CreateDirectory("Project");
            new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
            SourceDirectory = Path.Combine(root, "src", "Book1");
            Directory.CreateDirectory(SourceDirectory);
            File.WriteAllText(SourcePath("Book1.xlsm"), "source-template", new UTF8Encoding(false));
            context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
                new ProjectResolutionRequest(root, null, root));
            Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
            File.WriteAllText(context.BinDocumentPath, "previous-completed-build", new UTF8Encoding(false));
        }

        private string SourceDirectory { get; }

        public FakeWorkbookGenerationAutomation Automation { get; } = new();

        public string SourcePath(string fileName) => Path.Combine(SourceDirectory, fileName);

        public Task<CommandResult> RunAsync(
            Func<string, byte[]>? readAllBytes = null,
            CancellationToken cancellationToken = default)
        {
            originalFiles = Directory.GetFiles(SourceDirectory, "*", SearchOption.AllDirectories)
                .Append(context.BinDocumentPath)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            var ownership = new WindowsExactFileSystemObjectOwnershipFactory();
            var materializer = new WorkbookMaterializer(
                ownership,
                new VbaSourceAdmission(() => 65001, readAllBytes: readAllBytes),
                Automation,
                new WorkbookReferenceNormalizer(new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
                transactions,
                new VbeImportSourceSetFactory(ownership, sourceSet =>
                {
                    mirrorCreations++;
                    mirrorPaths.Add(sourceSet.StagingPath);
                }));
            var command = new BuildCommand(
                new WorkbookOutputCommand(materializer), new FileSystemPathIdentityResolver(), ownership);
            return command.RunAsync(context, cancellationToken);
        }

        public void AssertNoGenerationAndFilesUnchanged()
        {
            Assert.Empty(Automation.OpenedWorkbooks);
            Assert.Empty(Automation.ImportedSources);
            Assert.Equal(0, Automation.SaveCalls);
            Assert.Equal(0, mirrorCreations);
            Assert.Equal(0, transactions.Creations);
            foreach (var (path, bytes) in originalFiles)
            {
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            Assert.Equal(new[] { context.BinDocumentPath }, Directory.GetFiles(Path.GetDirectoryName(context.BinDocumentPath)!));
        }

        public void AssertSuccessfulBuild(CommandResult result, int importedSourceCount, bool sourcesUnchanged = true)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"Built {context.BinDocumentPath}{Environment.NewLine}Imported {importedSourceCount} source files.{Environment.NewLine}",
                result.StandardOutput);
            Assert.Empty(result.StandardError);
            var stagingWorkbook = Assert.Single(Automation.OpenedWorkbooks);
            Assert.False(File.Exists(stagingWorkbook));
            Assert.Equal(importedSourceCount, Automation.ImportedSources.Count);
            Assert.Equal(1, Automation.VerifyCalls);
            Assert.Equal(1, Automation.SaveCalls);
            Assert.Equal(1, mirrorCreations);
            Assert.False(Directory.Exists(Assert.Single(mirrorPaths)));
            Assert.Equal(1, transactions.Creations);
            Assert.All(Automation.ImportedSources, source =>
            {
                Assert.False(File.Exists(source.SourcePath));
                if (source.BinaryPath is not null) Assert.False(File.Exists(source.BinaryPath));
            });
            Assert.Equal(originalFiles[context.TemplateDocumentPath], File.ReadAllBytes(context.BinDocumentPath));
            Assert.Equal(originalFiles[context.TemplateDocumentPath], File.ReadAllBytes(context.TemplateDocumentPath));
            Assert.Equal(new[] { context.BinDocumentPath }, Directory.GetFiles(Path.GetDirectoryName(context.BinDocumentPath)!));
            if (sourcesUnchanged)
            {
                foreach (var (path, bytes) in originalFiles.Where(file => file.Key != context.BinDocumentPath))
                {
                    Assert.Equal(bytes, File.ReadAllBytes(path));
                }
            }
        }

        public void Dispose() => temp.Dispose();
    }

    private static ReportedDiagnostic ReadDiagnostic(JsonElement diagnostic)
        => ReadDiagnostic(
            diagnostic,
            diagnostic.GetProperty("type").GetString()!,
            diagnostic.GetProperty("owner").GetString()!,
            diagnostic.GetProperty("uri").GetString()!);

    private static ReportedDiagnostic ReadDiagnostic(JsonElement diagnostic, string type, string owner, string uri)
    {
        var range = diagnostic.GetProperty("range");
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        return new(
            type,
            owner,
            uri,
            diagnostic.GetProperty("code").GetString()!,
            diagnostic.GetProperty("message").GetString()!,
            diagnostic.GetProperty("severity").GetString()!,
            start.GetProperty("line").GetInt32(), start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(), end.GetProperty("character").GetInt32());
    }

    private sealed record ReportedDiagnostic(
        string Type, string Owner, string Uri, string Code, string Message, string Severity,
        int StartLine, int StartCharacter, int EndLine, int EndCharacter);

    private sealed class RecordingOutputTransactionFactory : IWorkbookOutputTransactionFactory
    {
        public int Creations { get; private set; }

        public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
        {
            Creations++;
            return WorkbookOutputTransaction.Create(
                new WindowsExactFileSystemObjectOwnershipFactory(), templateWorkbookPath, targetWorkbookPath);
        }
    }
}
