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
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceDiagnosticsTests
{
    [Theory]
    [InlineData("unexpected")]
    [InlineData("unexpected-after-cancel")]
    [InlineData("untrusted-cancel")]
    public async Task UnexpectedRequiredInputFailureStillReturnsIncompleteAnalysis(string kind)
    {
        using var fixture = new BuildFixture();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        Exception cause = kind == "untrusted-cancel"
            ? new OperationCanceledException("Required metadata acquisition cancelled without a matching request.")
            : new NullReferenceException("Unexpected required metadata processing failure.");
        var provider = new SemanticInputProvider((_, _, _, _) =>
        {
            if (kind == "unexpected-after-cancel") cancellation.Cancel();
            return Task.FromException<VbaProjectSemanticInputs>(cause);
        });

        var result = await fixture.RunAsync(cancellationToken: cancellation.Token, semanticInputProvider: provider);

        Assert.Equal(1, result.ExitCode);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(cause.Message, Assert.Single(report.GetProperty("failures").EnumerateArray())
            .GetProperty("message").GetString());
        Assert.Equal(1, provider.Calls);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildCannotBypassRequiredInputAcquisitionWhenNoProviderIsConfigured()
    {
        using var fixture = new BuildFixture();
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));

        var result = await fixture.RunAsync(provideEmptyInputs: false);

        Assert.Equal(1, result.ExitCode);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Contains("provider", Assert.Single(report.GetProperty("failures").EnumerateArray())
            .GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildAddsTheAlreadyAcceptedReferenceWithoutResolvingItAgain()
    {
        using var fixture = new BuildFixture([new("Example Library")]);
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        const string guid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        fixture.Automation.AdoptedReferenceNamespaces.Add("Example Library", "Example");
        var provider = new SemanticInputProvider((context, template, sources, token) =>
            Task.FromResult(VbaProjectSemanticInputs.Capture(
                VbaReferenceSelection.Capture(["Example Library"], null),
                VbaProjectReferenceCatalogSet.Empty.WithCatalog(new("Example Library", ["Example"], [])
                    { ReferencedVbaProjectName = "Example" }),
                identities: new Dictionary<string, VbaProjectReferenceCatalogIdentity>
                {
                    ["Example Library"] = new("Example Library", guid, 4, 2, 0, "C:/accepted/Example.dll")
                },
                projectNamespaces: VbaProjectNamespaceIdentity.Capture("VbaProject", [new("Example Library", "Example")]))));

        var result = await fixture.RunAsync(semanticInputProvider: provider);

        fixture.AssertSuccessfulBuild(result, importedSourceCount: 1);
        var reference = Assert.Single(fixture.Automation.References);
        Assert.Equal(guid, reference.Guid);
        Assert.Equal(4, reference.Major);
        Assert.Equal(2, reference.Minor);
        Assert.Equal("Example", reference.NamespaceName);
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryBuildGeneratesFromTheTemplateCapturedBeforeMetadataAcquisition(bool removeTemplate)
    {
        using var fixture = new BuildFixture();
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\nEnd Sub\n", new UTF8Encoding(false));
        var templatePath = fixture.SourcePath("Book1.xlsm");
        var provider = new SemanticInputProvider((context, template, sources, token) =>
        {
            if (removeTemplate) File.Delete(templatePath);
            else File.WriteAllText(templatePath, "a new authoring package after acquisition started", new UTF8Encoding(false));
            return Task.FromResult(VbaProjectSemanticInputs.Empty);
        });

        var result = await fixture.RunAsync(semanticInputProvider: provider);

        fixture.AssertSuccessfulBuild(result, importedSourceCount: 1, templateUnchanged: false);
        Assert.Equal(1, provider.Calls);
        if (removeTemplate) Assert.False(File.Exists(templatePath));
        else Assert.Equal("a new authoring package after acquisition started", File.ReadAllText(templatePath));
    }

    [Theory]
    [InlineData("input", false)]
    [InlineData("com", false)]
    [InlineData("cancel", true)]
    [InlineData("process-release", true)]
    [InlineData("dispatcher", true)]
    [InlineData("released-cleanup", true)]
    public async Task AcquisitionFailureReportsCollectedFindingsWithoutLosingLifecycleEvidence(string kind, bool cancelled)
    {
        using var fixture = new BuildFixture();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run(ByVal name As String, ByVal name As Long)\nEnd Sub\n",
            new UTF8Encoding(false));
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen);
        Exception cause = kind switch
        {
            "input" => new IOException("Required TypeLib metadata is unavailable."),
            "com" => new System.Runtime.InteropServices.COMException("Required host Event metadata could not be acquired."),
            "cancel" => new WorkbookAutomationCanceledException(stage, cancellation.Token),
            "process-release" or "dispatcher" => new WorkbookAutomationCleanupException("Metadata discovery release detail."),
            _ => new WorkbookAutomationReleasedProcessCleanupException("Metadata discovery scratch cleanup detail.")
        };
        if (cause is IWorkbookAutomationLifecycleFailure lifecycle)
        {
            lifecycle.LifecycleEvidence = new(stage, kind != "process-release", kind != "dispatcher", cancelled);
        }
        if (cancelled && kind != "cancel")
        {
            cause = new AggregateException(new WorkbookAutomationCanceledException(stage, cancellation.Token), cause);
        }
        var provider = new SemanticInputProvider((context, template, sources, token) =>
        {
            if (cancelled) cancellation.Cancel();
            return Task.FromException<VbaProjectSemanticInputs>(cause);
        });

        var result = await fixture.RunAsync(cancellationToken: cancellation.Token, semanticInputProvider: provider);

        Assert.Equal(kind == "cancel" ? 130 : 1, result.ExitCode);
        Assert.Equal(kind == "process-release" ? OwnedProcessReleaseProof.Unproven
            : OwnedProcessReleaseProof.ProvenOrNotStarted, result.OwnedProcessReleaseProof);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Contains(report.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == "validation.duplicateCallableParameterName");
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal("project", failure.GetProperty("scope").GetString());
        Assert.Contains(cause.Message, failure.GetProperty("message").GetString(), StringComparison.Ordinal);
        if (kind == "com") Assert.Contains(CommandErrorMessages.ExcelComAutomationFailed("build", cause), result.StandardError);
        Assert.Equal(1, provider.Calls);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildUsesAcquiredExternalSignaturesBeforeGeneration()
    {
        using var fixture = new BuildFixture([new("Example Library")]);
        const string source = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    AcceptValue 1, 2\nEnd Sub\n";
        File.WriteAllText(fixture.SourcePath("Caller.bas"), source, new UTF8Encoding(false));
        var provider = new SemanticInputProvider((context, template, sources, cancellationToken) =>
        {
            Assert.Equal("Example Library", Assert.Single(context.Document.References).Name);
            Assert.Equal(fixture.SourcePath("Book1.xlsm"), template.SourcePath);
            Assert.Equal(source, Assert.Single(sources).Text);
            var catalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new("Example Library", ["Example"],
            [
                new("Example Library", "AcceptValue", VbaSourceDefinitionKind.Procedure,
                    Signature: new("Sub AcceptValue(ByVal value As Long)",
                        [new("value", TypeReference: new("Long"), IsByRef: false)],
                        CallableKind: VbaCallableKind.Sub, SupportsNamedArguments: true),
                    GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
            ]));
            return Task.FromResult(VbaProjectSemanticInputs.Capture(
                VbaReferenceSelection.Capture(["Example Library"], null), catalogs));
        });

        var result = await fixture.RunAsync(semanticInputProvider: provider);

        Assert.Equal(1, result.ExitCode);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        var diagnostic = Assert.Single(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.GetProperty("code").GetString());
        Assert.Contains("AcceptValue", diagnostic.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, provider.Calls);
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Fact]
    public async Task OrdinaryBuildReportsAllCapturedSyntaxDocumentAndSemanticErrorsBeforeGeneration()
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
                    "error", 2, 21, 2, 25),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "validation.duplicateDeclaration", "Declaration 'name' conflicts with another declaration in this scope.",
                    "error", 1, 21, 1, 25),
                new ReportedDiagnostic("diagnostic", "vba-dev", new Uri(firstSource).AbsoluteUri,
                    "validation.duplicateDeclaration", "Declaration 'name' conflicts with another declaration in this scope.",
                    "error", 1, 43, 1, 47)
            },
            report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublishContinuesAfterSourceReadFailureAndReportsIncompleteAnalysis(bool publish)
    {
        using var fixture = new BuildFixture(publish: publish);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublishKeepsFormSyntaxFindingsAfterSidecarReadFailureAndAnalyzesLaterSources(bool publish)
    {
        using var fixture = new BuildFixture(publish: publish);
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
    public async Task OrdinaryBuildDoesNotInferCallFailureFromIncompleteSourcesButKeepsIndependentFindings()
    {
        using var fixture = new BuildFixture();
        var unavailable = fixture.SourcePath("AUnavailable.bas");
        File.WriteAllText(unavailable,
            "Attribute VB_Name = \"AUnavailable\"\nPublic Sub AcceptValue(ByRef value As Variant)\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(fixture.SourcePath("Caller.bas"),
            "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Dim item As Integer\n    AcceptValue item\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(fixture.SourcePath("Target.bas"),
            "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n",
            new UTF8Encoding(false));
        var duplicates = fixture.SourcePath("Duplicates.bas");
        File.WriteAllText(duplicates,
            "Attribute VB_Name = \"Duplicates\"\nPublic Sub Work()\nEnd Sub\nPublic Sub Work()\nEnd Sub\n",
            new UTF8Encoding(false));

        var result = await fixture.RunAsync(path => path == unavailable
            ? throw new IOException("A possible competing declaration could not be read.")
            : File.ReadAllBytes(path));

        Assert.Equal(1, result.ExitCode);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        var failure = Assert.Single(report.GetProperty("failures").EnumerateArray());
        Assert.Equal(new Uri(unavailable).AbsoluteUri, failure.GetProperty("uri").GetString());
        Assert.Equal("A possible competing declaration could not be read.", failure.GetProperty("message").GetString());
        var diagnostics = report.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.GetProperty("code").GetString() == "validation.incompatibleCallArgumentList");
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("validation.duplicateDeclaration", diagnostic.GetProperty("code").GetString());
            Assert.Equal(new Uri(duplicates).AbsoluteUri, diagnostic.GetProperty("uri").GetString());
        });
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublishContinuesAfterStrictSourceDecodeFailure(bool publish)
    {
        using var fixture = new BuildFixture(publish: publish);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublishKeepsEarlierFindingsAndStopsAfterProjectFatalProcessingFailure(bool publish)
    {
        using var fixture = new BuildFixture(publish: publish);
        var firstSource = fixture.SourcePath("AFirst.bas");
        var failedSource = fixture.SourcePath("ZFailure.bas");
        var unreadSource = fixture.SourcePath("ZZAfter.bas");
        File.WriteAllText(firstSource,
            "Attribute VB_Name = \"AFirst\"\nPublic Sub Run()\n    Example(Arg1:=1, ARG1:=2)\nEnd Sub\n",
            new UTF8Encoding(false));
        File.WriteAllText(failedSource, "Attribute VB_Name = \"ZFailure\"\n", new UTF8Encoding(false));
        File.WriteAllText(unreadSource, "Attribute VB_Name = \"ZZAfter\"\n", new UTF8Encoding(false));
        var reads = new List<string>();
        var provider = new SemanticInputProvider((_, _, _, _) => Task.FromResult(VbaProjectSemanticInputs.Empty));

        var result = await fixture.RunAsync(path =>
        {
            reads.Add(path);
            if (StringComparer.OrdinalIgnoreCase.Equals(path, failedSource))
            {
                throw new NullReferenceException("Unexpected source reader defect.");
            }
            return File.ReadAllBytes(path);
        }, semanticInputProvider: provider);

        Assert.Equal(0, provider.Calls);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAndPublishAnalyzesAndImportsCapturedBytesAfterAuthoringSourcesChangeOrDisappear(bool publish)
    {
        using var fixture = new BuildFixture(publish: publish);
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

    public static IEnumerable<object[]> ProjectSemanticCases()
        => ReadProjectSemanticCases().SelectMany(fixture => new[] { false, true }
            .Select(publish => new object[] { fixture.GetProperty("id").GetString()!, publish }));

    [Theory]
    [MemberData(nameof(ProjectSemanticCases))]
    public async Task BuildAndPublishSemanticDiagnosticsMatchTheNeutralLiteralCorpus(string caseId, bool publish)
    {
        var corpus = ReadProjectSemanticCases().Single(item =>
            item.GetProperty("id").GetString() == caseId);
        using var fixture = new BuildFixture(publish: publish);
        foreach (var source in corpus.GetProperty("sources").EnumerateArray())
        {
            File.WriteAllText(fixture.SourcePath(source.GetProperty("fileName").GetString()!),
                source.GetProperty("source").GetString()!, new UTF8Encoding(false));
        }
        var expected = corpus.GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic => ReadDiagnostic(diagnostic, "diagnostic", "vba-dev",
                new Uri(fixture.SourcePath(diagnostic.GetProperty("fileName").GetString()!)).AbsoluteUri)
                with { Message = diagnostic.GetProperty("cliMessage").GetString()! })
            .ToArray();

        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var names = new List<string>();
        if (corpus.TryGetProperty("referenceCatalogs", out var catalogData))
        {
            foreach (var entry in catalogData.EnumerateArray())
            {
                var catalog = entry.Deserialize<VbaProjectReferenceCatalog>(jsonOptions)!;
                catalogs = catalogs.WithCatalog(catalog);
                names.Add(catalog.ReferenceName);
            }
        }
        var host = corpus.TryGetProperty("hostEvents", out var hostData)
            ? hostData.Deserialize<VbaIntrinsicHostEventCatalog>(jsonOptions) : null;
        var inputs = VbaProjectSemanticInputs.Capture(
            names.Count == 0 ? null : VbaReferenceSelection.Capture(names, null), catalogs, host);
        var result = await fixture.RunAsync(semanticInputProvider:
            new SemanticInputProvider((_, _, _, _) => Task.FromResult(inputs)));

        if (expected.Length == 0)
        {
            fixture.AssertSuccessfulBuild(result, corpus.GetProperty("sources").GetArrayLength());
            return;
        }
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadSourceAnalysisReport(result.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("failures").EnumerateArray());
        Assert.Equal(expected, report.GetProperty("diagnostics").EnumerateArray().Select(ReadDiagnostic));
        var expectedDiagnostics = corpus.GetProperty("diagnostics").EnumerateArray().ToArray();
        var actualDiagnostics = report.GetProperty("diagnostics").EnumerateArray().ToArray();
        for (var index = 0; index < expectedDiagnostics.Length; index++)
        {
            var expectedRelated = expectedDiagnostics[index].GetProperty("relatedInformation").EnumerateArray().ToArray();
            var actualRelated = actualDiagnostics[index].TryGetProperty("relatedInformation", out var related)
                ? related.EnumerateArray().ToArray() : [];
            Assert.Equal(expectedRelated.Length, actualRelated.Length);
            for (var relatedIndex = 0; relatedIndex < expectedRelated.Length; relatedIndex++)
            {
                var expectedItem = expectedRelated[relatedIndex];
                var actualItem = actualRelated[relatedIndex];
                Assert.Equal(expectedItem.GetProperty("message").GetString(), actualItem.GetProperty("message").GetString());
                var location = actualItem.GetProperty("location");
                Assert.Equal(new Uri(fixture.SourcePath(expectedItem.GetProperty("fileName").GetString()!)).AbsoluteUri,
                    location.GetProperty("uri").GetString());
                Assert.True(JsonElement.DeepEquals(expectedItem.GetProperty("range"), location.GetProperty("range")));
            }
        }
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    private static JsonElement[] ReadProjectSemanticCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-semantic-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(fixture => fixture.Clone()).ToArray();
    }

    [Fact]
    public async Task OrdinaryBuildPreservesExpectedFoundAndRelatedDeclarationLocation()
    {
        var corpus = ReadProjectSemanticCases().Single(item =>
            item.GetProperty("id").GetString() == "source-byref-integer-to-long");
        using var fixture = new BuildFixture();
        foreach (var source in corpus.GetProperty("sources").EnumerateArray())
        {
            File.WriteAllText(fixture.SourcePath(source.GetProperty("fileName").GetString()!),
                source.GetProperty("source").GetString()!, new UTF8Encoding(false));
        }

        var result = await fixture.RunAsync();

        Assert.Equal(1, result.ExitCode);
        var report = ReadSourceAnalysisReport(result.StandardError);
        var diagnostic = Assert.Single(report.GetProperty("diagnostics").EnumerateArray());
        var related = Assert.Single(diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal("No available callable signature accepts this argument list.",
            diagnostic.GetProperty("message").GetString());
        Assert.Equal("Candidate signature: Sub AcceptValue(ByRef value As Long). Mismatches: argument 1 for parameter 'value' ByRef type: expected Long, found Integer.",
            related.GetProperty("message").GetString());
        var location = related.GetProperty("location");
        Assert.Equal(new Uri(fixture.SourcePath("Target.bas")).AbsoluteUri,
            location.GetProperty("uri").GetString());
        var range = location.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(22, range.GetProperty("end").GetProperty("character").GetInt32());
        fixture.AssertNoGenerationAndFilesUnchanged();
    }

    public static IEnumerable<object[]> ConformanceCases()
        => ReadConformanceCases().SelectMany(fixture => new[] { false, true }
            .Select(publish => new object[] { fixture.GetProperty("id").GetString()!, publish }));

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public async Task BuildAndPublishDiagnosticsMatchTheNeutralLiteralCorpus(string caseId, bool publish)
    {
        var corpus = ReadConformanceCases().Single(item => item.GetProperty("id").GetString() == caseId);
        using var fixture = new BuildFixture(publish: publish);
        var sourcePath = fixture.SourcePath(corpus.GetProperty("fileName").GetString()!);
        File.WriteAllText(sourcePath, corpus.GetProperty("source").GetString()!, new UTF8Encoding(false));
        var expected = corpus.GetProperty("syntaxDiagnostics").EnumerateArray()
            .Concat(corpus.GetProperty("documentValidationDiagnostics").EnumerateArray())
            .Concat(corpus.GetProperty("projectValidationDiagnostics").EnumerateArray())
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
        Assert.Equal("3.0", report.GetProperty("schemaVersion").GetString());
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
        private readonly bool publish;
        private string OutputPath => publish ? context.PublishDocumentPath : context.BinDocumentPath;

        public BuildFixture(IReadOnlyList<VbaProjectReference>? references = null, bool publish = false)
        {
            this.publish = publish;
            var root = temp.CreateDirectory("Project");
            new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null,
                references: references));
            SourceDirectory = Path.Combine(root, "src", "Book1");
            Directory.CreateDirectory(SourceDirectory);
            File.WriteAllText(SourcePath("Book1.xlsm"), "source-template", new UTF8Encoding(false));
            context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
                new ProjectResolutionRequest(root, null, root));
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            File.WriteAllText(OutputPath, "previous-completed-build", new UTF8Encoding(false));
        }

        private string SourceDirectory { get; }

        public FakeWorkbookGenerationAutomation Automation { get; } = new();

        public string SourcePath(string fileName) => Path.Combine(SourceDirectory, fileName);

        public Task<CommandResult> RunAsync(
            Func<string, byte[]>? readAllBytes = null,
            CancellationToken cancellationToken = default,
            IProjectSemanticInputProvider? semanticInputProvider = null,
            bool provideEmptyInputs = true)
        {
            originalFiles = Directory.GetFiles(SourceDirectory, "*", SearchOption.AllDirectories)
                .Append(OutputPath)
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
                }), semanticInputProvider: semanticInputProvider ?? (provideEmptyInputs ? FakeProjectSemanticInputProvider.Empty : null));
            if (publish) return new PublishCommand(new WorkbookOutputCommand(materializer)).RunAsync(context, cancellationToken);
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
            Assert.Equal(new[] { OutputPath }, Directory.GetFiles(Path.GetDirectoryName(OutputPath)!));
        }

        public void AssertSuccessfulBuild(CommandResult result, int importedSourceCount, bool sourcesUnchanged = true,
            bool templateUnchanged = true)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"{(publish ? "Published" : "Built")} {OutputPath}{Environment.NewLine}Imported {importedSourceCount} source files.{Environment.NewLine}",
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
            Assert.Equal(originalFiles[context.TemplateDocumentPath], File.ReadAllBytes(OutputPath));
            if (templateUnchanged)
                Assert.Equal(originalFiles[context.TemplateDocumentPath], File.ReadAllBytes(context.TemplateDocumentPath));
            Assert.Equal(new[] { OutputPath }, Directory.GetFiles(Path.GetDirectoryName(OutputPath)!));
            if (sourcesUnchanged)
            {
                foreach (var (path, bytes) in originalFiles.Where(file => file.Key != OutputPath
                    && (templateUnchanged || file.Key != context.TemplateDocumentPath)))
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

    private sealed class SemanticInputProvider(
        Func<ResolvedProjectContext, CapturedWorkbookTemplate, IReadOnlyList<VbaSyntaxTree>, CancellationToken,
            Task<VbaProjectSemanticInputs>> acquire) : IProjectSemanticInputProvider
    {
        internal int Calls { get; private set; }
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context,
            CapturedWorkbookTemplate template, IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
        {
            Calls++;
            return acquire(context, template, sources, cancellationToken);
        }
    }

    private sealed class RecordingOutputTransactionFactory : IWorkbookOutputTransactionFactory
    {
        public int Creations { get; private set; }

        public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
        {
            Creations++;
            return WorkbookOutputTransaction.Create(
                new WindowsExactFileSystemObjectOwnershipFactory(), templateWorkbookPath, targetWorkbookPath);
        }

        public IWorkbookOutputTransaction Create(CapturedWorkbookTemplate templateWorkbookPath, string targetWorkbookPath)
        {
            Creations++;
            return WorkbookOutputTransaction.Create(
                new WindowsExactFileSystemObjectOwnershipFactory(), templateWorkbookPath, targetWorkbookPath);
        }
    }
}
