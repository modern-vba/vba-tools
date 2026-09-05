using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class DoctorSourceAdmissionTests
{
    [Fact]
    public async Task DoctorUsesOneAcpAndCapturedSourceForEveryDocumentAndProfile()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "First", root, null);
        manifest.Documents.Add("Second", ProjectDocument.CreateExcel("Second"));
        var bytes = Encoding.UTF8.GetBytes("Attribute VB_Name = \"Module1\"\r\n' caf\u00e9\r\n");
        foreach (var document in manifest.Documents.Values)
        {
            var sourceDirectory = Path.Combine(root, document.SourcePath);
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, "Module1.bas"), bytes);
            File.WriteAllText(Path.Combine(root, document.TemplatePath), "unchanged template");
        }
        new JsonProjectManifestStore().Save(root, manifest);
        var codePageReads = 0;
        var inventories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var admission = new VbaSourceAdmission(
            () => ++codePageReads == 1 ? 1252 : 932,
            path =>
            {
                inventories[path] = inventories.GetValueOrDefault(path) + 1;
                return Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            },
            path =>
            {
                reads[path] = reads.GetValueOrDefault(path) + 1;
                return File.ReadAllBytes(path);
            });
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var expectedText = Encoding.GetEncoding(1252).GetString(bytes);
        var observedText = new List<string>();
        var automation = new FakeWorkbookGenerationAutomation { ThrowOnImport = true, ThrowOnSave = true };
        var mirrors = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Doctor mirrors must use the admitted ACP."),
            sourceSet =>
            {
                Assert.Equal(1252, sourceSet.ActiveCodePage);
                observedText.Add(File.ReadAllText(Assert.Single(sourceSet.SourceFiles).SourcePath,
                    Encoding.GetEncoding(1252)));
            });
        var command = CreateDoctor(root, admission, automation, mirrors);

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var profiles = output.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => check.GetProperty("id").GetString()!.StartsWith(
                "project.workbookMaterialization/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, profiles.Length);
        Assert.All(profiles, check => Assert.Equal("pass", check.GetProperty("status").GetString()));
        Assert.Equal(4, observedText.Count);
        Assert.All(observedText, text => Assert.Equal(expectedText, text));
        Assert.Equal(1, codePageReads);
        Assert.Equal(2, inventories.Count);
        Assert.All(inventories.Values, count => Assert.Equal(1, count));
        Assert.Equal(2, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
        foreach (var sourcePath in reads.Keys)
        {
            Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        }
    }

    [Fact]
    public async Task DoctorKeepsTestOnlyDecodeFailureOutOfPublishInspection()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].CommonModules.Add(new InstalledCommonModule(
            "TestOnly", "TestOnly.bas", Requested: true, TestOnly: true));
        new JsonProjectManifestStore().Save(root, manifest);
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "Book1.xlsm"), "unchanged template");
        File.WriteAllText(Path.Combine(sourceDirectory, "Runtime.bas"), "Attribute VB_Name = \"Runtime\"\r\n");
        var testOnlyPath = Path.Combine(sourceDirectory, "TestOnly.bas");
        byte[] invalidBytes = [0xef, 0xbb, 0xbf, 0xff];
        File.WriteAllBytes(testOnlyPath, invalidBytes);
        var testOnlyReads = 0;
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            if (path == testOnlyPath) { testOnlyReads++; }
            return File.ReadAllBytes(path);
        });
        var automation = new FakeWorkbookGenerationAutomation { ThrowOnImport = true, ThrowOnSave = true };
        var command = CreateDoctor(root, admission, automation, new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Doctor must not reacquire ACP.")));

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        var checks = output.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        var build = Assert.Single(checks, check => check.GetProperty("id").GetString() ==
            "project.workbookMaterialization/Book1/build");
        var publish = Assert.Single(checks, check => check.GetProperty("id").GetString() ==
            "project.workbookMaterialization/Book1/publish");
        Assert.Equal("fail", build.GetProperty("status").GetString());
        Assert.Contains(testOnlyPath, build.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("pass", publish.GetProperty("status").GetString());
        Assert.Equal(1, testOnlyReads);
        Assert.Equal(invalidBytes, File.ReadAllBytes(testOnlyPath));
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("kind-header")]
    [InlineData("projection")]
    [InlineData("paired-sidecar")]
    public async Task DoctorPublishExclusionDoesNotHideTheBuildFailure(string failureKind)
    {
        using var temp = TempDirectory.Create();
        var (root, sourceDirectory) = CreateSingleDocument(temp);
        var sourcePath = Path.Combine(sourceDirectory, failureKind == "paired-sidecar" ? "Excluded.frm" : "Excluded.bas");
        var (text, failureMessage) = failureKind switch
        {
            "identity" => ("'#ExcludePublish\r\nOption Explicit\r\n", "authoritative ModuleIdentity"),
            "kind-header" => ("VERSION 1.0 CLASS\r\nBEGIN\r\n  MultiUse = -1\r\nEND\r\nAttribute VB_Name = \"Excluded\"\r\n'#ExcludePublish\r\n", "invalid ModuleIdentity"),
            "projection" => ("Attribute VB_Name = \"Excluded\"\r\n'#ExcludePublish\r\n' \U0001f600\r\n", "represented losslessly"),
            "paired-sidecar" => ("VERSION 5.00\r\nBegin VB.Form Excluded\r\nEnd\r\nAttribute VB_Name = \"Excluded\"\r\n'#ExcludePublish\r\n", "paired sidecar read failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        File.WriteAllBytes(sourcePath, bytes);
        var sidecarPath = Path.ChangeExtension(sourcePath, ".frx");
        byte[] sidecarBytes = [0, 1, 255];
        if (failureKind == "paired-sidecar") { File.WriteAllBytes(sidecarPath, sidecarBytes); }
        var sidecarReads = 0;
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            if (path == sidecarPath)
            {
                sidecarReads++;
                throw new IOException("paired sidecar read failed");
            }
            return File.ReadAllBytes(path);
        });
        var automation = new FakeWorkbookGenerationAutomation { ThrowOnImport = true, ThrowOnSave = true };
        var command = CreateDoctor(root, admission, automation,
            new VbeImportSourceSetFactory(() => throw new InvalidOperationException("Unexpected ACP read.")));

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var build = FindProfile(output, "build");
        Assert.Equal("fail", build.GetProperty("status").GetString());
        Assert.Contains(failureMessage, build.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("pass", FindProfile(output, "publish").GetProperty("status").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(failureKind == "paired-sidecar" ? 1 : 0, sidecarReads);
        if (failureKind == "paired-sidecar") { Assert.Equal(sidecarBytes, File.ReadAllBytes(sidecarPath)); }
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
    }

    [Fact]
    public async Task DoctorDoesNotExcludeAMarkerBeforeInvalidTrailingBytes()
    {
        using var temp = TempDirectory.Create();
        var (root, sourceDirectory) = CreateSingleDocument(temp);
        var sourcePath = Path.Combine(sourceDirectory, "Excluded.bas");
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Excluded\"\r\n'#ExcludePublish\r\n' ")).Append((byte)0xff).ToArray();
        File.WriteAllBytes(sourcePath, bytes);
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            reads++;
            return File.ReadAllBytes(path);
        });
        var automation = new FakeWorkbookGenerationAutomation();
        var command = CreateDoctor(root, admission, automation, new VbeImportSourceSetFactory());

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        foreach (var profile in new[] { "build", "publish" })
        {
            var check = FindProfile(output, profile);
            Assert.Equal("fail", check.GetProperty("status").GetString());
            Assert.Contains("strictly decoded as utf8bom", check.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.Equal(1, reads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public async Task DoctorDoesNotExcludeAnIncludedCommonModuleByItsLocalMarker()
    {
        using var temp = TempDirectory.Create();
        var (root, sourceDirectory) = CreateSingleDocument(temp,
            [new InstalledCommonModule("Common", "Common.bas", Requested: true, TestOnly: false)]);
        var sourcePath = Path.Combine(sourceDirectory, "Common.bas");
        File.WriteAllText(sourcePath,
            "Attribute VB_Name = \"Common\"\r\n'#ExcludePublish\r\n' \U0001f600\r\n", new UTF8Encoding(true));
        var automation = new FakeWorkbookGenerationAutomation();
        var command = CreateDoctor(root, new VbaSourceAdmission(() => 1252), automation,
            new VbeImportSourceSetFactory());

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        foreach (var profile in new[] { "build", "publish" })
        {
            var check = FindProfile(output, profile);
            Assert.Equal("fail", check.GetProperty("status").GetString());
            Assert.Contains("represented losslessly", check.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public async Task DoctorContinuesHealthyDocumentAfterCapturedSourceReadFailure()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "First", root, null);
        manifest.Documents.Add("Second", ProjectDocument.CreateExcel("Second"));
        new JsonProjectManifestStore().Save(root, manifest);
        foreach (var document in manifest.Documents.Values)
        {
            Directory.CreateDirectory(Path.Combine(root, document.SourcePath));
            File.WriteAllText(Path.Combine(root, document.TemplatePath), "unchanged template");
            File.WriteAllText(Path.Combine(root, document.SourcePath, "Module1.bas"),
                "Attribute VB_Name = \"Module1\"\r\n");
        }
        var failedPath = Path.Combine(root, "src", "First", "Module1.bas");
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return path == failedPath ? throw new IOException("captured source read failed") : File.ReadAllBytes(path);
        });
        var automation = new FakeWorkbookGenerationAutomation { ThrowOnImport = true, ThrowOnSave = true };
        var command = CreateDoctor(root, admission, automation, new VbeImportSourceSetFactory());

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        foreach (var profile in new[] { "build", "publish" })
        {
            var failed = FindProfile(output, profile, "First");
            Assert.Equal("fail", failed.GetProperty("status").GetString());
            Assert.Contains("captured source read failed", failed.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal("pass", FindProfile(output, profile, "Second").GetProperty("status").GetString());
        }
        Assert.Equal(2, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.EndsWith("staged-Second.xlsm", Assert.Single(automation.OpenedWorkbooks), StringComparison.Ordinal);
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
    }

    [Fact]
    public async Task DoctorReportsFailedInventoryAsIncompleteInsteadOfEmptySourceSuccess()
    {
        using var temp = TempDirectory.Create();
        var (root, sourceDirectory) = CreateSingleDocument(temp);
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"\r\n");
        var inventories = 0;
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 1252, _ =>
        {
            inventories++;
            throw new IOException("document inventory failed");
        }, path => { reads++; return File.ReadAllBytes(path); });
        var automation = new FakeWorkbookGenerationAutomation();
        var command = CreateDoctor(root, admission, automation, new VbeImportSourceSetFactory());

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        AssertIncompleteSourceInspection(output);
        Assert.Contains("document inventory failed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, inventories);
        Assert.Equal(0, reads);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public async Task DoctorCaptureCancellationDoesNotPublishPartialProfileSuccess()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var (root, sourceDirectory) = CreateSingleDocument(temp);
        var sourcePaths = new[] { "First.bas", "Second.bas" }
            .Select(fileName => Path.Combine(sourceDirectory, fileName)).ToArray();
        foreach (var sourcePath in sourcePaths)
        {
            File.WriteAllText(sourcePath, $"Attribute VB_Name = \"{Path.GetFileNameWithoutExtension(sourcePath)}\"\r\n");
        }
        var originalBytes = sourcePaths.ToDictionary(path => path, File.ReadAllBytes);
        var reads = 0;
        var admission = new VbaSourceAdmission(() => 1252, readAllBytes: path =>
        {
            reads++;
            var bytes = File.ReadAllBytes(path);
            cancellation.Cancel();
            return bytes;
        });
        var automation = new FakeWorkbookGenerationAutomation();
        var mirrors = new VbeImportSourceSetFactory(
            () => throw new InvalidOperationException("Canceled capture must not create an import mirror."));
        var command = CreateDoctor(root, admission, automation, mirrors);

        var result = await command.RunAsync(
            new DoctorCommandRequest(root, root, Format: DoctorOutputFormat.Json), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        AssertIncompleteSourceInspection(output);
        Assert.Equal(1, reads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(Directory.GetFiles(root, "staged-*"));
        foreach (var sourcePath in sourcePaths)
        {
            Assert.Equal(originalBytes[sourcePath], File.ReadAllBytes(sourcePath));
        }
    }

    [Fact]
    public async Task EnvironmentOnlyDoctorDoesNotAcquireSourceAuthority()
    {
        using var temp = TempDirectory.Create();
        var (root, sourceDirectory) = CreateSingleDocument(temp);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "Invalid.bas"), [0xef, 0xbb, 0xbf, 0xff]);
        var codePageReads = 0;
        var inventories = 0;
        var reads = 0;
        var admission = new VbaSourceAdmission(() =>
        {
            codePageReads++;
            throw new InvalidOperationException("Environment diagnostics must not acquire source ACP.");
        }, _ =>
        {
            inventories++;
            throw new IOException("Environment diagnostics must not inventory source files.");
        }, _ =>
        {
            reads++;
            throw new IOException("Environment diagnostics must not read source bytes.");
        });
        var automation = new FakeWorkbookGenerationAutomation();
        var command = CreateDoctor(root, admission, automation, new VbeImportSourceSetFactory());

        var result = await command.RunAsync(new DoctorCommandRequest(
            ProjectRoot: null,
            StartDirectory: root,
            Scope: DoctorScope.Environment,
            Format: DoctorOutputFormat.Json), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("environment", output.RootElement.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, output.RootElement.GetProperty("project").ValueKind);
        Assert.All(output.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal("pass", check.GetProperty("status").GetString()));
        Assert.Equal(0, codePageReads);
        Assert.Equal(0, inventories);
        Assert.Equal(0, reads);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    private static (string Root, string SourceDirectory) CreateSingleDocument(
        TempDirectory temp,
        IReadOnlyList<InstalledCommonModule>? commonModules = null)
    {
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].CommonModules.AddRange(commonModules ?? []);
        new JsonProjectManifestStore().Save(root, manifest);
        var sourceDirectory = Path.Combine(root, "src", "Book1");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "Book1.xlsm"), "unchanged template");
        return (root, sourceDirectory);
    }

    private static JsonElement FindProfile(JsonDocument output, string profile, string documentName = "Book1")
        => Assert.Single(output.RootElement.GetProperty("checks").EnumerateArray(), check =>
            check.GetProperty("id").GetString() == $"project.workbookMaterialization/{documentName}/{profile}");

    private static void AssertIncompleteSourceInspection(JsonDocument output)
    {
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("unverified", output.RootElement.GetProperty("status").GetString());
        var checks = output.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Contains(checks, check => check.GetProperty("id").GetString() == "doctor.infrastructure");
        Assert.DoesNotContain(checks, check => check.GetProperty("id").GetString()!.StartsWith(
            "project.workbookMaterialization/", StringComparison.Ordinal));
    }

    private static DoctorCommand CreateDoctor(
        string root,
        VbaSourceAdmission admission,
        FakeWorkbookGenerationAutomation automation,
        VbeImportSourceSetFactory mirrors)
    {
        var materialization = new ExcelProjectMaterializationDiagnosticPort(
            automation,
            templatePath =>
            {
                var staged = Path.Combine(root, "staged-" + Path.GetFileName(templatePath));
                File.Copy(templatePath, staged);
                return staged;
            },
            File.Delete,
            new WorkbookReferenceNormalizer(new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            mirrors,
            new WorkbookMaterializationNamePreflight());
        var pipeline = new DoctorDiagnosticPipeline(
            new ProjectContextResolver(new JsonProjectManifestStore()),
            [new ProjectConfigurationDiagnosticProvider(), new CommonModulesDiagnosticProvider(new CommonModulesManifestReader())],
            [], materialization, new FakeEnvironmentDiagnosticPort(), admission);
        return new DoctorCommand(pipeline, new DoctorReportRenderer());
    }
}
