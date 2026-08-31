using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
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
            var automation = new ExcelComWorkbookBuildAutomation();
            using var session = automation.OpenWorkbook(
                workbookPath,
                CancellationToken.None);
            var baseline = session.GetReferences().ToArray();

            var result = session.TryResolveReference(
                "Microsoft Scripting Runtime",
                new ResolvedVbaProjectReference(
                    "Microsoft Scripting Runtime",
                    ScriptingGuid,
                    1,
                    0));

            Assert.Equal(VbaProjectReferenceProbeAttemptOutcome.Accepted, result.Outcome);
            Assert.Equal(ScriptingGuid, result.Reference!.Guid);
            Assert.Equal(baseline, session.GetReferences());
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
        var automation = new ExcelComWorkbookBuildAutomation();

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
            output.WriteLine(
                $"VBE import integration: direct Excel {directImportExcelVersion}; production Excel {productionExcelVersion}; " +
                $"active ACP {activeCodePage}; seed Excel {seedExcelVersion}; " +
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

    private static WorkbookGenerationPipeline CreateGenerationPipeline()
        => new(
            (IWorkbookGenerationAutomation)new ExcelComWorkbookBuildAutomation(),
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
            excelObject = CreateHiddenExcelApplication();
            dynamic excel = excelObject;
            var excelVersion = Convert.ToString(excel.Version) ?? string.Empty;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath);
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
            workbook.Close(false);
            ComObjectReleaser.Release(workbookObject);
            workbookObject = null;
            return excelVersion;
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
        string nonAsciiText)
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
            formComponentObject = components.Item("Dialog");
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
