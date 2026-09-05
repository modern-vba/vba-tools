using VbaDev.Infrastructure.FileSystem;
using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceAdmissionTests
{
    [Fact]
    public async Task SnapshotBuildUsesTheActiveCodePageForAmbiguousBomlessSource()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var snapshotPath = temp.CreateDirectory("snapshot");
        var sourcePath = Path.Combine(snapshotPath, "Module1.bas");
        var originalBytes = new UTF8Encoding(false, true).GetBytes(
            "Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n");
        File.WriteAllBytes(sourcePath, originalBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var command = new BuildCommand(
            CreateOutputCommand(automation, 1252),
            new BuildSourceSnapshotCaptureFactory(
                temp.CreateDirectory("scratch"), new VbaSourceAdmission(() => 1252)),
            new BuildSourceSnapshotOutputSafetyValidator(new FileSystemPathIdentityResolver()));
        var outputPath = Path.Combine(temp.Path, "output", "Book1.xlsm");

        var result = await command.RunSnapshotAsync(context, snapshotPath, outputPath, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var imported = Assert.Single(automation.ImportedSources);
        Assert.Contains("' caf\u00c3\u00a9", imported.ImportVerification.CodeModuleLines);
        Assert.Equal("windows-1252", imported.ImportVerification.OriginalEncoding);
        Assert.Equal(originalBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal("template-workbook", File.ReadAllText(outputPath));
        Assert.False(File.Exists(context.BinDocumentPath));
    }

    [Fact]
    public async Task OrdinaryBuildUsesTheActiveCodePageForAmbiguousBomlessSource()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        const string utf8Text = "Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n";
        var originalBytes = new UTF8Encoding(false, true).GetBytes(utf8Text);
        File.WriteAllBytes(sourcePath, originalBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var command = CreateCommand(automation, 1252);

        var result = await command.RunAsync(context, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var imported = Assert.Single(automation.ImportedSources);
        Assert.Contains("' caf\u00c3\u00a9", imported.ImportVerification.CodeModuleLines);
        Assert.Equal("windows-1252", imported.ImportVerification.OriginalEncoding);
        Assert.Equal(originalBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal("template-workbook", File.ReadAllText(context.BinDocumentPath));
    }

    [Fact]
    public async Task OrdinaryBuildPreservesAnEmptySourceSet()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 65001).RunAsync(context, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(1, automation.SaveCalls);
        Assert.Equal("template-workbook", File.ReadAllText(context.BinDocumentPath));
    }

    [Fact]
    public async Task OrdinaryBuildKeepsItsAdmittedInventoryAfterSourcesAndSidecarsDisappear()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var root = context.DocumentSourceSetPath;
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var modulePath = Path.Combine(nested, "Greeting.bas");
        var formPath = Path.Combine(root, "Dialog.frm");
        var sidecarPath = Path.Combine(root, "Dialog.frx");
        const string moduleText = "Attribute VB_Name = \"Greeting\"\r\nOption Explicit\r\n";
        const string formText = "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\"\r\nOption Explicit\r\n";
        File.WriteAllBytes(modulePath, Encoding.ASCII.GetBytes(moduleText));
        File.WriteAllBytes(formPath, Encoding.ASCII.GetBytes(formText));
        File.WriteAllBytes(sidecarPath, [0, 1, 127, 255]);
        File.WriteAllBytes(Path.Combine(root, "Orphan.frx"), [4, 5]);
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "not a source");
        context.Document.CommonModules.AddRange([
            new("Missing", "Missing.bas", Requested: true, TestOnly: false),
            new("Greeting", "Greeting.bas", Requested: false, TestOnly: true, Orphaned: true)
        ]);
        var acpCalls = 0;
        var inventoryCalls = 0;
        var reads = new Dictionary<string, byte[]>();
        var admission = new VbaSourceAdmission(
            () => { acpCalls++; return 1252; },
            directory =>
            {
                inventoryCalls++;
                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
            },
            path =>
            {
                var bytes = File.ReadAllBytes(path);
                Assert.True(reads.TryAdd(path, bytes), $"Source was read again: {path}");
                return bytes;
            });
        var automation = new FakeWorkbookGenerationAutomation();
        AdmittedVbaSourceSet? captured = null;
        var mirrorFactory = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Mirror requested ACP again."),
            mirror =>
            {
                Assert.Empty(automation.OpenedWorkbooks);
                captured = Assert.IsType<AdmittedVbaSourceSet>(mirror.Admission);
                foreach (var (path, bytes) in reads)
                {
                    Array.Fill(bytes, (byte)0);
                    File.Delete(path);
                }
                File.WriteAllText(Path.Combine(root, "Late.bas"), "Attribute VB_Name = \"Late\"\r\n");
                var form = Assert.Single(mirror.SourceFiles, source => source.Kind == VbaSourceKind.Form);
                Assert.Equal(new byte[] { 0, 1, 127, 255 }, File.ReadAllBytes(form.BinaryPath!));
            });

        var result = await CreateCommand(automation, 1252, new WorkbookSourcePlanner(admission), mirrorFactory)
            .RunAsync(context, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, acpCalls);
        Assert.Equal(1, inventoryCalls);
        Assert.Equal(3, reads.Count);
        Assert.NotNull(captured);
        Assert.Equal(VbaSourceAdmissionIntent.Build, captured.Intent);
        Assert.Equal(1252, captured.ActiveCodePage);
        Assert.Equal(["Greeting.bas", "Dialog.frm"], automation.ImportedSources.Select(source => source.FileName));
        foreach (var source in captured.Sources)
        {
            var imported = Assert.Single(automation.ImportedSources, item => item.FileName == source.FileName);
            Assert.Equal(source.Text, source.Syntax.Text);
            Assert.Equal(Encoding.ASCII.GetBytes(source.Text), source.OriginalBytes.ToArray());
            Assert.Equal(source.Projection.CodeModuleLines, imported.ImportVerification.CodeModuleLines);
            Assert.Same(source.ModuleIdentityAuthority, imported.ModuleIdentityAuthority);
            Assert.Equal(source.SourcePath, imported.DiagnosticSourcePath);
            Assert.Equal(source.OriginalEncoding, imported.ImportVerification.OriginalEncoding);
        }
        Assert.Equal("template-workbook", File.ReadAllText(context.BinDocumentPath));
    }

    [Fact]
    public async Task CancellationDuringAdmissionStopsBeforeReadingTheNextSource()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(temp.Path);
        File.WriteAllText(Path.Combine(context.DocumentSourceSetPath, "First.bas"), "Attribute VB_Name = \"First\"\r\n");
        File.WriteAllText(Path.Combine(context.DocumentSourceSetPath, "Second.bas"), "Attribute VB_Name = \"Second\"\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        File.WriteAllText(context.BinDocumentPath, "previous-output");
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            reads++;
            var bytes = File.ReadAllBytes(path);
            cancellation.Cancel();
            return bytes;
        });
        var automation = new FakeWorkbookGenerationAutomation();

        var result = await CreateCommand(automation, 1252, new WorkbookSourcePlanner(admission))
            .RunAsync(context, cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Equal(1, reads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal("previous-output", File.ReadAllText(context.BinDocumentPath));
        Assert.Equal([context.BinDocumentPath], Directory.GetFiles(Path.GetDirectoryName(context.BinDocumentPath)!));
    }

    [Theory]
    [MemberData(nameof(VbaSourceAdmissionTests.EncodingCases), MemberType = typeof(VbaSourceAdmissionTests))]
    public async Task OrdinaryBuildConformsToTheFixedAcpEncodingCorpus(string id, string caseJson)
    {
        using var document = JsonDocument.Parse(caseJson);
        var item = document.RootElement;
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, item.GetProperty("fileName").GetString()!);
        var bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
        var activeCodePage = item.GetProperty("activeCodePage").GetInt32();
        File.WriteAllBytes(sourcePath, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        File.WriteAllText(context.BinDocumentPath, "previous-output");
        var automation = new FakeWorkbookGenerationAutomation();
        var mirrorObserved = false;
        var mirrorFactory = new VbeImportSourceSetFactory(() => activeCodePage, mirror =>
        {
            mirrorObserved = true;
            Assert.Empty(automation.OpenedWorkbooks);
            var source = Assert.Single(mirror.Admission!.Sources);
            var expectedText = item.GetProperty("expectedText").GetString();
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

        var result = await CreateCommand(automation, activeCodePage, mirrorFactory: mirrorFactory)
            .RunAsync(context, CancellationToken.None);

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
        Assert.Equal(shouldFail ? "previous-output" : "template-workbook", File.ReadAllText(context.BinDocumentPath));
        Assert.Equal([context.BinDocumentPath], Directory.GetFiles(Path.GetDirectoryName(context.BinDocumentPath)!));
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public async Task SnapshotTestUsesTheActiveCodePageForAmbiguousBomlessSource()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        const string text = "Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n";
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        File.WriteAllBytes(sourcePath, bytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var mirrorFactory = new VbeImportSourceSetFactory(() => 1252);
        var outputCommand = CreateOutputCommand(automation, 1252, mirrorFactory: mirrorFactory);
        var build = new BuildCommand(outputCommand, new FileSystemPathIdentityResolver());
        var runner = new FakeWorkbookTestRunner();
        var test = new TestCommand(build, runner, new TestResultOutputFormatter(), new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), temp.CreateDirectory("scratch"),
                new SnapshotTestWorkspaceFileSystem(), 3, TimeSpan.Zero,
                sourceCaptureFactory: new SnapshotSourceCaptureFactory(new VbaSourceAdmission(() => 1252))));

        var result = await test.RunAsync(context,
            new TestCommandRequest("text", true, new(), TimeSpan.FromMinutes(1), context.DocumentSourceSetPath), CancellationToken.None);

        Assert.True(result.ExitCode == 0, result.StandardError);
        var imported = Assert.Single(automation.ImportedSources);
        Assert.Contains("' caf\u00c3\u00a9", imported.ImportVerification.CodeModuleLines);
        Assert.Equal("windows-1252", imported.ImportVerification.OriginalEncoding);
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        Assert.Single(runner.Workbooks);
        Assert.NotEqual(context.BinDocumentPath, runner.Workbooks[0]);
    }

    public static IEnumerable<object[]> SnapshotEncodingCases()
    {
        foreach (var item in VbaSourceAdmissionTests.EncodingCases())
        {
            yield return ["build", item[0], item[1]];
            yield return ["test", item[0], item[1]];
        }
    }

    [Theory]
    [MemberData(nameof(SnapshotEncodingCases))]
    public async Task SnapshotCommandsConformToTheFixedAcpEncodingCorpus(string command, string id, string caseJson)
    {
        using var document = JsonDocument.Parse(caseJson);
        var item = document.RootElement;
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var snapshotPath = temp.CreateDirectory("snapshot");
        var sourcePath = Path.Combine(snapshotPath, item.GetProperty("fileName").GetString()!);
        var bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
        var activeCodePage = item.GetProperty("activeCodePage").GetInt32();
        File.WriteAllBytes(sourcePath, bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        File.WriteAllText(context.BinDocumentPath, "persistent-bin");
        var outputPath = Path.Combine(temp.Path, "snapshot-output.xlsm");
        File.WriteAllText(outputPath, "previous-output");
        var acpCalls = 0;
        var readCalls = 0;
        var admission = new VbaSourceAdmission(
            () => { acpCalls++; return activeCodePage; },
            readAllBytes: path => { readCalls++; Assert.Equal(sourcePath, path); return File.ReadAllBytes(path); });
        var automation = new FakeWorkbookGenerationAutomation();
        var mirrorObserved = false;
        var mirrorFactory = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Mirror requested ACP again."),
            mirror =>
            {
                mirrorObserved = true;
                Assert.Empty(automation.OpenedWorkbooks);
                var source = Assert.Single(mirror.Admission!.Sources);
                var expectedText = item.GetProperty("expectedText").GetString();
                Assert.Equal(expectedText, source.Text);
                Assert.Equal(expectedText, source.Syntax.Text);
                Assert.Equal(item.GetProperty("expectedEncoding").GetString(), source.OriginalEncoding);
                Assert.Equal(bytes, source.OriginalBytes.ToArray());
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding = Encoding.GetEncoding(activeCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                Assert.Equal(encoding.GetBytes(expectedText!), File.ReadAllBytes(Assert.Single(mirror.SourceFiles).SourcePath));
            });
        var scratchRoot = temp.CreateDirectory("scratch");
        var build = new BuildCommand(CreateOutputCommand(automation, activeCodePage, mirrorFactory: mirrorFactory),
            new BuildSourceSnapshotCaptureFactory(scratchRoot, admission),
            new BuildSourceSnapshotOutputSafetyValidator(new FileSystemPathIdentityResolver()));
        var runner = new FakeWorkbookTestRunner();
        var test = new TestCommand(build, runner, new TestResultOutputFormatter(), new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot,
                new SnapshotTestWorkspaceFileSystem(), 3, TimeSpan.Zero,
                sourceCaptureFactory: new SnapshotSourceCaptureFactory(admission)));

        var result = command == "build"
            ? await build.RunSnapshotAsync(context, snapshotPath, outputPath, CancellationToken.None)
            : await test.RunAsync(context,
                new TestCommandRequest("ndjson", true, new(), TimeSpan.FromMinutes(1), snapshotPath), CancellationToken.None);

        var shouldFail = (item.TryGetProperty("expectedFailure", out var failure) && failure.GetBoolean())
            || (item.TryGetProperty("expectedProjectionFailure", out var projectionFailure) && projectionFailure.GetBoolean());
        Assert.True(result.ExitCode == (shouldFail ? 1 : 0), $"{command}/{id}: {result.StandardError}");
        Assert.Equal(1, acpCalls);
        Assert.Equal(1, readCalls);
        Assert.Equal(!shouldFail, mirrorObserved);
        if (shouldFail)
        {
            Assert.Contains(sourcePath, result.StandardError, StringComparison.Ordinal);
            Assert.Empty(automation.OpenedWorkbooks);
            Assert.Empty(runner.Workbooks);
            Assert.Empty(result.StandardOutput);
        }
        else
        {
            Assert.Equal(item.GetProperty("expectedEncoding").GetString(), Assert.Single(automation.ImportedSources).ImportVerification.OriginalEncoding);
            Assert.Equal(command == "test" ? 1 : 0, runner.Workbooks.Count);
        }
        Assert.Equal(command == "build" && !shouldFail ? "template-workbook" : "previous-output", File.ReadAllText(outputPath));
        Assert.Equal("persistent-bin", File.ReadAllText(context.BinDocumentPath));
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.Empty(Directory.EnumerateFiles(context.DocumentSourceSetPath));
    }

    [Fact]
    public async Task OrdinaryTestBuildFirstAdmissionFailureNeverStartsTheTestRunner()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Module1.bas");
        byte[] invalidUtf8 = [0xef, 0xbb, 0xbf, 0xc3];
        File.WriteAllBytes(sourcePath, invalidUtf8);
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        File.WriteAllText(context.BinDocumentPath, "previous-output");
        var acpCalls = 0;
        var admission = new VbaSourceAdmission(() => { acpCalls++; return 1252; });
        var automation = new FakeWorkbookGenerationAutomation();
        var runner = new FakeWorkbookTestRunner();
        var test = new TestCommand(
            CreateCommand(automation, 1252, new WorkbookSourcePlanner(admission)),
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new FileSystemPathIdentityResolver());

        var result = await test.RunAsync(context, new TestCommandRequest("ndjson", true, new()), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, acpCalls);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(sourcePath, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(runner.Workbooks);
        Assert.Equal("previous-output", File.ReadAllText(context.BinDocumentPath));
        Assert.Equal(invalidUtf8, File.ReadAllBytes(sourcePath));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task OrdinaryTestKeepsAdmittedLocationsAfterSourceFilesChangeAfterMaterialization(
        string mutation)
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "Test_Module.bas");
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Test_Module\"\r\n' admitted before materialization\r\nPublic Sub Test_Passes()\r\nEnd Sub\r\n",
            new UTF8Encoding(false));
        var activeCodePageCalls = 0;
        var sourceReadCalls = 0;
        var admission = new VbaSourceAdmission(
            () =>
            {
                activeCodePageCalls++;
                return 65001;
            },
            readAllBytes: path =>
            {
                sourceReadCalls++;
                return File.ReadAllBytes(path);
            });
        var automation = new FakeWorkbookGenerationAutomation();
        var mutationCalls = 0;
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""))
        {
            OnRun = () =>
            {
                mutationCalls++;
                const string replacement =
                    "Attribute VB_Name = \"Test_Module\"\r\nPublic Sub Test_Passes()\r\nEnd Sub\r\n";
                switch (mutation)
                {
                    case "edit":
                        File.WriteAllText(sourcePath, replacement, new UTF8Encoding(false));
                        break;
                    case "replace":
                        var replacementPath = Path.Combine(
                            Path.GetDirectoryName(sourcePath)!,
                            "replacement.bas");
                        File.WriteAllText(replacementPath, replacement, new UTF8Encoding(false));
                        File.Move(replacementPath, sourcePath, overwrite: true);
                        break;
                    case "delete":
                        File.Delete(sourcePath);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported mutation: {mutation}");
                }
            }
        };
        var test = new TestCommand(
            CreateCommand(
                automation,
                65001,
                new WorkbookSourcePlanner(admission)),
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new FileSystemPathIdentityResolver());

        var result = await test.RunAsync(
            context,
            new TestCommandRequest("ndjson", true, new()),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var completed = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(completed);
        var location = document.RootElement.GetProperty("location");
        Assert.Equal(new Uri(sourcePath).AbsoluteUri, location.GetProperty("uri").GetString());
        Assert.Equal(2, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, mutationCalls);
        Assert.Equal(1, activeCodePageCalls);
        Assert.Equal(1, sourceReadCalls);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task SnapshotTestKeepsAdmittedLocationsAfterSourceFilesDisappear()
    {
        using var temp = TempDirectory.Create();
        var context = CreateContext(temp.Path);
        var snapshotPath = temp.CreateDirectory("snapshot");
        var sourcePath = Path.Combine(Directory.CreateDirectory(Path.Combine(snapshotPath, "nested")).FullName, "Test_Module.bas");
        var sourceBytes = new UTF8Encoding(false, true).GetBytes(
            "Attribute VB_Name = \"Test_Module\"\n' caf\u00e9\n' frozen\nPublic Sub Test_Passes()\nEnd Sub\n");
        File.WriteAllBytes(sourcePath, sourceBytes);
        var scratchRoot = temp.CreateDirectory("scratch");
        var acpCalls = 0;
        var readCalls = 0;
        var admission = new VbaSourceAdmission(
            () => { acpCalls++; return 1252; },
            readAllBytes: path => { readCalls++; return File.ReadAllBytes(path); });
        var automation = new FakeWorkbookGenerationAutomation();
        var runner = new FakeWorkbookTestRunner(
            new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""))
        {
            OnRun = () => File.Delete(sourcePath)
        };
        var test = new TestCommand(
            CreateCommand(automation, 1252, mirrorFactory: new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Mirror requested ACP again."))),
            runner,
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(new FileSystemPathIdentityResolver(), scratchRoot,
                new SnapshotTestWorkspaceFileSystem(), 3, TimeSpan.Zero,
                sourceCaptureFactory: new SnapshotSourceCaptureFactory(admission)));

        var result = await test.RunAsync(context,
            new TestCommandRequest("ndjson", true, new(), TimeSpan.FromMinutes(1), snapshotPath), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var completed = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(completed);
        Assert.True(document.RootElement.TryGetProperty("location", out var location), completed);
        Assert.Equal(new Uri(Path.Combine(context.DocumentSourceSetPath, "nested", "Test_Module.bas")).AbsoluteUri,
            location.GetProperty("uri").GetString());
        Assert.Equal(3, location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(11, location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(1, acpCalls);
        Assert.Equal(1, readCalls);
        Assert.Contains("' caf\u00c3\u00a9", Assert.Single(automation.ImportedSources).ImportVerification.CodeModuleLines);
        Assert.False(File.Exists(sourcePath));
        Assert.False(File.Exists(context.BinDocumentPath));
        Assert.Empty(Directory.EnumerateFiles(context.DocumentSourceSetPath));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
        Assert.DoesNotContain(snapshotPath, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.StandardError);
    }

    private static ResolvedProjectContext CreateContext(string root)
    {
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "sources")).FullName;
        var templatePath = Path.Combine(root, "Template.xlsm");
        File.WriteAllText(templatePath, "template-workbook");
        return new(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            manifest.Documents["Book1"],
            sourceDirectory,
            templatePath,
            Path.Combine(root, "bin", "Book1.xlsm"),
            Path.Combine(root, "publish", "Book1.xlsm"),
            null);
    }

    private static BuildCommand CreateCommand(
        FakeWorkbookGenerationAutomation automation,
        int activeCodePage,
        WorkbookSourcePlanner? planner = null,
        VbeImportSourceSetFactory? mirrorFactory = null)
        => new(CreateOutputCommand(automation, activeCodePage, planner, mirrorFactory), new FileSystemPathIdentityResolver());

    private static WorkbookOutputCommand CreateOutputCommand(
        FakeWorkbookGenerationAutomation automation,
        int activeCodePage,
        WorkbookSourcePlanner? planner = null,
        VbeImportSourceSetFactory? mirrorFactory = null)
        => new(
            new WorkbookMaterializer(
                planner ?? new WorkbookSourcePlanner(() => activeCodePage),
                automation,
                new WorkbookReferenceNormalizer(new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
                new WorkbookOutputTransactionFactory(),
                mirrorFactory ?? new VbeImportSourceSetFactory(() => activeCodePage)));
}
