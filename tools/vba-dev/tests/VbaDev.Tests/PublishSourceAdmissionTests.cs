using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class PublishSourceAdmissionTests
{
    [Fact]
    public async Task OrdinaryPublishUsesTheCapturedAcpForAmbiguousBomlessUtf8()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var path = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        var bytes = new UTF8Encoding(false, true).GetBytes("Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n");
        File.WriteAllBytes(path, bytes);
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 1252).RunAsync(context, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        var imported = Assert.Single(automation.ImportedSources);
        Assert.Contains("' caf\u00c3\u00a9", imported.ImportVerification.CodeModuleLines);
        Assert.Equal("windows-1252", imported.ImportVerification.OriginalEncoding);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal("template-workbook", File.ReadAllText(context.PublishDocumentPath));
    }

    [Fact]
    public async Task ManifestExclusionsAreUnreadAndIncludedCommonModulesIgnoreMarkersInManifestOrder()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var hidden = Directory.CreateDirectory(Path.Combine(context.DocumentSourceSetPath, ".hidden")).FullName;
        var excluded = Path.Combine(hidden, "TestOnly.frm");
        var excludedSidecar = Path.ChangeExtension(excluded, ".frx");
        File.WriteAllBytes(excluded, [0xff, 0xfe, 0]);
        File.WriteAllBytes(excludedSidecar, [0, 1, 255]);
        foreach (var name in new[] { "ZRuntime", "ARuntime", "Local" })
        {
            File.WriteAllText(Path.Combine(context.DocumentSourceSetPath, name + ".bas"),
                $"Attribute VB_Name = \"{name}\"\r\n" + (name == "Local" ? "" : "'#ExcludePublish\r\n"));
        }
        context.Document.CommonModules.AddRange([
            new("Missing", "Missing.bas", Requested: true, TestOnly: false),
            new("ZRuntime", "ZRuntime.bas", Requested: true, TestOnly: false, Orphaned: true),
            new("TestOnly", "TestOnly.frm", Requested: false, TestOnly: true, Orphaned: true),
            new("ARuntime", "ARuntime.bas", Requested: false, TestOnly: false)
        ]);
        var reads = new List<string>();
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            Assert.NotEqual(excluded, path);
            Assert.NotEqual(excludedSidecar, path);
            reads.Add(path);
            return File.ReadAllBytes(path);
        });
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 1252, admission).RunAsync(context, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(["ZRuntime.bas", "ARuntime.bas", "Local.bas"], automation.ImportedSources.Select(source => source.FileName));
        Assert.Equal(3, reads.Count);
        Assert.Equal(3, reads.Distinct().Count());
    }

    [Fact]
    public async Task FlatCollisionsFailBeforeTestOnlyFilteringOrAnyContentRead()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var first = Path.Combine(context.DocumentSourceSetPath, "Excluded.bas");
        var second = Path.Combine(Directory.CreateDirectory(Path.Combine(context.DocumentSourceSetPath, "obj")).FullName, "excluded.bas");
        File.WriteAllBytes(first, [0xff]);
        File.WriteAllBytes(second, [0xfe]);
        context.Document.CommonModules.Add(new("Excluded", "Excluded.bas", Requested: true, TestOnly: true));
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 932, readAllBytes: path => { reads++; return File.ReadAllBytes(path); });
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 932, admission).RunAsync(context, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Duplicate VBA source file names", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(first, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(second, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, reads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(Directory.Exists(Path.GetDirectoryName(context.PublishDocumentPath)));
    }

    [Theory]
    [InlineData(".bas")]
    [InlineData(".cls")]
    [InlineData(".frm")]
    public async Task ProvedMarkerExclusionBypassesIdentityKindProjectionAndSidecarRead(string extension)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var path = Path.Combine(context.DocumentSourceSetPath, "Excluded" + extension);
        const string text = "'#ExcludePublish\r\n' \U0001f600\r\nAttribute VB_Name = \"Option\"\r\n";
        File.WriteAllText(path, text, new UTF8Encoding(true, true));
        var sidecar = Path.ChangeExtension(path, ".frx");
        File.WriteAllBytes(sidecar, [0, 1, 255]);
        var reads = new List<string>();
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: sourcePath =>
        {
            reads.Add(sourcePath);
            Assert.NotEqual(sidecar, sourcePath);
            return File.ReadAllBytes(sourcePath);
        });
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 1252, admission).RunAsync(context, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal([path], reads);
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(1, automation.SaveCalls);
        Assert.Equal("template-workbook", File.ReadAllText(context.PublishDocumentPath));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(32, "\r\n", " \t'#excludepublishSuffix", true)]
    [InlineData(33, "\n", "'#ExcludePublish", false)]
    [InlineData(2, "\r", "\u3000'#ExcludePublish", true)]
    [InlineData(2, "\r\n", "\u00a0'#ExcludePublish", false)]
    public async Task MarkerUsesTheEstablishedPhysicalLineWhitespaceAndPrefixRules(
        int markerLine, string newline, string marker, bool excluded)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var lines = new List<string> { "Attribute VB_Name = \"Module1\"" };
        lines.AddRange(Enumerable.Repeat("' filler", markerLine - 2));
        lines.Add(marker);
        lines.Add("Option Explicit");
        File.WriteAllText(Path.Combine(context.DocumentSourceSetPath, "Module1.bas"), string.Join(newline, lines));
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 65001).RunAsync(context, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(excluded ? 0 : 1, automation.ImportedSources.Count);
    }

    [Theory]
    [InlineData(65001, true, 0xc3)]
    [InlineData(932, false, 0x81)]
    public async Task MarkerLookingPrefixCannotHideInvalidBytesAtTheEnd(int activeCodePage, bool bom, byte invalidTail)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var path = Path.Combine(context.DocumentSourceSetPath, "Excluded.bas");
        var prefix = Encoding.ASCII.GetBytes("'#ExcludePublish\r\nAttribute VB_Name = \"Excluded\"\r\n");
        var bytes = (bom ? new byte[] { 0xef, 0xbb, 0xbf } : []).Concat(prefix).Append(invalidTail).ToArray();
        File.WriteAllBytes(path, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(context.PublishDocumentPath)!);
        File.WriteAllText(context.PublishDocumentPath, "previous-output");
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, activeCodePage).RunAsync(context, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(path, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal("previous-output", File.ReadAllText(context.PublishDocumentPath));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task OneImmutablePublishCaptureSuppliesSelectionImportFactsAndSidecarsAfterAuthoringMutation()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var root = context.DocumentSourceSetPath;
        var hidden = Directory.CreateDirectory(Path.Combine(root, ".hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        var formPath = Path.Combine(hidden.FullName, "Dialog.frm");
        var binaryPath = Path.ChangeExtension(formPath, ".frx");
        var modulePath = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "obj")).FullName, "Keep.bas");
        File.WriteAllText(formPath, "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\"\r\nOption Explicit\r\n");
        File.WriteAllBytes(binaryPath, [0, 1, 127, 255]);
        File.WriteAllText(modulePath, "Attribute VB_Name = \"Keep\"\r\nOption Explicit\r\n");
        var excluded = Path.Combine(root, "Excluded.frm");
        File.WriteAllText(excluded, "'#ExcludePublish\r\n' \U0001f600\r\n", new UTF8Encoding(true, true));
        File.WriteAllBytes(Path.ChangeExtension(excluded, ".frx"), [9, 8]);
        File.WriteAllBytes(Path.Combine(root, "Orphan.frx"), [7]);
        var acpCalls = 0;
        var inventoryCalls = 0;
        var reads = new Dictionary<string, byte[]>();
        var admission = new VbaSourceAdmission(
            () => { acpCalls++; return 1252; },
            directory => { inventoryCalls++; return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray(); },
            path =>
            {
                var bytes = File.ReadAllBytes(path);
                Assert.True(reads.TryAdd(path, bytes), $"Repeated read: {path}");
                return bytes;
            });
        var automation = new FakeWorkbookGenerationAutomation();
        AdmittedVbaSourceSet? captured = null;
        string? mirrorPath = null;
        var mirrorFactory = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Publish requested ACP again."),
            mirror =>
            {
                Assert.Empty(automation.OpenedWorkbooks);
                captured = mirror.Admission;
                mirrorPath = mirror.StagingPath;
                Assert.Equal(new byte[] { 0, 1, 127, 255 }, File.ReadAllBytes(Assert.Single(mirror.SourceFiles, source => source.Kind == VbaSourceKind.Form).BinaryPath!));
                foreach (var (path, bytes) in reads)
                {
                    Array.Fill(bytes, (byte)0);
                    File.Delete(path);
                }
                File.WriteAllText(Path.Combine(root, "Late.bas"), "Attribute VB_Name = \"Late\"\r\n");
                File.WriteAllBytes(excluded, [0xff]);
            });

        var result = await CreateCommand(automation, 1252, admission, mirrorFactory).RunAsync(context, CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal(1, acpCalls);
        Assert.Equal(1, inventoryCalls);
        Assert.Equal(4, reads.Count);
        Assert.NotNull(captured);
        Assert.Equal(VbaSourceAdmissionIntent.Publish, captured.Intent);
        Assert.Equal(1252, captured.ActiveCodePage);
        Assert.Equal(["Dialog.frm", "Keep.bas"], automation.ImportedSources.Select(source => source.FileName));
        foreach (var source in captured.Sources)
        {
            var imported = Assert.Single(automation.ImportedSources, item => item.FileName == source.FileName);
            Assert.Equal(source.Text, source.Syntax.Text);
            Assert.Equal(Encoding.ASCII.GetBytes(source.Text), source.OriginalBytes.ToArray());
            Assert.Equal(source.Projection.CodeModuleLines, imported.ImportVerification.CodeModuleLines);
            Assert.Same(source.ModuleIdentityAuthority, imported.ModuleIdentityAuthority);
            Assert.Equal(source.OriginalEncoding, imported.ImportVerification.OriginalEncoding);
            Assert.Equal(source.DiagnosticSourcePath, imported.DiagnosticSourcePath);
        }
        Assert.False(Directory.Exists(mirrorPath));
        Assert.Equal("template-workbook", File.ReadAllText(context.PublishDocumentPath));
    }

    [Theory]
    [MemberData(nameof(VbaSourceAdmissionTests.EncodingCases), MemberType = typeof(VbaSourceAdmissionTests))]
    public async Task OrdinaryPublishConformsToTheFixedAcpEncodingCorpus(string id, string caseJson)
    {
        using var document = JsonDocument.Parse(caseJson);
        var item = document.RootElement;
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, item.GetProperty("fileName").GetString()!);
        var bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
        var activeCodePage = item.GetProperty("activeCodePage").GetInt32();
        File.WriteAllBytes(sourcePath, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(context.PublishDocumentPath)!);
        File.WriteAllText(context.PublishDocumentPath, "previous-output");
        var automation = new FakeWorkbookGenerationAutomation();
        var mirrorObserved = false;
        var mirrorFactory = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("ACP must come from Publish admission."),
            mirror =>
            {
                mirrorObserved = true;
                Assert.Empty(automation.OpenedWorkbooks);
                var source = Assert.Single(mirror.Admission!.Sources);
                var expectedText = item.GetProperty("expectedText").GetString();
                Assert.Equal(VbaSourceAdmissionIntent.Publish, mirror.Admission.Intent);
                Assert.Equal(expectedText, source.Text);
                Assert.Equal(expectedText, source.Syntax.Text);
                Assert.Equal(item.GetProperty("expectedEncoding").GetString(), source.OriginalEncoding);
                Assert.Equal(bytes, source.OriginalBytes.ToArray());
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding = Encoding.GetEncoding(activeCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var stagedBytes = File.ReadAllBytes(Assert.Single(mirror.SourceFiles).SourcePath);
                Assert.Equal(expectedText, encoding.GetString(stagedBytes));
                Assert.Equal(encoding.GetBytes(expectedText!), stagedBytes);
            });

        var result = await CreateCommand(automation, activeCodePage, mirrorFactory: mirrorFactory).RunAsync(context, CancellationToken.None);

        var shouldFail = (item.TryGetProperty("expectedFailure", out var failure) && failure.GetBoolean())
            || (item.TryGetProperty("expectedProjectionFailure", out var projectionFailure) && projectionFailure.GetBoolean());
        Assert.True(result.ExitCode == (shouldFail ? 1 : 0), $"{id}: {result.StandardError}");
        Assert.Equal(!shouldFail, mirrorObserved);
        if (shouldFail)
        {
            Assert.Contains(sourcePath, result.StandardError, StringComparison.Ordinal);
            Assert.Empty(automation.OpenedWorkbooks);
            Assert.Empty(automation.ImportedSources);
        }
        else
        {
            Assert.Equal(item.GetProperty("expectedEncoding").GetString(), Assert.Single(automation.ImportedSources).ImportVerification.OriginalEncoding);
        }
        Assert.Equal(shouldFail ? "previous-output" : "template-workbook", File.ReadAllText(context.PublishDocumentPath));
        Assert.Equal([context.PublishDocumentPath], Directory.GetFiles(Path.GetDirectoryName(context.PublishDocumentPath)!));
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }

    [Theory]
    [InlineData("inventory", 0)]
    [InlineData("source", 1)]
    [InlineData("sidecar", 2)]
    public async Task PublishAdmissionCancellationStopsAtBoundedCaptureStages(string stage, int expectedReads)
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(temp.Path);
        var form = Path.Combine(context.DocumentSourceSetPath, "A_Form.frm");
        File.WriteAllText(form, "VERSION 5.00\r\nBegin VB.Form A_Form\r\nEnd\r\nAttribute VB_Name = \"A_Form\"\r\n");
        File.WriteAllBytes(Path.ChangeExtension(form, ".frx"), [0, 1, 255]);
        File.WriteAllText(Path.Combine(context.DocumentSourceSetPath, "ZLast.bas"), "Attribute VB_Name = \"ZLast\"\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(context.PublishDocumentPath)!);
        File.WriteAllText(context.PublishDocumentPath, "previous-output");
        var reads = new List<string>();
        var admission = new VbaSourceAdmission(
            () => 65001,
            directory =>
            {
                var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
                if (stage == "inventory") cancellation.Cancel();
                return paths;
            },
            path =>
            {
                reads.Add(path);
                var bytes = File.ReadAllBytes(path);
                if (stage == "source" || (stage == "sidecar" && Path.GetExtension(path) == ".frx")) cancellation.Cancel();
                return bytes;
            });
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 65001, admission).RunAsync(context, cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(expectedReads, reads.Count);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal("previous-output", File.ReadAllText(context.PublishDocumentPath));
        Assert.Equal([context.PublishDocumentPath], Directory.GetFiles(Path.GetDirectoryName(context.PublishDocumentPath)!));
    }

    [Fact]
    public void DoctorPublishPreflightRetainsUtf8FirstDecodingAndDoesNotRequireATemplate()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        const string text = "Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n";
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        File.WriteAllBytes(sourcePath, bytes);
        File.Delete(context.TemplateDocumentPath);

        var selected = new WorkbookSourcePlanner(() => 1252).ResolvePublishSourceFilesForPreflight(context);

        Assert.Equal(text, Assert.Single(selected).ExpectedUnicodeText);
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        Assert.False(File.Exists(context.TemplateDocumentPath));
    }

    private static ResolvedProjectContext CreateContext(string root)
    {
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "sources")).FullName;
        var templatePath = Path.Combine(root, "Template.xlsm");
        File.WriteAllText(templatePath, "template-workbook");
        return new(
            root, Path.Combine(root, ProjectManifest.ManifestFileName), manifest,
            "Book1", manifest.Documents["Book1"], sourceDirectory, templatePath,
            Path.Combine(root, "bin", "Book1.xlsm"), Path.Combine(root, "publish", "Book1.xlsm"), null);
    }

    private static PublishCommand CreateCommand(
        FakeWorkbookGenerationAutomation automation,
        int activeCodePage,
        VbaSourceAdmission? admission = null,
        VbeImportSourceSetFactory? mirrorFactory = null)
        => new(new WorkbookOutputCommand(
            new WorkbookMaterializer(
                admission is null ? new WorkbookSourcePlanner(() => activeCodePage) : new WorkbookSourcePlanner(admission),
                automation,
                new WorkbookReferenceNormalizer(new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
                new WorkbookOutputTransactionFactory(),
                mirrorFactory ?? new VbeImportSourceSetFactory(() => activeCodePage))));
}
