using VbaDev.Infrastructure.FileSystem;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VbaDev.App.Build;
using VbaDev.App.Diagnostics;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;
using Xunit;
using Xunit.Abstractions;

namespace VbaDev.Tests;

[Collection(WindowsExcelIntegrationCollection.Name)]
public sealed class WorkbookGenerationWindowsExcelIntegrationTests
{
    private const string ScriptingGuid = "420b2830-e718-11cf-893d-00a0c9054228";
    private readonly ITestOutputHelper output;

    public WorkbookGenerationWindowsExcelIntegrationTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExplicitlyLaunchedOwnedExcelCanBeBoundAndReleased()
    {
        using var terminationController = new OwnedExcelTerminationController();
        await using var dispatcher = new StaComDispatcher();
        var host = await dispatcher.InvokeAsync(
            () => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                CancellationToken.None),
            CancellationToken.None);
        await dispatcher.InvokeAsync(
            () =>
            {
                try
                {
                    dynamic excel = host.ExcelObject;
                    Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(excel.Version)));
                }
                finally
                {
                    ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                        host,
                        TimeSpan.FromSeconds(5));
                }

                return true;
            },
            CancellationToken.None);
        await terminationController.RequestCleanupAsync(TimeSpan.FromSeconds(5));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task InSessionReferenceProbeRestoresTheOpenWorkbookInventory()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "ReferenceProbe.xlsm");
        CreateEmptyMacroEnabledWorkbook(workbookPath);
        var originalWorkbook = File.ReadAllBytes(workbookPath);
        var initialProcesses = CaptureExcelProcessIds();

        try
        {
            var automation = new ExcelComWorkbookGenerationAutomation();
            var result = await automation.RunAsync(
                workbookPath,
                WorkbookAutomationTimeouts.Default,
                async (session, cancellationToken) =>
                {
                    var baseline = (await session
                            .GetReferencesAsync(cancellationToken))
                        .ToArray();
                    var probeResult = await ((IVbaProjectReferenceProbeSession)session)
                        .TryResolveAsync(
                            "Microsoft Scripting Runtime",
                            new ResolvedVbaProjectReference(
                                "Microsoft Scripting Runtime",
                                ScriptingGuid,
                                1,
                                0),
                            cancellationToken);
                    Assert.Equal(
                        baseline,
                        await session.GetReferencesAsync(cancellationToken));
                    return probeResult;
                },
                CancellationToken.None);

            Assert.Equal(VbaProjectReferenceProbeAttemptOutcome.Accepted, result.Outcome);
            Assert.Equal(ScriptingGuid, result.Reference!.Guid);
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        }

        Assert.Equal(originalWorkbook, File.ReadAllBytes(workbookPath));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task StartupTimeoutPreservesExistingTargetAndCleansOwnedArtifacts()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var originalTarget = Encoding.UTF8.GetBytes("existing-target");
        File.WriteAllBytes(targetPath, originalTarget);
        var pipeline = CreateGenerationPipeline();
        var timeouts = WorkbookAutomationTimeouts.Default with
        {
            ExcelStartup = TimeSpan.FromMilliseconds(1),
            ProcessCleanup = TimeSpan.Zero
        };

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            pipeline.GenerateAsync(
                "OwnedBuildProject",
                templatePath,
                targetPath,
                [],
                [],
                timeouts,
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.ExcelStartup, error.Stage.Kind);
        Assert.Equal(originalTarget, File.ReadAllBytes(targetPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetPath)!,
            ".OwnedBuildProject.*.tmp.xlsm"));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.Equal(
            initialBootstrapFiles.Order(),
            CaptureBootstrapWorkbookPaths().Order());
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task CancellationDuringStartupPreservesExistingTargetAndCleansOwnedArtifacts()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var originalTarget = Encoding.UTF8.GetBytes("existing-target");
        File.WriteAllBytes(targetPath, originalTarget);
        var pipeline = CreateGenerationPipeline();
        using var cancellation = new CancellationTokenSource();

        var generation = pipeline.GenerateAsync(
            "OwnedBuildProject",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default with
            {
                ProcessCleanup = TimeSpan.Zero
            },
            cancellation.Token);
        _ = await WaitForOwnedExcelProcessAsync(
            initialProcesses,
            generation,
            TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(
            () => generation);

        Assert.Contains(
            error.Stage.Kind,
            new[]
            {
                WorkbookAutomationStageKind.ExcelStartup,
                WorkbookAutomationStageKind.WorkbookOpen
            });
        Assert.Equal(originalTarget, File.ReadAllBytes(targetPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetPath)!,
            ".OwnedBuildProject.*.tmp.xlsm"));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
        Assert.Equal(
            initialBootstrapFiles.Order(),
            CaptureBootstrapWorkbookPaths().Order());
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task UnexpectedOwnedProcessLossIsReportedWithoutTouchingOtherExcelProcesses()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var templatePath = Path.Combine(temp.Path, "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        var automation = new ExcelComWorkbookGenerationAutomation();

        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(() =>
            automation.RunAsync(
                templatePath,
                WorkbookAutomationTimeouts.Default with
                {
                    ProcessCleanup = TimeSpan.Zero
                },
                async (session, cancellationToken) =>
                {
                    var ownedProcessId = Assert.Single(
                        CaptureExcelProcessIds().Except(initialProcesses));
                    using var ownedProcess = Process.GetProcessById(ownedProcessId);
                    ownedProcess.Kill(entireProcessTree: false);
                    await ownedProcess.WaitForExitAsync(cancellationToken);
                    await session.VerifyAsync(cancellationToken);
                    return true;
                },
                CancellationToken.None));

        Assert.Equal(WorkbookAutomationStageKind.Verification, error.Stage.Kind);
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task GenerationCompletesInItsOwnExcelProcessWithoutDisturbingPreExistingExcel()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var sourceDirectory = temp.CreateDirectory("src");
        var templatePath = Path.Combine(sourceDirectory, "OwnedBuildProject.xlsm");
        var sourcePath = Path.Combine(sourceDirectory, "Feature.bas");
        var targetPath = Path.Combine(temp.Path, "bin", "OwnedBuildProject.xlsm");
        CreateEmptyMacroEnabledWorkbook(templatePath);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Feature\"\r\nOption Explicit\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        object? preExistingExcel = null;
        try
        {
            preExistingExcel = CreateHiddenExcelApplication();
            var processesWithPreExistingExcel = CaptureExcelProcessIds();
            var preExistingProcessId = Assert.Single(
                processesWithPreExistingExcel.Except(initialProcesses));

            var pipeline = CreateGenerationPipeline();
            await pipeline.GenerateAsync(
                "OwnedBuildProject",
                templatePath,
                targetPath,
                [],
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None);

            Assert.True(File.Exists(targetPath));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(targetPath)!,
                ".OwnedBuildProject.*.tmp.xlsm"));
            await WaitForProcessSetAsync(processesWithPreExistingExcel, TimeSpan.FromSeconds(20));
            Assert.Contains(preExistingProcessId, CaptureExcelProcessIds());

            dynamic excel = preExistingExcel;
            Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(excel.Version)));
        }
        finally
        {
            QuitExcel(preExistingExcel);
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ActiveCodePageImportPreservesAttributesNestedFormStateAndProjectedCodeAfterReopen()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        var nonAsciiText = SelectNonAsciiFixtureText(activeCodePage);
        var formSourcePath = Path.Combine(temp.Path, "Dialog.frm");
        var formSidecarPath = Path.Combine(temp.Path, "Dialog.frx");
        var seedWorkbookPath = Path.Combine(temp.Path, "FormSeed.xlsm");
        CreateEmptyMacroEnabledWorkbook(seedWorkbookPath);
        var seedExcelVersion = ExportNestedUserFormFixture(
            seedWorkbookPath,
            formSourcePath,
            nonAsciiText);
        Assert.True(File.Exists(formSidecarPath));
        Assert.NotEmpty(File.ReadAllBytes(formSidecarPath));
        var formSourceText = DecodeActiveCodePageFile(formSourcePath, activeCodePage);
        Assert.Contains("Dialog.frx", formSourceText, StringComparison.OrdinalIgnoreCase);

        var standardSourcePath = Path.Combine(temp.Path, "UnicodeModule.bas");
        var classSourcePath = Path.Combine(temp.Path, "ContractClass.cls");
        var standardSourceText = string.Join("\r\n", [
            "Attribute VB_Name = \"UnicodeModule\"",
            "Option Explicit",
            $"Public Const NonAsciiValue As String = \"{nonAsciiText}\"",
            string.Empty
        ]);
        var classSourceText = string.Join("\r\n", [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"ContractClass\"",
            "Attribute VB_GlobalNameSpace = False",
            "Attribute VB_Creatable = False",
            "Attribute VB_PredeclaredId = True",
            "Attribute VB_Exposed = False",
            "Option Explicit",
            "Public Function Item() As String",
            $"Attribute Item.VB_Description = \"Default member {nonAsciiText}\"",
            "Attribute Item.VB_UserMemId = 0",
            $"    Item = \"{nonAsciiText}\"",
            "End Function",
            string.Empty
        ]);
        var utf16Be = new UnicodeEncoding(
            bigEndian: true,
            byteOrderMark: true,
            throwOnInvalidBytes: true);
        var utf8Bom = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: true,
            throwOnInvalidBytes: true);
        File.WriteAllBytes(
            standardSourcePath,
            utf16Be.GetPreamble().Concat(utf16Be.GetBytes(standardSourceText)).ToArray());
        File.WriteAllBytes(
            classSourcePath,
            utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(classSourceText)).ToArray());
        var originalStandardBytes = File.ReadAllBytes(standardSourcePath);
        var originalClassBytes = File.ReadAllBytes(classSourcePath);
        var originalFormBytes = File.ReadAllBytes(formSourcePath);
        var originalSidecarBytes = File.ReadAllBytes(formSidecarPath);
        var explicitSourceDirectory = temp.CreateDirectory("ExplicitSources");
        File.WriteAllBytes(Path.Combine(explicitSourceDirectory, "UnicodeModule.bas"), originalStandardBytes);
        File.WriteAllBytes(Path.Combine(explicitSourceDirectory, "ContractClass.cls"), originalClassBytes);
        var utf16Le = new UnicodeEncoding(false, true, true);
        var explicitFormBytes = utf16Le.GetPreamble().Concat(utf16Le.GetBytes(formSourceText)).ToArray();
        File.WriteAllBytes(Path.Combine(explicitSourceDirectory, "Dialog.frm"), explicitFormBytes);
        File.WriteAllBytes(Path.Combine(explicitSourceDirectory, "Dialog.frx"), originalSidecarBytes);
        var explicitTargetPath = Path.Combine(temp.Path, "ExplicitImported.xlsm");
        CreateEmptyMacroEnabledWorkbook(explicitTargetPath);
        var targetWorkbookPath = Path.Combine(temp.Path, "Imported.xlsm");
        CreateEmptyMacroEnabledWorkbook(targetWorkbookPath);
        var productionTemplatePath = Path.Combine(temp.Path, "ProductionTemplate.xlsm");
        var productionTargetPath = Path.Combine(temp.Path, "bin", "ProductionImported.xlsm");
        CreateEmptyMacroEnabledWorkbook(productionTemplatePath);

        using (var importSourceSet = VbeImportSourceSet.Create(
            [
                new VbaSourceFile(standardSourcePath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(classSourcePath, VbaSourceKind.ClassModule, null),
                new VbaSourceFile(formSourcePath, VbaSourceKind.Form, formSidecarPath)
            ],
            activeCodePage))
        {
            var directImportExcelVersion = ImportAndAssertImmediatelyAndAfterReopen(
                targetWorkbookPath,
                importSourceSet.SourceFiles,
                activeCodePage,
                nonAsciiText,
                temp.Path);
            await CreateGenerationPipeline().GenerateAsync(
                "ProductionImported",
                productionTemplatePath,
                productionTargetPath,
                [],
                [
                    new VbaSourceFile(standardSourcePath, VbaSourceKind.StandardModule, null),
                    new VbaSourceFile(classSourcePath, VbaSourceKind.ClassModule, null),
                    new VbaSourceFile(formSourcePath, VbaSourceKind.Form, formSidecarPath)
                ],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None);
            var productionExcelVersion = OpenAndAssertPersistedWorkbook(
                productionTargetPath,
                importSourceSet.SourceFiles,
                activeCodePage,
                nonAsciiText,
                Path.Combine(temp.Path, "ProductionContractClass.cls"));
            Assert.False(string.IsNullOrWhiteSpace(seedExcelVersion));
            Assert.False(string.IsNullOrWhiteSpace(directImportExcelVersion));
            Assert.False(string.IsNullOrWhiteSpace(productionExcelVersion));
            var explicitResult = await new ImportCommand(new ExcelComWorkbookGenerationAutomation()).RunAsync(
                new ImportCommandRequest(explicitSourceDirectory, explicitTargetPath, temp.Path),
                CancellationToken.None);
            Assert.True(explicitResult.ExitCode == 0, explicitResult.StandardError);
            var explicitExcelVersion = OpenAndAssertPersistedWorkbook(
                explicitTargetPath,
                importSourceSet.SourceFiles,
                activeCodePage,
                nonAsciiText,
                Path.Combine(temp.Path, "ExplicitContractClass.cls"));
            Assert.False(string.IsNullOrWhiteSpace(explicitExcelVersion));
            Assert.Equal(originalStandardBytes, File.ReadAllBytes(Path.Combine(explicitSourceDirectory, "UnicodeModule.bas")));
            Assert.Equal(originalClassBytes, File.ReadAllBytes(Path.Combine(explicitSourceDirectory, "ContractClass.cls")));
            Assert.Equal(explicitFormBytes, File.ReadAllBytes(Path.Combine(explicitSourceDirectory, "Dialog.frm")));
            Assert.Equal(originalSidecarBytes, File.ReadAllBytes(Path.Combine(explicitSourceDirectory, "Dialog.frx")));
            Assert.Empty(Directory.GetFiles(temp.Path, ".ExplicitImported.*.tmp.xlsm"));
            output.WriteLine(
                $"VBE import integration: direct Excel {directImportExcelVersion}; production Excel {productionExcelVersion}; " +
                $"explicit import Excel {explicitExcelVersion}; active ACP {activeCodePage}; seed Excel {seedExcelVersion}; " +
                $"source encodings {string.Join(", ", importSourceSet.SourceFiles.Select(source => source.ImportVerification.OriginalEncoding))}.");
        }

        Assert.Equal(originalStandardBytes, File.ReadAllBytes(standardSourcePath));
        Assert.Equal(originalClassBytes, File.ReadAllBytes(classSourcePath));
        Assert.Equal(originalFormBytes, File.ReadAllBytes(formSourcePath));
        Assert.Equal(originalSidecarBytes, File.ReadAllBytes(formSidecarPath));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryBuildPreservesAdmittedAcpAndBomSourcesAndNestedFormStateAfterReopen()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            string? stagingPath = null;
            var command = CreateOrdinaryBuildCommand(sourceSet =>
            {
                Assert.Equal(activeCodePage, sourceSet.ActiveCodePage);
                Assert.NotNull(sourceSet.Admission);
                admittedSources = sourceSet.SourceFiles.ToArray();
                stagingPath = sourceSet.StagingPath;
            });

            var result = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Contains($"Built {fixture.Context.BinDocumentPath}", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
            Assert.NotNull(admittedSources);
            Assert.Equal(
                new[] { "utf8bom", "utf16le", "utf16be", activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}" },
                admittedSources.Select(source => source.ImportVerification.OriginalEncoding));
            var reopenedVersion = await AssertOrdinaryBuildWorkbookAsync(
                fixture, admittedSources, activeCodePage, temp.Path, cancellation.Token);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in fixture.CallerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            output.WriteLine(
                $"Ordinary admitted build: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"reopened Excel {reopenedVersion}; encodings {string.Join(", ", admittedSources.Select(source => source.ImportVerification.OriginalEncoding))}.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryBuildKeepsCapturedSourcesAndSidecarsAfterAuthoringChanges()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            var sourceDirectory = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules");
            var latePath = Path.Combine(sourceDirectory, "LateAdded.bas");
            foreach (var deleteForm in new[] { false, true })
            {
                foreach (var source in fixture.CallerBytes)
                {
                    File.WriteAllBytes(source.Key, source.Value);
                }

                File.Delete(latePath);
                IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
                string? stagingPath = null;
                var command = CreateOrdinaryBuildCommand(sourceSet =>
                {
                    Assert.Equal(activeCodePage, sourceSet.ActiveCodePage);
                    Assert.NotNull(sourceSet.Admission);
                    admittedSources = sourceSet.SourceFiles.ToArray();
                    stagingPath = sourceSet.StagingPath;
                    File.WriteAllBytes(Path.Combine(sourceDirectory, "UnicodeModule.bas"), [0xff]);
                    File.Delete(Path.Combine(sourceDirectory, "ContractClass.cls"));
                    foreach (var fileName in new[] { "Dialog.frm", "Dialog.frx" })
                    {
                        var path = Path.Combine(sourceDirectory, fileName);
                        if (deleteForm)
                        {
                            File.Delete(path);
                        }
                        else
                        {
                            File.WriteAllBytes(path, [0xff]);
                        }
                    }

                    WriteEncodedFixtureSource(
                        latePath,
                        "Attribute VB_Name = \"LateAdded\"\r\nOption Explicit\r\n",
                        StrictEncoding(activeCodePage));
                });

                var result = await command.RunAsync(fixture.Context, cancellation.Token);

                Assert.True(result.ExitCode == 0, result.StandardError);
                Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
                Assert.NotNull(admittedSources);
                Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(Path.Combine(sourceDirectory, "UnicodeModule.bas")));
                Assert.False(File.Exists(Path.Combine(sourceDirectory, "ContractClass.cls")));
                Assert.True(File.Exists(latePath));
                foreach (var fileName in new[] { "Dialog.frm", "Dialog.frx" })
                {
                    var path = Path.Combine(sourceDirectory, fileName);
                    if (deleteForm)
                    {
                        Assert.False(File.Exists(path));
                    }
                    else
                    {
                        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(path));
                    }
                }

                var phase = deleteForm ? "deleted" : "replaced";
                var reopenedVersion = await AssertOrdinaryBuildWorkbookAsync(
                    fixture, admittedSources, activeCodePage, temp.CreateDirectory(phase), cancellation.Token);
                Assert.False(Directory.Exists(stagingPath));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
                Assert.Equal(fixture.CallerBytes[fixture.Context.TemplateDocumentPath], File.ReadAllBytes(fixture.Context.TemplateDocumentPath));
                Assert.Equal(fixture.CallerBytes[fixture.Context.ManifestPath], File.ReadAllBytes(fixture.Context.ManifestPath));
                output.WriteLine(
                    $"Ordinary build capture independence: form/FRX {phase}; actual GetACP {activeCodePage}; " +
                    $"seed Excel {fixture.SeedExcelVersion}; reopened Excel {reopenedVersion}; original four components retained, late addition absent.");
            }
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryBuildAdmissionAndProjectionFailuresPreserveCompletedWorkbookWithoutStartingExcel()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            var automation = new RecordingOwnedWorkbookGenerationAutomation();
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            var command = CreateOrdinaryBuildCommand(
                sourceSet => admittedSources = sourceSet.SourceFiles.ToArray(), automation);
            var baseline = await command.RunAsync(fixture.Context, cancellation.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            Assert.NotNull(admittedSources);
            var completedWorkbook = File.ReadAllBytes(fixture.Context.BinDocumentPath);
            var sourcePath = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "UnicodeModule.bas");
            var failures = new List<(byte[] Bytes, string ExpectedError)>
            {
                ([0xef, 0xbb, 0xbf, 0xc3, 0x28], "utf8bom")
            };
            if (activeCodePage != 65001)
            {
                var utf8Bom = new UTF8Encoding(true, true);
                failures.Add((
                    utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(
                        "Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\n' \U0001f642\r\n")).ToArray(),
                    $"Windows code page {activeCodePage}"));
            }

            foreach (var failure in failures)
            {
                File.WriteAllBytes(sourcePath, failure.Bytes);

                var result = await command.RunAsync(fixture.Context, cancellation.Token);

                Assert.Equal(1, result.ExitCode);
                Assert.Contains(failure.ExpectedError, result.StandardError, StringComparison.Ordinal);
                Assert.Equal(1, automation.StartedRuns);
                Assert.Equal(1, automation.CompletedRuns);
                Assert.Equal(completedWorkbook, File.ReadAllBytes(fixture.Context.BinDocumentPath));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
                foreach (var source in fixture.CallerBytes)
                {
                    Assert.Equal(source.Key == sourcePath ? failure.Bytes : source.Value, File.ReadAllBytes(source.Key));
                }

                Assert.Equal(initialProcesses.Order(), CaptureExcelProcessIds().Order());
            }

            var reopenedVersion = await AssertOrdinaryBuildWorkbookAsync(
                fixture, admittedSources, activeCodePage, temp.Path, cancellation.Token);
            output.WriteLine(
                $"Ordinary build early failure: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"preserved output reopened with Excel {reopenedVersion}; {failures.Count} failures without another Excel invocation." +
                (activeCodePage == 65001 ? " ACP 65001 represents all valid Unicode, so the legacy ACP projection-failure case is inapplicable." : string.Empty));
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryBuildCancellationAfterOwnedSavePreservesPreviousOutputAndCleansStaging()
    {
        using var temp = TempDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, deadline.Token);
            IReadOnlyList<VbeImportSourceFile>? previousSources = null;
            var baseline = await CreateOrdinaryBuildCommand(
                sourceSet => previousSources = sourceSet.SourceFiles.ToArray()).RunAsync(fixture.Context, deadline.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            Assert.NotNull(previousSources);
            var previousWorkbook = File.ReadAllBytes(fixture.Context.BinDocumentPath);
            WriteEncodedFixtureSource(
                Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "UnicodeModule.bas"),
                $"Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\nPublic Const NonAsciiValue As String = \"{fixture.NonAsciiText} updated\"\r\n",
                StrictEncoding(activeCodePage));
            var callerBytes = fixture.CallerBytes.Keys.ToDictionary(path => path, File.ReadAllBytes);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var automation = new RecordingOwnedWorkbookGenerationAutomation(cancellation);
            string? stagingPath = null;
            var command = CreateOrdinaryBuildCommand(sourceSet => stagingPath = sourceSet.StagingPath, automation);

            var result = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.Equal(130, result.ExitCode);
            Assert.Contains("output commit", result.StandardError, StringComparison.Ordinal);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, automation.StartedRuns);
            Assert.Equal(1, automation.CompletedRuns);
            Assert.Equal(previousWorkbook, File.ReadAllBytes(fixture.Context.BinDocumentPath));
            Assert.NotNull(stagingPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in callerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            var reopenedVersion = await AssertOrdinaryBuildWorkbookAsync(
                fixture, previousSources, activeCodePage, temp.Path, deadline.Token);
            output.WriteLine(
                $"Ordinary build cancellation before commit: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"real updated build saved and owned Excel released; previous output reopened with Excel {reopenedVersion}; staging removed.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryBuildCancellationAfterCommitRetainsSuccessfulUpdatedOutput()
    {
        using var temp = TempDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, deadline.Token);
            var baseline = await CreateOrdinaryBuildCommand(_ => { }).RunAsync(fixture.Context, deadline.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            var previousWorkbook = File.ReadAllBytes(fixture.Context.BinDocumentPath);
            WriteEncodedFixtureSource(
                Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "UnicodeModule.bas"),
                $"Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\nPublic Const NonAsciiValue As String = \"{fixture.NonAsciiText} updated\"\r\n",
                StrictEncoding(activeCodePage));
            var callerBytes = fixture.CallerBytes.Keys.ToDictionary(path => path, File.ReadAllBytes);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var automation = new RecordingOwnedWorkbookGenerationAutomation();
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            string? stagingPath = null;
            var command = CreateOrdinaryBuildCommand(
                sourceSet =>
                {
                    admittedSources = sourceSet.SourceFiles.ToArray();
                    stagingPath = sourceSet.StagingPath;
                },
                automation,
                new CancelAfterCommitTransactionFactory(cancellation));

            var result = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Contains($"Built {fixture.Context.BinDocumentPath}", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, automation.StartedRuns);
            Assert.Equal(1, automation.CompletedRuns);
            Assert.False(previousWorkbook.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Context.BinDocumentPath)));
            Assert.NotNull(admittedSources);
            var reopenedVersion = await AssertOrdinaryBuildWorkbookAsync(
                fixture, admittedSources, activeCodePage, temp.Path, deadline.Token);
            Assert.NotNull(stagingPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in callerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            output.WriteLine(
                $"Ordinary build cancellation after commit: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"updated output reopened with Excel {reopenedVersion}; success retained and staging removed.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryPublishSelectionPreservesIncludedModulesAndNestedFormStateAfterReopen()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryPublishFixtureAsync(temp, activeCodePage, cancellation.Token);
            var sourceDirectory = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules");
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            string? stagingPath = null;
            var command = CreateOrdinaryPublishCommand(sourceSet =>
            {
                Assert.Equal(activeCodePage, sourceSet.ActiveCodePage);
                Assert.NotNull(sourceSet.Admission);
                Assert.Equal(VbaSourceAdmissionIntent.Publish, sourceSet.Admission.Intent);
                admittedSources = sourceSet.SourceFiles.ToArray();
                stagingPath = sourceSet.StagingPath;
            });
            using (var testOnlySource = new FileStream(Path.Combine(sourceDirectory, "TestOnlyDialog.frm"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var testOnlySidecar = new FileStream(Path.Combine(sourceDirectory, "TestOnlyDialog.frx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var excludedSidecar = new FileStream(Path.Combine(sourceDirectory, "MarkerExcluded.frx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = await command.RunAsync(fixture.Context, cancellation.Token);

                Assert.True(result.ExitCode == 0, result.StandardError);
                Assert.Contains($"Published {fixture.Context.PublishDocumentPath}", result.StandardOutput, StringComparison.Ordinal);
                Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
            }

            Assert.NotNull(admittedSources);
            Assert.Equal(
                new[] { "UnicodeModule", "ContractClass", "Dialog", "UnicodeBomModule" },
                admittedSources.Select(source => source.ImportVerification.ComponentName));
            Assert.Equal(
                new[] { activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}", "utf8bom", "utf16le", "utf16be" },
                admittedSources.Select(source => source.ImportVerification.OriginalEncoding));
            var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                fixture, fixture.Context.PublishDocumentPath, admittedSources, activeCodePage, temp.Path, cancellation.Token);
            Assert.NotNull(stagingPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in fixture.CallerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            output.WriteLine(
                $"Ordinary publish selection: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; reopened Excel {reopenedVersion}; " +
                "four included components retained, unread testOnly form/FRX and marker-excluded Unicode form/FRX absent; CommonModule marker ignored.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryPublishKeepsCapturedSelectionAndSidecarsAfterAuthoringChanges()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryPublishFixtureAsync(temp, activeCodePage, cancellation.Token);
            var sourceDirectory = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules");
            foreach (var deleteForm in new[] { false, true })
            {
                foreach (var source in fixture.CallerBytes)
                {
                    File.WriteAllBytes(source.Key, source.Value);
                }

                File.Delete(Path.Combine(sourceDirectory, "LateAdded.bas"));
                var changes = new Dictionary<string, byte[]?>
                {
                    ["UnicodeModule.bas"] = [0xff],
                    ["ContractClass.cls"] = null,
                    ["Dialog.frm"] = deleteForm ? null : [0xff],
                    ["Dialog.frx"] = deleteForm ? null : [0xff],
                    ["MarkerExcluded.frm"] = [0xef, 0xbb, 0xbf, 0xc3, 0x28],
                    ["LateAdded.bas"] = Encoding.ASCII.GetBytes("Attribute VB_Name = \"LateAdded\"\r\nOption Explicit\r\n")
                };
                IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
                string? stagingPath = null;
                var command = CreateOrdinaryPublishCommand(sourceSet =>
                {
                    Assert.Equal(activeCodePage, sourceSet.ActiveCodePage);
                    Assert.NotNull(sourceSet.Admission);
                    Assert.Equal(VbaSourceAdmissionIntent.Publish, sourceSet.Admission.Intent);
                    admittedSources = sourceSet.SourceFiles.ToArray();
                    stagingPath = sourceSet.StagingPath;
                    foreach (var change in changes)
                    {
                        var path = Path.Combine(sourceDirectory, change.Key);
                        if (change.Value is null)
                        {
                            File.Delete(path);
                        }
                        else
                        {
                            File.WriteAllBytes(path, change.Value);
                        }
                    }
                });

                var result = await command.RunAsync(fixture.Context, cancellation.Token);

                Assert.True(result.ExitCode == 0, result.StandardError);
                Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
                Assert.NotNull(admittedSources);
                foreach (var change in changes)
                {
                    var path = Path.Combine(sourceDirectory, change.Key);
                    if (change.Value is null)
                    {
                        Assert.False(File.Exists(path));
                    }
                    else
                    {
                        Assert.Equal(change.Value, File.ReadAllBytes(path));
                    }
                }

                var phase = deleteForm ? "deleted" : "replaced";
                var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                    fixture, fixture.Context.PublishDocumentPath, admittedSources, activeCodePage, temp.CreateDirectory(phase), cancellation.Token);
                Assert.NotNull(stagingPath);
                Assert.False(Directory.Exists(stagingPath));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
                foreach (var source in fixture.CallerBytes.Where(source => !changes.ContainsKey(Path.GetFileName(source.Key))))
                {
                    Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
                }

                output.WriteLine(
                    $"Ordinary publish capture independence: form/FRX {phase}; actual GetACP {activeCodePage}; " +
                    $"seed Excel {fixture.SeedExcelVersion}; reopened Excel {reopenedVersion}; included state retained, mutated exclusion and late addition ignored.");
            }
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryPublishStructuralAndEncodingFailuresPreserveCompletedOutputWithoutStartingExcel()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryPublishFixtureAsync(temp, activeCodePage, cancellation.Token);
            var sourceDirectory = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules");
            var automation = new RecordingOwnedWorkbookGenerationAutomation();
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            var command = CreateOrdinaryPublishCommand(
                sourceSet => admittedSources = sourceSet.SourceFiles.ToArray(), automation);
            var baseline = await command.RunAsync(fixture.Context, cancellation.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            Assert.NotNull(admittedSources);
            var completedWorkbook = File.ReadAllBytes(fixture.Context.PublishDocumentPath);
            var utf8Bom = new UTF8Encoding(true, true);
            var failures = new List<(string FileName, byte[] Bytes, string ExpectedError)>
            {
                ("MarkerExcluded.frm",
                    utf8Bom.GetPreamble()
                        .Concat(utf8Bom.GetBytes("'#ExcludePublish\r\n" + string.Concat(Enumerable.Repeat("' Padding\r\n", 40))))
                        .Concat(new byte[] { 0xc3, 0x28 }).ToArray(),
                    "utf8bom")
            };
            if (activeCodePage != 65001)
            {
                failures.Add((
                    "UnicodeModule.bas",
                    utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(
                        "Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\n' \U0001f642\r\n")).ToArray(),
                    $"Windows code page {activeCodePage}"));
            }

            foreach (var failure in failures)
            {
                foreach (var source in fixture.CallerBytes)
                {
                    File.WriteAllBytes(source.Key, source.Value);
                }

                var sourcePath = Path.Combine(sourceDirectory, failure.FileName);
                File.WriteAllBytes(sourcePath, failure.Bytes);

                var result = await command.RunAsync(fixture.Context, cancellation.Token);

                Assert.Equal(1, result.ExitCode);
                Assert.Contains(failure.ExpectedError, result.StandardError, StringComparison.Ordinal);
                Assert.Equal(1, automation.StartedRuns);
                Assert.Equal(1, automation.CompletedRuns);
                Assert.Equal(completedWorkbook, File.ReadAllBytes(fixture.Context.PublishDocumentPath));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
                foreach (var source in fixture.CallerBytes)
                {
                    Assert.Equal(source.Key == sourcePath ? failure.Bytes : source.Value, File.ReadAllBytes(source.Key));
                }

                Assert.Equal(initialProcesses.Order(), CaptureExcelProcessIds().Order());
            }

            foreach (var source in fixture.CallerBytes)
            {
                File.WriteAllBytes(source.Key, source.Value);
            }

            var duplicateDirectory = Path.Combine(sourceDirectory, "duplicate");
            Directory.CreateDirectory(duplicateDirectory);
            var duplicatePath = Path.Combine(duplicateDirectory, "testonlydialog.FRM");
            byte[] duplicateBytes = [0xef, 0xbb, 0xbf, 0xc3, 0x28];
            File.WriteAllBytes(duplicatePath, duplicateBytes);

            var collision = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.Equal(1, collision.ExitCode);
            Assert.Contains("Duplicate VBA source file names", collision.StandardError, StringComparison.Ordinal);
            Assert.Equal(1, automation.StartedRuns);
            Assert.Equal(1, automation.CompletedRuns);
            Assert.Equal(completedWorkbook, File.ReadAllBytes(fixture.Context.PublishDocumentPath));
            Assert.Equal(duplicateBytes, File.ReadAllBytes(duplicatePath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in fixture.CallerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            Assert.Equal(initialProcesses.Order(), CaptureExcelProcessIds().Order());
            var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                fixture, fixture.Context.PublishDocumentPath, admittedSources, activeCodePage, temp.Path, cancellation.Token);
            output.WriteLine(
                $"Ordinary publish early failure: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"preserved output reopened with Excel {reopenedVersion}; {failures.Count + 1} structural/encoding failures without another Excel invocation." +
                (activeCodePage == 65001 ? " ACP 65001 represents all valid Unicode, so the legacy ACP projection-failure case is inapplicable." : string.Empty));
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryPublishCancellationAfterOwnedSavePreservesPreviousOutputAndCleansStaging()
    {
        using var temp = TempDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryPublishFixtureAsync(temp, activeCodePage, deadline.Token);
            IReadOnlyList<VbeImportSourceFile>? previousSources = null;
            var baseline = await CreateOrdinaryPublishCommand(
                sourceSet => previousSources = sourceSet.SourceFiles.ToArray()).RunAsync(fixture.Context, deadline.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            Assert.NotNull(previousSources);
            var previousWorkbook = File.ReadAllBytes(fixture.Context.PublishDocumentPath);
            WriteEncodedFixtureSource(
                Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "UnicodeModule.bas"),
                $"Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\nPublic Const NonAsciiValue As String = \"{fixture.NonAsciiText} updated\"\r\n",
                StrictEncoding(activeCodePage));
            var callerBytes = fixture.CallerBytes.Keys.ToDictionary(path => path, File.ReadAllBytes);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var automation = new RecordingOwnedWorkbookGenerationAutomation(cancellation);
            string? stagingPath = null;
            var command = CreateOrdinaryPublishCommand(sourceSet => stagingPath = sourceSet.StagingPath, automation);

            var result = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.Equal(130, result.ExitCode);
            Assert.Contains("output commit", result.StandardError, StringComparison.Ordinal);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, automation.StartedRuns);
            Assert.Equal(1, automation.CompletedRuns);
            Assert.Equal(previousWorkbook, File.ReadAllBytes(fixture.Context.PublishDocumentPath));
            Assert.NotNull(stagingPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in callerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                fixture, fixture.Context.PublishDocumentPath, previousSources, activeCodePage, temp.Path, deadline.Token);
            output.WriteLine(
                $"Ordinary publish cancellation before commit: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"real updated publish saved and owned Excel released; previous output reopened with Excel {reopenedVersion}; staging removed.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task OrdinaryPublishCancellationAfterCommitRetainsSuccessfulUpdatedOutput()
    {
        using var temp = TempDirectory.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryPublishFixtureAsync(temp, activeCodePage, deadline.Token);
            var baseline = await CreateOrdinaryPublishCommand(_ => { }).RunAsync(fixture.Context, deadline.Token);
            Assert.True(baseline.ExitCode == 0, baseline.StandardError);
            var previousWorkbook = File.ReadAllBytes(fixture.Context.PublishDocumentPath);
            WriteEncodedFixtureSource(
                Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "UnicodeModule.bas"),
                $"Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\nPublic Const NonAsciiValue As String = \"{fixture.NonAsciiText} updated\"\r\n",
                StrictEncoding(activeCodePage));
            var callerBytes = fixture.CallerBytes.Keys.ToDictionary(path => path, File.ReadAllBytes);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var automation = new RecordingOwnedWorkbookGenerationAutomation();
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            string? stagingPath = null;
            var command = CreateOrdinaryPublishCommand(
                sourceSet =>
                {
                    admittedSources = sourceSet.SourceFiles.ToArray();
                    stagingPath = sourceSet.StagingPath;
                },
                automation,
                new CancelAfterCommitTransactionFactory(cancellation));

            var result = await command.RunAsync(fixture.Context, cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.Contains($"Published {fixture.Context.PublishDocumentPath}", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Imported 4 source files.", result.StandardOutput, StringComparison.Ordinal);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, automation.StartedRuns);
            Assert.Equal(1, automation.CompletedRuns);
            Assert.False(previousWorkbook.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.Context.PublishDocumentPath)));
            Assert.NotNull(admittedSources);
            var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                fixture, fixture.Context.PublishDocumentPath, admittedSources, activeCodePage, temp.Path, deadline.Token);
            Assert.NotNull(stagingPath);
            Assert.False(Directory.Exists(stagingPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!, ".AdmissionBook.*.tmp.xlsm"));
            foreach (var source in callerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            output.WriteLine(
                $"Ordinary publish cancellation after commit: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                $"updated output reopened with Excel {reopenedVersion}; success retained and staging removed.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ExplicitImportAdmissionFailurePreservesRealWorkbookAndExcelProcessSet()
    {
        using var temp = TempDirectory.Create();
        var initialProcesses = CaptureExcelProcessIds();
        var sourceDirectory = temp.CreateDirectory("src");
        var sourcePath = Path.Combine(sourceDirectory, "Module1.bas");
        var targetPath = Path.Combine(temp.Path, "Target.xlsm");
        CreateEmptyMacroEnabledWorkbook(targetPath);
        var originalWorkbook = File.ReadAllBytes(targetPath);
        byte[] malformedBomSource = [0xef, 0xbb, 0xbf, 0xc3, 0x28];
        File.WriteAllBytes(sourcePath, malformedBomSource);

        var command = new ImportCommand(new ExcelComWorkbookGenerationAutomation());
        var result = await command.RunAsync(
            new ImportCommandRequest(sourceDirectory, targetPath, temp.Path),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("utf8bom", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(originalWorkbook, File.ReadAllBytes(targetPath));
        Assert.Equal(malformedBomSource, File.ReadAllBytes(sourcePath));
        Assert.Empty(Directory.GetFiles(temp.Path, ".Target.*.tmp.xlsm"));
        if (ActiveWindowsAnsiCodePage.Get() != 65001)
        {
            var utf8Bom = new UTF8Encoding(true, true);
            var unrepresentableSource = utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(
                "Attribute VB_Name = \"Module1\"\r\n' 😀\r\n")).ToArray();
            File.WriteAllBytes(sourcePath, unrepresentableSource);
            var projectionResult = await command.RunAsync(
                new ImportCommandRequest(sourceDirectory, targetPath, temp.Path),
                CancellationToken.None);
            Assert.Equal(1, projectionResult.ExitCode);
            Assert.Contains("Windows code page", projectionResult.StandardError, StringComparison.Ordinal);
            Assert.Equal(originalWorkbook, File.ReadAllBytes(targetPath));
            Assert.Equal(unrepresentableSource, File.ReadAllBytes(sourcePath));
        }

        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ConventionCompliantIdentifierRecasingPersistsAcrossOwnedSaveAndReopen()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        var firstClassSourcePath = Path.Combine(temp.Path, "FileNameProvider.cls");
        var secondClassSourcePath = Path.Combine(temp.Path, "FilenameAuthority.cls");
        var workbookPath = Path.Combine(temp.Path, "Recasing.xlsm");
        WriteRecasingClassSource(
            firstClassSourcePath,
            "FileNameProvider",
            "FileName",
            "first");
        WriteRecasingClassSource(
            secondClassSourcePath,
            "FilenameAuthority",
            "Filename",
            "second");
        var originalFirstClassBytes = File.ReadAllBytes(firstClassSourcePath);
        var originalSecondClassBytes = File.ReadAllBytes(secondClassSourcePath);
        CreateEmptyMacroEnabledWorkbook(workbookPath);

        string stagingDirectory;
        using (var sourceSet = VbeImportSourceSet.Create(
                   [
                       new VbaSourceFile(firstClassSourcePath, VbaSourceKind.ClassModule, null),
                       new VbaSourceFile(secondClassSourcePath, VbaSourceKind.ClassModule, null)
                   ],
                   activeCodePage))
        {
            stagingDirectory = Path.GetDirectoryName(sourceSet.SourceFiles[0].SourcePath)!;
            IReadOnlyList<VbeIdentifierRecasingPair>? immediatePairs = null;
            var immediate = await ImportVerifySaveWithOwnedExcelAsync(
                workbookPath,
                sourceSet.SourceFiles,
                components =>
                {
                    immediatePairs = AssertConventionCompliantRecasing(
                        sourceSet.SourceFiles,
                        components);
                },
                cancellation.Token);
            var reopened = await CaptureWithOwnedExcelAsync(
                workbookPath,
                sourceSet.SourceFiles,
                cancellation.Token);
            var reopenedPairs = AssertConventionCompliantRecasing(
                sourceSet.SourceFiles,
                reopened.Components);

            Assert.NotNull(immediatePairs);
            Assert.Equal(immediatePairs, reopenedPairs);
            Assert.Equal(immediate.ExcelVersion, reopened.ExcelVersion);
            Assert.Equal(immediate.Components.Count, reopened.Components.Count);
            for (var index = 0; index < immediate.Components.Count; index++)
            {
                Assert.Equal(
                    immediate.Components[index].ComponentName,
                    reopened.Components[index].ComponentName);
                Assert.Equal(
                    immediate.Components[index].ComponentKind,
                    reopened.Components[index].ComponentKind);
                Assert.Equal(
                    immediate.Components[index].CodeModuleLines,
                    reopened.Components[index].CodeModuleLines);
            }

            Assert.DoesNotContain("FileNameProvider", immediate.InitialComponentNames);
            Assert.DoesNotContain("FilenameAuthority", immediate.InitialComponentNames);
            output.WriteLine(
                $"VBE identifier recasing evidence: source-built blank .xlsm initial components " +
                $"[{string.Join(", ", immediate.InitialComponentNames)}]; import order " +
                $"{string.Join(" -> ", sourceSet.SourceFiles.Select(source => source.ImportVerification.ComponentName))}; " +
                $"import Excel {immediate.ExcelVersion}; reopen Excel {reopened.ExcelVersion}; " +
                $"active ACP {sourceSet.ActiveCodePage}; pairs " +
                $"{string.Join(", ", immediatePairs.Select(pair => $"{pair.SourceIdentifier} -> {pair.VbeIdentifier}"))}.");
        }

        Assert.False(Directory.Exists(stagingDirectory));
        Assert.Equal(originalFirstClassBytes, File.ReadAllBytes(firstClassSourcePath));
        Assert.Equal(originalSecondClassBytes, File.ReadAllBytes(secondClassSourcePath));
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task ProductionGenerationReportsRecasingWarningsInComponentImportOrderAndPersistsOutput()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        var templatePath = Path.Combine(temp.Path, "RecasingProductionTemplate.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "RecasingProduction.xlsm");
        var sourceSpecifications = new[]
        {
            ("ZFileNameProvider", "FileName", "first"),
            ("AOtherNameProvider", "OtherName", "second"),
            ("YFilenameAuthority", "Filename", "third"),
            ("BOthernameAuthority", "Othername", "fourth")
        };
        var sourceFiles = sourceSpecifications
            .Select(specification =>
            {
                var path = Path.Combine(temp.Path, specification.Item1 + ".cls");
                WriteRecasingClassSource(
                    path,
                    specification.Item1,
                    specification.Item2,
                    specification.Item3);
                return new VbaSourceFile(path, VbaSourceKind.ClassModule, null);
            })
            .ToArray();
        var originalSourceBytes = sourceFiles.ToDictionary(
            source => source.SourcePath,
            source => File.ReadAllBytes(source.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        CreateEmptyMacroEnabledWorkbook(templatePath);
        string verificationStagingDirectory;

        using (var verificationSourceSet = VbeImportSourceSet.Create(
                   sourceFiles,
                   activeCodePage))
        {
            verificationStagingDirectory = Path.GetDirectoryName(
                verificationSourceSet.SourceFiles[0].SourcePath)!;
            var result = await CreateGenerationPipeline().GenerateAsync(
                "RecasingProduction",
                templatePath,
                targetPath,
                [],
                sourceFiles,
                WorkbookAutomationTimeouts.Default,
                cancellation.Token);

            Assert.Collection(
                result.VerificationReport.Warnings,
                warning => AssertRecasingWarning(
                    warning,
                    "ZFileNameProvider",
                    "FileName",
                    "Filename"),
                warning => AssertRecasingWarning(
                    warning,
                    "AOtherNameProvider",
                    "OtherName",
                    "Othername"));
            Assert.True(File.Exists(targetPath));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(targetPath)!,
                ".RecasingProduction.*.tmp.xlsm"));

            var reopened = await CaptureWithOwnedExcelAsync(
                targetPath,
                verificationSourceSet.SourceFiles,
                cancellation.Token);
            Assert.Equal(4, reopened.Components.Count);
            AssertRecasingWarning(
                Assert.IsType<VbeIdentifierRecasingWarning>(
                    VbeImportedComponentVerifier.Verify(
                        verificationSourceSet.SourceFiles[0].ImportVerification,
                        reopened.Components[0])),
                "ZFileNameProvider",
                "FileName",
                "Filename");
            AssertRecasingWarning(
                Assert.IsType<VbeIdentifierRecasingWarning>(
                    VbeImportedComponentVerifier.Verify(
                        verificationSourceSet.SourceFiles[1].ImportVerification,
                        reopened.Components[1])),
                "AOtherNameProvider",
                "OtherName",
                "Othername");
            Assert.Null(VbeImportedComponentVerifier.Verify(
                verificationSourceSet.SourceFiles[2].ImportVerification,
                reopened.Components[2]));
            Assert.Null(VbeImportedComponentVerifier.Verify(
                verificationSourceSet.SourceFiles[3].ImportVerification,
                reopened.Components[3]));

            output.WriteLine(
                $"Production recasing warnings: Excel {reopened.ExcelVersion}; active ACP {activeCodePage}; " +
                $"component order {string.Join(" -> ", result.VerificationReport.Warnings.Select(warning => warning.ComponentName))}.");
        }

        Assert.False(Directory.Exists(verificationStagingDirectory));
        foreach (var source in sourceFiles)
        {
            Assert.Equal(
                originalSourceBytes[source.SourcePath],
                File.ReadAllBytes(source.SourcePath));
        }
        await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task DoctorInspectsBuildAndPublishProfilesWithoutChangingCallerFilesOrLeavingOwnedExcel()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Context.PublishDocumentPath)!);
            File.WriteAllBytes(fixture.Context.BinDocumentPath, Encoding.ASCII.GetBytes("existing-bin-output"));
            File.WriteAllBytes(fixture.Context.PublishDocumentPath, Encoding.ASCII.GetBytes("existing-publish-output"));
            var callerBytes = Directory.GetFiles(fixture.Context.ProjectRoot, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            var project = new ProjectContextResolver(new JsonProjectManifestStore()).ResolveProject(
                new(fixture.Context.ProjectRoot, null, fixture.Context.ProjectRoot));

            var result = await new ExcelProjectMaterializationDiagnosticPort().RunAsync(project, cancellation.Token);

            Assert.True(result.Complete);
            Assert.False(result.Canceled);
            Assert.Collection(
                result.Results,
                check =>
                {
                    Assert.Equal("project.workbookMaterialization/AdmissionBook/build", check.Id);
                    Assert.True(check.Status == DiagnosticStatus.Pass, $"{check.Id}: {check.Status}: {check.Message}");
                },
                check =>
                {
                    Assert.Equal("project.workbookMaterialization/AdmissionBook/publish", check.Id);
                    Assert.True(check.Status == DiagnosticStatus.Pass, $"{check.Id}: {check.Status}: {check.Message}");
                });
            Assert.Equal(
                callerBytes.Keys.Order(StringComparer.Ordinal),
                Directory.GetFiles(fixture.Context.ProjectRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            foreach (var source in callerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            output.WriteLine(
                $"Doctor profile observation: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                "Build and Publish PASS; ACP/BOM sources, FRX, template, manifest, bin and publish bytes unchanged.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task SnapshotBuildPreservesAcpBomAndFormBytesAndRejectsInvalidInputBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            var snapshotPath = CopySnapshotFixtureSources(temp, fixture);
            var snapshotBytes = Directory.GetFiles(snapshotPath, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!);
            CreateEmptyMacroEnabledWorkbook(fixture.Context.BinDocumentPath);
            var originalBin = File.ReadAllBytes(fixture.Context.BinDocumentPath);
            var outputPath = Path.Combine(temp.CreateDirectory("snapshot-output"), "AdmissionBook.xlsm");
            IReadOnlyList<VbeImportSourceFile>? admittedSources = null;
            var automation = new RecordingOwnedWorkbookGenerationAutomation();
            var command = CreateOrdinaryBuildCommand(sourceSet =>
            {
                Assert.Equal(activeCodePage, sourceSet.ActiveCodePage);
                Assert.NotNull(sourceSet.Admission);
                admittedSources = sourceSet.SourceFiles.ToArray();
            }, automation);

            var result = await command.RunSnapshotAsync(fixture.Context, snapshotPath, outputPath, cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            Assert.NotNull(admittedSources);
            Assert.Equal(
                new[] { "utf8bom", "utf16le", "utf16be", activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}" },
                admittedSources.Select(source => source.ImportVerification.OriginalEncoding));
            var reopenedVersion = await AssertOrdinaryWorkbookAsync(
                fixture, outputPath, admittedSources, activeCodePage, temp.Path, cancellation.Token);
            var completedOutput = File.ReadAllBytes(outputPath);
            foreach (var source in snapshotBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }

            var badSourcePath = Path.Combine(snapshotPath, "modules", "UnicodeModule.bas");
            List<byte[]> failures = [[0xef, 0xbb, 0xbf, 0xc3, 0x28]];
            if (activeCodePage != 65001)
            {
                var utf8Bom = new UTF8Encoding(true, true);
                failures.Add(utf8Bom.GetPreamble().Concat(utf8Bom.GetBytes(
                    "Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\n' \U0001f642\r\n")).ToArray());
            }
            foreach (var invalidBytes in failures)
            {
                File.WriteAllBytes(badSourcePath, invalidBytes);
                var failed = await command.RunSnapshotAsync(fixture.Context, snapshotPath, outputPath, cancellation.Token);
                Assert.Equal(1, failed.ExitCode);
                Assert.Equal(1, automation.StartedRuns);
                Assert.Equal(1, automation.CompletedRuns);
                Assert.Equal(completedOutput, File.ReadAllBytes(outputPath));
                Assert.Equal(invalidBytes, File.ReadAllBytes(badSourcePath));
            }

            Assert.Equal(originalBin, File.ReadAllBytes(fixture.Context.BinDocumentPath));
            foreach (var source in fixture.CallerBytes)
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }
            output.WriteLine($"Snapshot Build v2: actual GetACP {activeCodePage}; reopened Excel {reopenedVersion}; " +
                $"four BOM/ACP components and nested FRX state preserved; {failures.Count} failures before another Excel start.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    [WindowsExcelIntegrationFact]
    [Trait("Category", "WindowsExcelIntegration")]
    public async Task SnapshotTestExecutesEditorOnlyBomSourceAndReturnsPersistentLocationWithoutChangingBin()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var initialProcesses = CaptureExcelProcessIds();
        var initialBootstrapFiles = CaptureBootstrapWorkbookPaths();
        var activeCodePage = ActiveWindowsAnsiCodePage.Get();
        try
        {
            var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellation.Token);
            var snapshotPath = CopySnapshotFixtureSources(temp, fixture);
            var editorOnlyPath = Path.Combine(snapshotPath, "modules", "SnapshotTests.bas");
            WriteEncodedFixtureSource(editorOnlyPath, string.Join("\r\n", [
                "Attribute VB_Name = \"SnapshotTests\"",
                "Option Explicit",
                "Public Sub UnitTestMain()",
                "    Dim resultSheet As Object",
                "    Set resultSheet = ThisWorkbook.Worksheets(1)",
                "    resultSheet.Name = \"UNIT_TEST_SHEET\"",
                "    resultSheet.Cells(1, 1).Value2 = \"Module\"",
                "    resultSheet.Cells(2, 1).Value2 = \"SnapshotTests\"",
                "    resultSheet.Cells(2, 2).Value2 = \"UnitTestMain\"",
                "    If UnicodeModule.NonAsciiValue = UnicodeBomModule.BomValue Then",
                "        resultSheet.Cells(2, 3).Value2 = \"OK\"",
                "    Else",
                "        resultSheet.Cells(2, 3).Value2 = \"NG\"",
                "    End If",
                "    resultSheet.Cells(2, 4).Value2 = UnicodeModule.NonAsciiValue",
                "End Sub",
                string.Empty
            ]), new UTF8Encoding(true, true));
            var capturedBytes = Directory.GetFiles(snapshotPath, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Context.BinDocumentPath)!);
            CreateEmptyMacroEnabledWorkbook(fixture.Context.BinDocumentPath);
            var originalBin = File.ReadAllBytes(fixture.Context.BinDocumentPath);
            var build = CreateOrdinaryBuildCommand(sourceSet => Assert.NotNull(sourceSet.Admission));
            var command = new TestCommand(build, new ExcelComWorkbookTestRunner(),
                new TestResultOutputFormatter(), new TestProcedureSourceLocator(), new FileSystemPathIdentityResolver());

            var result = await command.RunAsync(fixture.Context,
                new TestCommandRequest("ndjson", true, new(), TimeSpan.FromMinutes(1), snapshotPath), cancellation.Token);

            Assert.True(result.ExitCode == 0, result.StandardError);
            using var finished = JsonDocument.Parse(Assert.Single(result.StandardOutput.Split('\n'),
                line => line.Contains("\"type\":\"testFinished\"", StringComparison.Ordinal)));
            Assert.Equal("passed", finished.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(fixture.NonAsciiText, finished.RootElement.GetProperty("message").GetString());
            var persistentPath = Path.Combine(fixture.Context.DocumentSourceSetPath, "modules", "SnapshotTests.bas");
            Assert.Equal(new Uri(persistentPath).AbsoluteUri,
                finished.RootElement.GetProperty("location").GetProperty("uri").GetString());
            Assert.False(File.Exists(persistentPath));
            Assert.Equal(originalBin, File.ReadAllBytes(fixture.Context.BinDocumentPath));
            foreach (var source in fixture.CallerBytes.Concat(capturedBytes))
            {
                Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
            }
            output.WriteLine($"Snapshot Test v2: actual GetACP {activeCodePage}; seed Excel {fixture.SeedExcelVersion}; " +
                "real UnitTestMain executed from editor-only UTF-8 BOM source; ACP/BOM values and persistent location preserved.");
        }
        finally
        {
            await WaitForProcessSetAsync(initialProcesses, TimeSpan.FromSeconds(20));
            Assert.Equal(initialBootstrapFiles.Order(), CaptureBootstrapWorkbookPaths().Order());
        }
    }

    private static string CopySnapshotFixtureSources(TempDirectory temp, OrdinaryWorkbookFixture fixture)
    {
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        foreach (var source in Directory.GetFiles(Path.Combine(fixture.Context.DocumentSourceSetPath, "modules")))
        {
            var target = Path.Combine(snapshotPath, Path.GetRelativePath(fixture.Context.DocumentSourceSetPath, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        return snapshotPath;
    }

    private static void WriteRecasingClassSource(
        string sourcePath,
        string componentName,
        string propertyName,
        string value)
        => File.WriteAllText(
            sourcePath,
            string.Join("\r\n", [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1  'True",
                "END",
                $"Attribute VB_Name = \"{componentName}\"",
                "Attribute VB_GlobalNameSpace = False",
                "Attribute VB_Creatable = False",
                "Attribute VB_PredeclaredId = False",
                "Attribute VB_Exposed = False",
                "Option Explicit",
                string.Empty,
                "' #############################################################################",
                "'!",
                "'! Provides one source-defined casing authority for real-Excel evidence.",
                "'!",
                "' #############################################################################",
                string.Empty,
                "'* Source-defined casing authority for the real-Excel recasing fixture.",
                $"Public Property Get {propertyName}() As String",
                $"    {propertyName} = \"{value}\"",
                "End Property",
                string.Empty
            ]),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static BuildCommand CreateOrdinaryBuildCommand(
        Action<VbeImportSourceSet> sourceSetCreated,
        IWorkbookGenerationAutomation? automation = null,
        IWorkbookOutputTransactionFactory? transactionFactory = null)
        => new(CreateOrdinaryOutputCommand(sourceSetCreated, automation, transactionFactory), new FileSystemPathIdentityResolver());

    private static PublishCommand CreateOrdinaryPublishCommand(
        Action<VbeImportSourceSet> sourceSetCreated,
        IWorkbookGenerationAutomation? automation = null,
        IWorkbookOutputTransactionFactory? transactionFactory = null)
        => new(CreateOrdinaryOutputCommand(sourceSetCreated, automation, transactionFactory));

    private static WorkbookOutputCommand CreateOrdinaryOutputCommand(
        Action<VbeImportSourceSet> sourceSetCreated,
        IWorkbookGenerationAutomation? automation,
        IWorkbookOutputTransactionFactory? transactionFactory)
        => new(
            new WorkbookMaterializer(
                new WorkbookSourcePlanner(),
                automation ?? new ExcelComWorkbookGenerationAutomation(),
                new WorkbookReferenceNormalizer(new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
                transactionFactory ?? new WorkbookOutputTransactionFactory(),
                new VbeImportSourceSetFactory(ActiveWindowsAnsiCodePage.Get, sourceSetCreated)));

    private static async Task<OrdinaryWorkbookFixture> CreateOrdinaryWorkbookFixtureAsync(
        TempDirectory temp,
        int activeCodePage,
        CancellationToken cancellationToken)
    {
        var root = temp.CreateDirectory("Project");
        var manifestStore = new JsonProjectManifestStore();
        manifestStore.Save(root, ProjectManifest.CreateDefault("AdmissionProject", "AdmissionBook", root, null));
        var context = new ProjectContextResolver(manifestStore).Resolve(new(root, "AdmissionBook", root));
        Directory.CreateDirectory(context.DocumentSourceSetPath);
        CreateEmptyMacroEnabledWorkbook(context.TemplateDocumentPath);
        var sourceDirectory = Path.Combine(context.DocumentSourceSetPath, "modules");
        Directory.CreateDirectory(sourceDirectory);
        var nonAsciiText = SelectNonAsciiFixtureText(activeCodePage);
        var formPath = Path.Combine(sourceDirectory, "Dialog.frm");
        var seedPath = Path.Combine(temp.Path, "FormSeed.xlsm");
        CreateEmptyMacroEnabledWorkbook(seedPath);
        var seedExcelVersion = await UseOwnedExcelWorkbookAsync(
            seedPath,
            session =>
            {
                ExportNestedUserFormFixture(session.WorkbookObject, formPath, nonAsciiText);
                dynamic excel = session.ExcelObject;
                return (string?)Convert.ToString(excel.Version) ?? string.Empty;
            },
            cancellationToken);
        var formText = DecodeActiveCodePageFile(formPath, activeCodePage);
        WriteEncodedFixtureSource(formPath, formText, new UnicodeEncoding(false, true, true));
        WriteEncodedFixtureSource(
            Path.Combine(sourceDirectory, "UnicodeModule.bas"),
            $"Attribute VB_Name = \"UnicodeModule\"\r\nOption Explicit\r\nPublic Const NonAsciiValue As String = \"{nonAsciiText}\"\r\n",
            StrictEncoding(activeCodePage));
        WriteEncodedFixtureSource(
            Path.Combine(sourceDirectory, "UnicodeBomModule.bas"),
            $"Attribute VB_Name = \"UnicodeBomModule\"\r\nOption Explicit\r\nPublic Const BomValue As String = \"{nonAsciiText}\"\r\n",
            new UnicodeEncoding(true, true, true));
        WriteEncodedFixtureSource(
            Path.Combine(sourceDirectory, "ContractClass.cls"),
            string.Join("\r\n", [
                "VERSION 1.0 CLASS", "BEGIN", "  MultiUse = -1  'True", "END",
                "Attribute VB_Name = \"ContractClass\"", "Attribute VB_GlobalNameSpace = False",
                "Attribute VB_Creatable = False", "Attribute VB_PredeclaredId = True",
                "Attribute VB_Exposed = False", "Option Explicit", "Public Function Item() As String",
                $"Attribute Item.VB_Description = \"Default member {nonAsciiText}\"",
                "Attribute Item.VB_UserMemId = 0", $"    Item = \"{nonAsciiText}\"", "End Function", string.Empty
            ]),
            new UTF8Encoding(true, true));
        var callerBytes = Directory.GetFiles(sourceDirectory)
            .Append(context.TemplateDocumentPath)
            .Append(context.ManifestPath)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        return new(context, nonAsciiText, seedExcelVersion, callerBytes);
    }

    private static async Task<OrdinaryWorkbookFixture> CreateOrdinaryPublishFixtureAsync(
        TempDirectory temp,
        int activeCodePage,
        CancellationToken cancellationToken)
    {
        var fixture = await CreateOrdinaryWorkbookFixtureAsync(temp, activeCodePage, cancellationToken);
        var context = fixture.Context;
        context.Document.CommonModules.AddRange(
        [
            new InstalledCommonModule("UnicodeModule", "UnicodeModule.bas", Requested: true, TestOnly: false, Orphaned: true),
            new InstalledCommonModule("ContractClass", "ContractClass.cls", Requested: false, TestOnly: false),
            new InstalledCommonModule("TestOnlyDialog", "TestOnlyDialog.frm", Requested: true, TestOnly: true)
        ]);
        new JsonProjectManifestStore().Save(context.ProjectRoot, context.Manifest);
        var sourceDirectory = Path.Combine(context.DocumentSourceSetPath, "modules");
        var commonModulePath = Path.Combine(sourceDirectory, "UnicodeModule.bas");
        var commonModuleText = StrictEncoding(activeCodePage).GetString(File.ReadAllBytes(commonModulePath));
        WriteEncodedFixtureSource(
            commonModulePath,
            commonModuleText.Replace("Option Explicit\r\n", "Option Explicit\r\n'#ExcludePublish\r\n", StringComparison.Ordinal),
            StrictEncoding(activeCodePage));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "TestOnlyDialog.frm"), [0xef, 0xbb, 0xbf, 0xc3, 0x28]);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "TestOnlyDialog.frx"), [0xff]);
        WriteEncodedFixtureSource(
            Path.Combine(sourceDirectory, "MarkerExcluded.frm"),
            "  \t'#eXcludePublish suffix\r\n' \U0001f642\r\nAttribute VB_Name = \"invalid-name\"\r\n",
            new UTF8Encoding(true, true));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "MarkerExcluded.frx"), [0xff]);
        Directory.CreateDirectory(Path.GetDirectoryName(context.BinDocumentPath)!);
        CreateEmptyMacroEnabledWorkbook(context.BinDocumentPath);
        return fixture with
        {
            CallerBytes = Directory.GetFiles(sourceDirectory)
                .Append(context.TemplateDocumentPath)
                .Append(context.ManifestPath)
                .Append(context.BinDocumentPath)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void WriteEncodedFixtureSource(string path, string text, Encoding encoding)
        => File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());

    private static Task<string> AssertOrdinaryBuildWorkbookAsync(
        OrdinaryWorkbookFixture fixture,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string artifactDirectory,
        CancellationToken cancellationToken)
        => AssertOrdinaryWorkbookAsync(
            fixture, fixture.Context.BinDocumentPath, sources, activeCodePage, artifactDirectory, cancellationToken);

    private static Task<string> AssertOrdinaryWorkbookAsync(
        OrdinaryWorkbookFixture fixture,
        string workbookPath,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string artifactDirectory,
        CancellationToken cancellationToken)
        => UseOwnedExcelWorkbookAsync(
            workbookPath,
            session =>
            {
                object? projectObject = null;
                object? componentsObject = null;
                try
                {
                    dynamic workbook = session.WorkbookObject;
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    componentsObject = project.VBComponents;
                    Assert.Equal(
                        sources.Select(source => source.ImportVerification.ComponentName)
                            .Concat(["ThisWorkbook", "Sheet1"]).Order(StringComparer.Ordinal),
                        CaptureComponentNames(componentsObject).Order(StringComparer.Ordinal));
                }
                finally
                {
                    ComObjectReleaser.Release(componentsObject);
                    ComObjectReleaser.Release(projectObject);
                }

                AssertCurrentWorkbookAfterReopen(
                    session.WorkbookObject,
                    sources,
                    activeCodePage,
                    fixture.NonAsciiText,
                    Path.Combine(artifactDirectory, "OrdinaryBuildContractClass.cls"));
                dynamic excel = session.ExcelObject;
                return (string?)Convert.ToString(excel.Version) ?? string.Empty;
            },
            cancellationToken);

    private sealed record OrdinaryWorkbookFixture(
        ResolvedProjectContext Context,
        string NonAsciiText,
        string SeedExcelVersion,
        IReadOnlyDictionary<string, byte[]> CallerBytes);

    private sealed class RecordingOwnedWorkbookGenerationAutomation(
        CancellationTokenSource? cancelAfterRelease = null) : IWorkbookGenerationAutomation
    {
        private readonly IWorkbookGenerationAutomation inner = new ExcelComWorkbookGenerationAutomation();

        public int StartedRuns { get; private set; }

        public int CompletedRuns { get; private set; }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            StartedRuns++;
            var result = await inner.RunAsync(workbookPath, timeouts, operation, cancellationToken);
            CompletedRuns++;
            cancelAfterRelease?.Cancel();
            return result;
        }
    }

    private sealed class CancelAfterCommitTransactionFactory(
        CancellationTokenSource cancellation) : IWorkbookOutputTransactionFactory
    {
        private readonly IWorkbookOutputTransactionFactory inner = new WorkbookOutputTransactionFactory();

        public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
            => new CancelAfterCommitTransaction(inner.Create(templateWorkbookPath, targetWorkbookPath), cancellation);
    }

    private sealed class CancelAfterCommitTransaction(
        IWorkbookOutputTransaction inner,
        CancellationTokenSource cancellation) : IWorkbookOutputTransaction
    {
        public string StagingWorkbookPath => inner.StagingWorkbookPath;

        public void Commit()
        {
            inner.Commit();
            cancellation.Cancel();
        }

        public void Dispose() => inner.Dispose();
    }

    private static WorkbookMaterializer CreateGenerationPipeline()
        => new(
            (IWorkbookGenerationAutomation)new ExcelComWorkbookGenerationAutomation(),
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())));

    private static Task<VbeOwnedWorkbookObservation> ImportVerifySaveWithOwnedExcelAsync(
        string workbookPath,
        IReadOnlyList<VbeImportSourceFile> sources,
        Action<IReadOnlyList<VbeImportedComponent>> verifyBeforeSave,
        CancellationToken cancellationToken)
        => UseOwnedExcelWorkbookAsync(
            workbookPath,
            session =>
            {
                object? projectObject = null;
                object? componentsObject = null;
                try
                {
                    dynamic excel = session.ExcelObject;
                    dynamic workbook = session.WorkbookObject;
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    componentsObject = project.VBComponents;
                    dynamic components = componentsObject;
                    var initialComponentNames = CaptureComponentNames(componentsObject);
                    foreach (var source in sources)
                    {
                        object? importedComponentObject = null;
                        try
                        {
                            importedComponentObject = components.Import(source.SourcePath);
                        }
                        finally
                        {
                            ComObjectReleaser.Release(importedComponentObject);
                        }
                    }

                    var importedComponents = CaptureImportedComponents(
                        componentsObject,
                        sources);
                    verifyBeforeSave(importedComponents);
                    workbook.Save();
                    return new VbeOwnedWorkbookObservation(
                        Convert.ToString(excel.Version) ?? string.Empty,
                        initialComponentNames,
                        importedComponents);
                }
                finally
                {
                    ComObjectReleaser.Release(componentsObject);
                    ComObjectReleaser.Release(projectObject);
                }
            },
            cancellationToken);

    private static Task<VbeOwnedWorkbookObservation> CaptureWithOwnedExcelAsync(
        string workbookPath,
        IReadOnlyList<VbeImportSourceFile> sources,
        CancellationToken cancellationToken)
        => UseOwnedExcelWorkbookAsync(
            workbookPath,
            session =>
            {
                object? projectObject = null;
                object? componentsObject = null;
                try
                {
                    dynamic excel = session.ExcelObject;
                    dynamic workbook = session.WorkbookObject;
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    componentsObject = project.VBComponents;
                    return new VbeOwnedWorkbookObservation(
                        Convert.ToString(excel.Version) ?? string.Empty,
                        CaptureComponentNames(componentsObject),
                        CaptureImportedComponents(componentsObject, sources));
                }
                finally
                {
                    ComObjectReleaser.Release(componentsObject);
                    ComObjectReleaser.Release(projectObject);
                }
            },
            cancellationToken);

    private static async Task<T> UseOwnedExcelWorkbookAsync<T>(
        string workbookPath,
        Func<ExcelComWorkbookSession, T> action,
        CancellationToken cancellationToken)
    {
        var cleanupGrace = TimeSpan.FromSeconds(5);
        using var terminationController = new OwnedExcelTerminationController();
        await using var dispatcher = new StaComDispatcher();
        try
        {
            return await dispatcher.InvokeAsync(
                () =>
                {
                    var host = ExcelComWorkbookSession.StartOwnedForGeneration(
                        terminationController,
                        cancellationToken);
                    ExcelComWorkbookSession? session = null;
                    try
                    {
                        session = ExcelComWorkbookSession.OpenOwnedForGeneration(
                            host,
                            workbookPath);
                        return action(session);
                    }
                    finally
                    {
                        if (session is not null)
                        {
                            session.DisposeOwnedGeneration(cleanupGrace);
                        }
                        else
                        {
                            ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                                host,
                                cleanupGrace);
                        }
                    }
                },
                cancellationToken);
        }
        finally
        {
            await terminationController.RequestCleanupAsync(cleanupGrace);
        }
    }

    private static IReadOnlyList<string> CaptureComponentNames(object componentsObject)
    {
        dynamic components = componentsObject;
        var componentNames = new List<string>();
        var componentCount = (int)components.Count;
        for (var index = 1; index <= componentCount; index++)
        {
            object? componentObject = null;
            try
            {
                componentObject = components.Item(index);
                dynamic component = componentObject;
                componentNames.Add((string)component.Name);
            }
            finally
            {
                ComObjectReleaser.Release(componentObject);
            }
        }

        return componentNames.AsReadOnly();
    }

    private static IReadOnlyList<VbeImportedComponent> CaptureImportedComponents(
        object componentsObject,
        IReadOnlyList<VbeImportSourceFile> sources)
    {
        dynamic components = componentsObject;
        var importedComponents = new List<VbeImportedComponent>(sources.Count);
        foreach (var source in sources)
        {
            object? componentObject = null;
            try
            {
                componentObject = components.Item(source.ImportVerification.ComponentName);
                importedComponents.Add(CaptureImportedComponent(componentObject));
            }
            finally
            {
                ComObjectReleaser.Release(componentObject);
            }
        }

        return importedComponents.AsReadOnly();
    }

    private static IReadOnlyList<VbeIdentifierRecasingPair> AssertConventionCompliantRecasing(
        IReadOnlyList<VbeImportSourceFile> sources,
        IReadOnlyList<VbeImportedComponent> components)
    {
        Assert.Equal(2, sources.Count);
        Assert.Equal(2, components.Count);
        var warning = Assert.IsType<VbeIdentifierRecasingWarning>(
            VbeImportedComponentVerifier.Verify(
                sources[0].ImportVerification,
                components[0]));
        Assert.Equal(VbeIdentifierRecasingWarning.WarningCode, warning.Code);
        Assert.Equal("FileNameProvider", warning.ComponentName);
        Assert.Equal(
            new VbeIdentifierRecasingPair("FileName", "Filename"),
            Assert.Single(warning.DistinctPairs));
        Assert.Null(VbeImportedComponentVerifier.Verify(
            sources[1].ImportVerification,
            components[1]));
        return warning.DistinctPairs;
    }

    private static void AssertRecasingWarning(
        VbeIdentifierRecasingWarning warning,
        string componentName,
        string sourceIdentifier,
        string vbeIdentifier)
    {
        Assert.Equal(VbeIdentifierRecasingWarning.WarningCode, warning.Code);
        Assert.Equal(componentName, warning.ComponentName);
        Assert.Equal(
            new VbeIdentifierRecasingPair(sourceIdentifier, vbeIdentifier),
            Assert.Single(warning.DistinctPairs));
    }

    private static VbeImportedComponent CaptureImportedComponent(object componentObject)
    {
        object? codeModuleObject = null;
        try
        {
            dynamic component = componentObject;
            var actualKind = (int)component.Type switch
            {
                1 => VbaSourceKind.StandardModule,
                2 => VbaSourceKind.ClassModule,
                3 => VbaSourceKind.Form,
                var type => throw new InvalidOperationException(
                    $"Unexpected imported component type '{type}'.")
            };
            codeModuleObject = component.CodeModule;
            dynamic codeModule = codeModuleObject;
            var lineCount = (int)codeModule.CountOfLines;
            var lines = new string[lineCount];
            for (var line = 1; line <= lineCount; line++)
            {
                lines[line - 1] = (string)codeModule.Lines(line, 1);
            }

            return new VbeImportedComponent((string)component.Name, actualKind, lines);
        }
        finally
        {
            ComObjectReleaser.Release(codeModuleObject);
        }
    }

    private sealed record VbeOwnedWorkbookObservation(
        string ExcelVersion,
        IReadOnlyList<string> InitialComponentNames,
        IReadOnlyList<VbeImportedComponent> Components);

    private static IReadOnlySet<string> CaptureBootstrapWorkbookPaths()
        => Directory
            .EnumerateFiles(
                Path.GetTempPath(),
                "vba-dev-excel-bootstrap-*.xlsx",
                SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static object CreateHiddenExcelApplication()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Excel COM automation requires Windows.");
        }

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is unavailable.");
        var excelObject = Activator.CreateInstance(excelType)
            ?? throw new InvalidOperationException("Excel COM automation could not be started.");
        dynamic excel = excelObject;
        excel.Visible = false;
        excel.DisplayAlerts = false;
        return excelObject;
    }

    private static string ExportNestedUserFormFixture(
        string workbookPath,
        string formSourcePath,
        string nonAsciiText)
    {
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        try
        {
            excelObject = CreateHiddenExcelApplication();
            dynamic excel = excelObject;
            var excelVersion = Convert.ToString(excel.Version) ?? string.Empty;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath);
            dynamic workbook = workbookObject;
            ExportNestedUserFormFixture(workbookObject, formSourcePath, nonAsciiText);
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;
            return excelVersion;
        }
        finally
        {
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                catch
                {
                }
            }

            ComObjectReleaser.Release(workbookObject);
            ComObjectReleaser.Release(workbooksObject);
            QuitExcel(excelObject);
        }
    }

    private static void ExportNestedUserFormFixture(
        object workbookObject,
        string formSourcePath,
        string nonAsciiText)
    {
        object? projectObject = null;
        object? componentsObject = null;
        object? formComponentObject = null;
        object? codeModuleObject = null;
        object? designerObject = null;
        object? controlsObject = null;
        object? frameObject = null;
        object? frameControlsObject = null;
        object? labelObject = null;
        object? textBoxObject = null;
        try
        {
            dynamic workbook = workbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            formComponentObject = components.Add(3);
            dynamic formComponent = formComponentObject;
            formComponent.Name = "Dialog";
            codeModuleObject = formComponent.CodeModule;
            dynamic codeModule = codeModuleObject;
            codeModule.AddFromString(
                "Option Explicit\r\nPrivate Sub UserForm_Initialize()\r\nEnd Sub\r\n");
            designerObject = formComponent.Designer;
            dynamic designer = designerObject;
            designer.Caption = $"Dialog {nonAsciiText}";
            controlsObject = designer.Controls;
            dynamic controls = controlsObject;
            frameObject = controls.Add("Forms.Frame.1", "FrameMain", true);
            dynamic frame = frameObject;
            frame.Caption = $"Frame {nonAsciiText}";
            frame.Left = 12;
            frame.Top = 12;
            frame.Width = 180;
            frame.Height = 96;
            frameControlsObject = frame.Controls;
            dynamic frameControls = frameControlsObject;
            labelObject = frameControls.Add("Forms.Label.1", "LabelMessage", true);
            dynamic label = labelObject;
            label.Caption = $"Label {nonAsciiText}";
            label.Left = 6;
            label.Top = 12;
            textBoxObject = frameControls.Add("Forms.TextBox.1", "InputText", true);
            dynamic textBox = textBoxObject;
            textBox.Left = 6;
            textBox.Top = 36;
            textBox.Width = 144;
            textBox.Height = 36;
            textBox.MultiLine = true;
            textBox.Value = $"{nonAsciiText}\r\nsidecar-value";
            formComponent.Export(formSourcePath);
        }
        finally
        {
            ComObjectReleaser.Release(textBoxObject);
            ComObjectReleaser.Release(labelObject);
            ComObjectReleaser.Release(frameControlsObject);
            ComObjectReleaser.Release(frameObject);
            ComObjectReleaser.Release(controlsObject);
            ComObjectReleaser.Release(designerObject);
            ComObjectReleaser.Release(codeModuleObject);
            ComObjectReleaser.Release(formComponentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
        }
    }

    private static string ImportAndAssertImmediatelyAndAfterReopen(
        string workbookPath,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string nonAsciiText,
        string artifactDirectory)
    {
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        try
        {
            excelObject = CreateHiddenExcelApplication();
            dynamic excel = excelObject;
            var excelVersion = Convert.ToString(excel.Version) ?? string.Empty;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath);
            ImportAndAssertCurrentWorkbook(
                workbookObject,
                sources,
                activeCodePage,
                nonAsciiText,
                Path.Combine(artifactDirectory, "ImmediateContractClass.cls"));
            dynamic workbook = workbookObject;
            workbook.Save();
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;

            workbookObject = workbooks.Open(workbookPath);
            AssertCurrentWorkbookAfterReopen(
                workbookObject,
                sources,
                activeCodePage,
                nonAsciiText,
                Path.Combine(artifactDirectory, "ReopenedContractClass.cls"));
            workbook = workbookObject;
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;
            return excelVersion;
        }
        finally
        {
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                catch
                {
                }
            }

            ComObjectReleaser.Release(workbookObject);
            ComObjectReleaser.Release(workbooksObject);
            QuitExcel(excelObject);
        }
    }

    private static string OpenAndAssertPersistedWorkbook(
        string workbookPath,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string nonAsciiText,
        string classExportPath)
    {
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        try
        {
            excelObject = CreateHiddenExcelApplication();
            dynamic excel = excelObject;
            var excelVersion = Convert.ToString(excel.Version) ?? string.Empty;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath);
            AssertCurrentWorkbookAfterReopen(
                workbookObject,
                sources,
                activeCodePage,
                nonAsciiText,
                classExportPath);
            dynamic workbook = workbookObject;
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;
            return excelVersion;
        }
        finally
        {
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                catch
                {
                }
            }

            ComObjectReleaser.Release(workbookObject);
            ComObjectReleaser.Release(workbooksObject);
            QuitExcel(excelObject);
        }
    }

    private static void ImportAndAssertCurrentWorkbook(
        object workbookObject,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string nonAsciiText,
        string classExportPath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        try
        {
            dynamic workbook = workbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            foreach (var source in sources)
            {
                object? componentObject = null;
                try
                {
                    componentObject = components.Import(source.SourcePath);
                    AssertImportedComponentProjection(componentObject, source.ImportVerification);
                }
                finally
                {
                    ComObjectReleaser.Release(componentObject);
                }
            }

            AssertClassAttributes(componentsObject, classExportPath, activeCodePage, nonAsciiText);
            AssertNestedFormState(componentsObject, nonAsciiText);
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
        }
    }

    private static void AssertCurrentWorkbookAfterReopen(
        object workbookObject,
        IReadOnlyList<VbeImportSourceFile> sources,
        int activeCodePage,
        string nonAsciiText,
        string classExportPath)
    {
        object? projectObject = null;
        object? componentsObject = null;
        try
        {
            dynamic workbook = workbookObject;
            projectObject = workbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            foreach (var source in sources)
            {
                object? componentObject = null;
                try
                {
                    componentObject = components.Item(source.ImportVerification.ComponentName);
                    AssertImportedComponentProjection(componentObject, source.ImportVerification);
                }
                finally
                {
                    ComObjectReleaser.Release(componentObject);
                }
            }

            AssertClassAttributes(componentsObject, classExportPath, activeCodePage, nonAsciiText);
            AssertNestedFormState(componentsObject, nonAsciiText);
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
        }
    }

    private static void AssertImportedComponentProjection(
        object componentObject,
        VbeImportVerification expected)
        => VbeImportedComponentVerifier.Verify(
            expected,
            CaptureImportedComponent(componentObject));

    private static void AssertClassAttributes(
        object componentsObject,
        string exportPath,
        int activeCodePage,
        string nonAsciiText)
    {
        object? componentObject = null;
        try
        {
            dynamic components = componentsObject;
            componentObject = components.Item("ContractClass");
            dynamic component = componentObject;
            Assert.Equal(2, (int)component.Type);
            component.Export(exportPath);
            var exportedText = DecodeActiveCodePageFile(exportPath, activeCodePage);
            Assert.Contains("Attribute VB_PredeclaredId = True", exportedText, StringComparison.Ordinal);
            Assert.Contains(
                $"Attribute Item.VB_Description = \"Default member {nonAsciiText}\"",
                exportedText,
                StringComparison.Ordinal);
            Assert.Contains("Attribute Item.VB_UserMemId = 0", exportedText, StringComparison.Ordinal);
        }
        finally
        {
            ComObjectReleaser.Release(componentObject);
        }
    }

    private static void AssertNestedFormState(
        object componentsObject,
        string nonAsciiText,
        string componentName = "Dialog")
    {
        object? formComponentObject = null;
        object? designerObject = null;
        object? controlsObject = null;
        object? frameObject = null;
        object? frameControlsObject = null;
        object? labelObject = null;
        object? textBoxObject = null;
        object? labelParentObject = null;
        object? textBoxParentObject = null;
        try
        {
            dynamic components = componentsObject;
            formComponentObject = components.Item(componentName);
            dynamic formComponent = formComponentObject;
            Assert.Equal(3, (int)formComponent.Type);
            designerObject = formComponent.Designer;
            dynamic designer = designerObject;
            Assert.Equal($"Dialog {nonAsciiText}", Convert.ToString(designer.Caption));
            controlsObject = designer.Controls;
            dynamic controls = controlsObject;
            frameObject = controls.Item("FrameMain");
            dynamic frame = frameObject;
            Assert.Contains(
                "Frame",
                Microsoft.VisualBasic.Information.TypeName(frameObject),
                StringComparison.OrdinalIgnoreCase);
            frameControlsObject = frame.Controls;
            dynamic frameControls = frameControlsObject;
            labelObject = frameControls.Item("LabelMessage");
            textBoxObject = frameControls.Item("InputText");
            dynamic label = labelObject;
            dynamic textBox = textBoxObject;
            Assert.Contains(
                "Label",
                Microsoft.VisualBasic.Information.TypeName(labelObject),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Microsoft.VisualBasic.Information.TypeName(textBoxObject),
                new[] { "TextBox", "IMdcText" },
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal($"Label {nonAsciiText}", Convert.ToString(label.Caption));
            Assert.True(Convert.ToBoolean(textBox.MultiLine));
            Assert.Equal($"{nonAsciiText}\r\nsidecar-value", Convert.ToString(textBox.Value));
            labelParentObject = label.Parent;
            textBoxParentObject = textBox.Parent;
            dynamic labelParent = labelParentObject;
            dynamic textBoxParent = textBoxParentObject;
            Assert.Equal("FrameMain", Convert.ToString(labelParent.Name));
            Assert.Equal("FrameMain", Convert.ToString(textBoxParent.Name));
        }
        finally
        {
            ComObjectReleaser.Release(textBoxParentObject);
            ComObjectReleaser.Release(labelParentObject);
            ComObjectReleaser.Release(textBoxObject);
            ComObjectReleaser.Release(labelObject);
            ComObjectReleaser.Release(frameControlsObject);
            ComObjectReleaser.Release(frameObject);
            ComObjectReleaser.Release(controlsObject);
            ComObjectReleaser.Release(designerObject);
            ComObjectReleaser.Release(formComponentObject);
        }
    }

    private static string DecodeActiveCodePageFile(string path, int activeCodePage)
        => StrictEncoding(activeCodePage).GetString(File.ReadAllBytes(path));

    private static string SelectNonAsciiFixtureText(int activeCodePage)
    {
        var encoding = StrictEncoding(activeCodePage);
        foreach (var candidate in new[] { "日本語", "café", "δοκιμή", "тест" })
        {
            try
            {
                var bytes = encoding.GetBytes(candidate);
                if (bytes.Any(value => value >= 0x80) &&
                    encoding.GetString(bytes).Equals(candidate, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (EncoderFallbackException)
            {
            }
        }

        throw new InvalidOperationException(
            $"Active Windows code page {activeCodePage} cannot represent the integration fixture's non-ASCII text.");
    }

    private static Encoding StrictEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return codePage == 65001
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
    }

    private static void QuitExcel(object? excelObject)
    {
        if (excelObject is null)
        {
            return;
        }

        try
        {
            dynamic excel = excelObject;
            excel.Quit();
        }
        finally
        {
            ComObjectReleaser.Release(excelObject);
            ComObjectReleaser.CollectReleasedComObjects();
        }
    }

    private static void CreateEmptyMacroEnabledWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.ms-excel.sheet.macroEnabled.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        WriteEntry(
            archive,
            "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(
            archive,
            "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        WriteEntry(
            archive,
            "xl/worksheets/sheet1.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static IReadOnlySet<int> CaptureExcelProcessIds()
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                try
                {
                    processIds.Add(process.Id);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return processIds;
    }

    private static async Task WaitForProcessSetAsync(
        IReadOnlySet<int> expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CaptureExcelProcessIds().SetEquals(expected))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Equal(expected.Order().ToArray(), CaptureExcelProcessIds().Order().ToArray());
    }

    private static async Task<int> WaitForOwnedExcelProcessAsync(
        IReadOnlySet<int> initialProcesses,
        Task generation,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var newProcesses = CaptureExcelProcessIds().Except(initialProcesses).ToArray();
            if (newProcesses.Length == 1)
            {
                return newProcesses[0];
            }

            if (generation.IsCompleted)
            {
                await generation;
                throw new InvalidOperationException(
                    "Workbook generation completed before an owned Excel process was observed.");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("An owned Excel process was not observed before cancellation.");
    }
}
