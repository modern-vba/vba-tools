using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookMaterializerTests
{
    [Fact]
    public async Task ProjectInspectionUsesOneDisposableSessionWithoutWritingAWorkbook()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [("Runtime.bas", "Attribute VB_Name = \"Runtime\"\r\n")]);
        var events = new List<string>();
        var materializer = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events)
            {
                OnImport = _ => events.Add("import")
            },
            new ForbiddenTransactionFactory());

        var result = await materializer.InspectAsync(
            fixture.Intent,
            CancellationToken.None);

        Assert.Collection(
            result.Profiles,
            profile =>
            {
                Assert.Equal(ProjectInspectionProfile.Build, profile.Profile);
                Assert.Equal(ProjectInspectionStatus.Pass, profile.Status);
            },
            profile =>
            {
                Assert.Equal(ProjectInspectionProfile.Publish, profile.Profile);
                Assert.Equal(ProjectInspectionStatus.Pass, profile.Status);
            });
        Assert.Equal(1, events.Count(item => item == "open"));
        Assert.DoesNotContain("import", events);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
        Assert.False(Directory.Exists(Path.GetDirectoryName(
            fixture.Intent.Context.BinDocumentPath)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(
            fixture.Intent.Context.PublishDocumentPath)));
    }

    [Fact]
    public async Task ProjectInspectionKeepsBuildAndPublishSourceFailuresIndependent()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [
                ("Runtime.bas", "Attribute VB_Name = \"CollisionName\"\r\n"),
                ("TestOnly.bas", "Attribute VB_Name = \"collisionname\"\r\n")
            ],
            manifest => manifest.Documents["Book1"].CommonModules.Add(
                new InstalledCommonModule(
                    "TestOnly",
                    "TestOnly.bas",
                    Requested: true,
                    TestOnly: true)));
        var events = new List<string>();
        var materializer = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new ForbiddenTransactionFactory());

        var result = await materializer.InspectAsync(
            fixture.Intent,
            CancellationToken.None);

        Assert.Collection(
            result.Profiles,
            build =>
            {
                Assert.Equal(ProjectInspectionProfile.Build, build.Profile);
                Assert.Equal(ProjectInspectionStatus.Fail, build.Status);
                Assert.Contains("CollisionName", build.Message, StringComparison.OrdinalIgnoreCase);
            },
            publish =>
            {
                Assert.Equal(ProjectInspectionProfile.Publish, publish.Profile);
                Assert.Equal(ProjectInspectionStatus.Pass, publish.Status);
            });
        Assert.Equal(1, events.Count(item => item == "open"));
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
    }

    [Fact]
    public async Task ProjectInspectionPreservesProfileEvidenceWhenDisposableCleanupIsUncertain()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [
                ("Runtime.bas", "Attribute VB_Name = \"CollisionName\"\r\n"),
                ("TestOnly.bas", "Attribute VB_Name = \"collisionname\"\r\n")
            ],
            manifest => manifest.Documents["Book1"].CommonModules.Add(
                new InstalledCommonModule(
                    "TestOnly",
                    "TestOnly.bas",
                    Requested: true,
                    TestOnly: true)));
        var stagedWorkbookPath = Path.Combine(temp.Path, "retained", "Book1.xlsm");
        var events = new List<string>();
        var materializer = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new ForbiddenTransactionFactory(),
            inspectionWorkbookStager: templatePath =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stagedWorkbookPath)!);
                File.Copy(templatePath, stagedWorkbookPath);
                return stagedWorkbookPath;
            },
            inspectionWorkbookDeleter: _ =>
                throw new IOException("The staged workbook remained locked."));

        try
        {
            var result = await materializer.InspectAsync(
                fixture.Intent,
                CancellationToken.None);

            Assert.False(result.Complete);
            Assert.False(result.Canceled);
            Assert.Collection(
                result.Profiles,
                build =>
                {
                    Assert.Equal(ProjectInspectionProfile.Build, build.Profile);
                    Assert.Equal(ProjectInspectionStatus.Fail, build.Status);
                    Assert.Contains("CollisionName", build.Message, StringComparison.OrdinalIgnoreCase);
                },
                publish =>
                {
                    Assert.Equal(ProjectInspectionProfile.Publish, publish.Profile);
                    Assert.Equal(ProjectInspectionStatus.Unverified, publish.Status);
                    Assert.Contains(stagedWorkbookPath, publish.Message, StringComparison.Ordinal);
                });
            Assert.True(File.Exists(stagedWorkbookPath));
            Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(stagedWorkbookPath)))
            {
                Directory.Delete(Path.GetDirectoryName(stagedWorkbookPath)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProjectInspectionPreservesSourceFailureWhenItsMirrorCleanupIsUncertain()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [
                ("Runtime.bas", "Attribute VB_Name = \"CollisionName\"\r\n"),
                ("TestOnly.bas", "Attribute VB_Name = \"collisionname\"\r\n")
            ],
            manifest => manifest.Documents["Book1"].CommonModules.Add(
                new InstalledCommonModule(
                    "TestOnly",
                    "TestOnly.bas",
                    Requested: true,
                    TestOnly: true)));
        var events = new List<string>();
        var sourceStagingPaths = new List<string>();
        FileStream? lockedSource = null;
        var materializer = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new ForbiddenTransactionFactory(),
            new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Inspection requested ACP again."),
                sourceSet =>
                {
                    sourceStagingPaths.Add(sourceSet.StagingPath);
                    if (sourceStagingPaths.Count == 1)
                    {
                        lockedSource = File.Open(
                            sourceSet.SourceFiles[0].SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None);
                    }
                }));

        try
        {
            var result = await materializer.InspectAsync(
                fixture.Intent,
                CancellationToken.None);

            Assert.False(result.Complete);
            Assert.False(result.Canceled);
            Assert.Collection(
                result.Profiles,
                build =>
                {
                    Assert.Equal(ProjectInspectionProfile.Build, build.Profile);
                    Assert.Equal(ProjectInspectionStatus.Fail, build.Status);
                    Assert.Contains("CollisionName", build.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(sourceStagingPaths[0], build.Message, StringComparison.Ordinal);
                },
                publish =>
                {
                    Assert.Equal(ProjectInspectionProfile.Publish, publish.Profile);
                    Assert.Equal(ProjectInspectionStatus.Pass, publish.Status);
                    Assert.DoesNotContain(sourceStagingPaths[0], publish.Message, StringComparison.Ordinal);
                });
            Assert.Equal(2, sourceStagingPaths.Count);
            Assert.True(Directory.Exists(sourceStagingPaths[0]));
            Assert.False(Directory.Exists(sourceStagingPaths[1]));
            Assert.Equal(1, events.Count(item => item == "open"));
            Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
        }
        finally
        {
            lockedSource?.Dispose();
            foreach (var stagingPath in sourceStagingPaths.Where(Directory.Exists))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProjectInspectionIsIncompleteWhenFailedWorkbookStagingCannotBeRemoved()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [("Runtime.bas", "Attribute VB_Name = \"Runtime\"\r\n")]);
        var stagingDirectory = Path.Combine(temp.Path, "retained-doctor-stage");
        var stagedWorkbookPath = Path.Combine(
            stagingDirectory,
            Path.GetFileName(fixture.TemplatePath));
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(stagedWorkbookPath, "retained-stage", Encoding.UTF8);
        var sourceStagingPaths = new List<string>();
        var events = new List<string>();
        var deleteAttempts = 0;
        using var lockedWorkbook = File.Open(
            stagedWorkbookPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var materializer = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new ForbiddenTransactionFactory(),
            new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Inspection requested ACP again."),
                sourceSet => sourceStagingPaths.Add(sourceSet.StagingPath)),
            inspectionWorkbookStager: templatePath =>
                WorkbookMaterializer.StageInspectionWorkbook(
                    templatePath,
                    stagingDirectory),
            inspectionWorkbookDeleter: _ => deleteAttempts++);

        var result = await materializer.InspectAsync(
            fixture.Intent,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.False(result.Canceled);
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal(ProjectInspectionStatus.Fail, profile.Status);
            Assert.Contains("could not be removed", profile.Message, StringComparison.Ordinal);
            Assert.Contains(stagingDirectory, profile.Message, StringComparison.Ordinal);
        });
        Assert.Empty(events);
        Assert.Equal(0, deleteAttempts);
        Assert.True(File.Exists(stagedWorkbookPath));
        Assert.Equal(2, sourceStagingPaths.Count);
        Assert.All(sourceStagingPaths, path => Assert.False(Directory.Exists(path)));
        Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
    }

    [Fact]
    public async Task ProjectInspectionReturnsIncompleteEvidenceWhenExcelCleanupIsUncertain()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [("Runtime.bas", "Attribute VB_Name = \"Runtime\"\r\n")]);
        var events = new List<string>();
        var sourceStagingPaths = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            AfterOperationError = new WorkbookAutomationCleanupException(
                "The owned Excel process release could not be proved.")
        };
        var materializer = CreatePipeline(
            automation,
            new ForbiddenTransactionFactory(),
            new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Inspection requested ACP again."),
                sourceSet => sourceStagingPaths.Add(sourceSet.StagingPath)));

        var result = await materializer.InspectAsync(
            fixture.Intent,
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.False(result.Canceled);
        Assert.All(result.Profiles, profile =>
        {
            Assert.Equal(ProjectInspectionStatus.Unverified, profile.Status);
            Assert.Contains("release could not be proved", profile.Message, StringComparison.Ordinal);
        });
        var stagedWorkbookPath = Assert.Single(automation.OpenedWorkbooks);
        Assert.False(File.Exists(stagedWorkbookPath));
        Assert.Equal(2, sourceStagingPaths.Count);
        Assert.All(sourceStagingPaths, path => Assert.False(Directory.Exists(path)));
        Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
        Assert.DoesNotContain("import", events);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
    }

    [Fact]
    public async Task ProjectInspectionPreservesConclusiveFailureWhenAutomationIsCanceled()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateProjectInspectionFixture(
            temp,
            [
                ("Runtime.bas", "Attribute VB_Name = \"CollisionName\"\r\n"),
                ("TestOnly.bas", "Attribute VB_Name = \"collisionname\"\r\n")
            ],
            manifest => manifest.Documents["Book1"].CommonModules.Add(
                new InstalledCommonModule(
                    "TestOnly",
                    "TestOnly.bas",
                    Requested: true,
                    TestOnly: true)));
        var events = new List<string>();
        var sourceStagingPaths = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            BeforeReturn = cancellation.Cancel,
            AfterOperationError = new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                cancellation.Token)
        };
        var materializer = CreatePipeline(
            automation,
            new ForbiddenTransactionFactory(),
            new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Inspection requested ACP again."),
                sourceSet => sourceStagingPaths.Add(sourceSet.StagingPath)));

        var result = await materializer.InspectAsync(
            fixture.Intent,
            cancellation.Token);

        Assert.False(result.Complete);
        Assert.True(result.Canceled);
        Assert.Collection(
            result.Profiles,
            build =>
            {
                Assert.Equal(ProjectInspectionProfile.Build, build.Profile);
                Assert.Equal(ProjectInspectionStatus.Fail, build.Status);
                Assert.Contains("CollisionName", build.Message, StringComparison.OrdinalIgnoreCase);
            },
            publish =>
            {
                Assert.Equal(ProjectInspectionProfile.Publish, publish.Profile);
                Assert.Equal(ProjectInspectionStatus.Unverified, publish.Status);
                Assert.Contains("cancelled", publish.Message, StringComparison.OrdinalIgnoreCase);
            });
        var stagedWorkbookPath = Assert.Single(automation.OpenedWorkbooks);
        Assert.False(File.Exists(stagedWorkbookPath));
        Assert.Equal(2, sourceStagingPaths.Count);
        Assert.All(sourceStagingPaths, path => Assert.False(Directory.Exists(path)));
        Assert.Equal("original-workbook", File.ReadAllText(fixture.TemplatePath, Encoding.UTF8));
        Assert.DoesNotContain("import", events);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
    }

    [Fact]
    public async Task GenerationUsesOneOwnedSessionAndCommitsOnlyAfterCleanupIsProved()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            BeforeReturn = () =>
            {
                Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
                events.Add("cleanup-proved");
            }
        };
        var timeouts = new WorkbookAutomationTimeouts(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6));
        var pipeline = CreatePipeline(automation, baseTimeouts: timeouts);

        await pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            timeouts,
            CancellationToken.None);

        Assert.Equal(timeouts, automation.Timeouts);
        Assert.Equal("new-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Equal(
            [
                "open",
                "get-project-name",
                "get-modules",
                "get-references",
                "get-references",
                "get-project-name",
                "get-modules",
                "get-references",
                "verify",
                "get-project-name",
                "get-modules",
                "get-references",
                "save",
                "cleanup-proved"
            ],
            events);
    }

    [Fact]
    public async Task GenerationDoesNotSaveOrCommitWhenVerificationReturnsNoReport()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        var events = new List<string>();
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events)
            {
                VerificationReport = null
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.MaterializeSourceSnapshotAsync(
                "Book1",
                templatePath,
                targetPath,
                [],
                [],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None));

        Assert.Contains("verification report", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task SourceSnapshotCaptureIsReleasedAfterImportMirrorCreationAndBeforeOutputOrExcelStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        BuildSourceSnapshotCapture? sourceCapture = null;
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new RecordingTransactionFactory(
                events,
                () => Assert.False(Directory.Exists(sourceCapture!.StagingPath))),
            new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Import mirror requested ACP again."),
                _ =>
                {
                    Assert.True(Directory.Exists(sourceCapture!.StagingPath));
                    events.Add("mirror-created");
                }));

        await pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None,
            captureCreated: capture => sourceCapture = capture);

        Assert.True(File.Exists(sourcePath));
        Assert.False(Directory.Exists(sourceCapture!.StagingPath));
        Assert.Equal(
            [
                "mirror-created",
                "transaction-create",
                "open",
                "get-project-name",
                "get-modules",
                "get-references",
                "get-references",
                "get-project-name",
                "get-modules",
                "get-references",
                "verify",
                "get-project-name",
                "get-modules",
                "get-references",
                "save"
            ],
            events);
    }

    [Fact]
    public async Task SourceSnapshotCaptureCleanupFailureStopsBeforeOutputOrExcelStarts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation(events),
            new RecordingTransactionFactory(events));
        FileStream? sourceLock = null;
        string? captureStagingPath = null;

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pipeline.MaterializeSourceSnapshotAsync(
                    "Book1",
                    templatePath,
                    targetPath,
                    [],
                    [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                    WorkbookAutomationTimeouts.Default,
                    CancellationToken.None,
                    captureCreated: capture =>
                    {
                        captureStagingPath = capture.StagingPath;
                        sourceLock = File.Open(
                            Assert.Single(capture.SourceFiles).SourcePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.None);
                    }));

            Assert.Contains("could not be removed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(events);
            Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
        }
        finally
        {
            sourceLock?.Dispose();
            if (captureStagingPath is not null && Directory.Exists(captureStagingPath))
            {
                Directory.Delete(captureStagingPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MissingAmbiguousReferenceIsProbedAgainstTheCleanedOpenWorkbook()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        var events = new List<string>();
        var resolvedIdentity = new ResolvedVbaProjectReference(
            "Ambiguous Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            2,
            0);
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            Modules = [new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule)],
            FinalModules = [],
            References =
            [
                new WorkbookReference(
                    "Old Library",
                    IsRemovable: true,
                    NamespaceName: "OldNamespace")
            ],
            FinalReferences =
            [
                new WorkbookReference(
                    "Ambiguous Library",
                    IsRemovable: true,
                    NamespaceName: "AdoptedNamespace")
            ],
            OnRemoveModule = moduleName => events.Add($"remove-module:{moduleName}"),
            OnRemoveReference = referenceName => events.Add($"remove-reference:{referenceName}"),
            OnReferenceProbe = (_, candidate) =>
            {
                events.Add($"probe-reference:{candidate.Guid}");
                return VbaProjectReferenceProbeAttemptResult.Accepted(resolvedIdentity);
            }
        };
        var probe = new RecordingBuildAmbiguityProbe(resolvedIdentity);
        var pipeline = new WorkbookMaterializer(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(
                    new FakeVbaProjectReferenceResolver(
                        resolvedIdentity,
                        new ResolvedVbaProjectReference(
                            "Ambiguous Library",
                            "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                            3,
                            0)),
                    probe)));

        await pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [new VbaProjectReference("Ambiguous Library")],
            [],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None);

        Assert.Empty(probe.BaselineWorkbookPaths);
        Assert.Equal([resolvedIdentity], automation.AddedReferences);
        Assert.True(
            events.IndexOf("remove-module:OldModule") <
            events.IndexOf("remove-reference:Old Library"));
        Assert.True(
            events.IndexOf("remove-reference:Old Library") <
            events.IndexOf($"probe-reference:{resolvedIdentity.Guid}"));
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task CancellationBeforeCommitIdentifiesOutputCommitAndPreservesPreviousOutput()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        var automation = new RecordingWorkbookGenerationAutomation([])
        {
            BeforeReturn = cancellation.Cancel
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(() =>
            pipeline.MaterializeSourceSnapshotAsync(
                "Book1",
                templatePath,
                targetPath,
                [],
                [],
                WorkbookAutomationTimeouts.Default,
                cancellation.Token));

        Assert.Equal(WorkbookAutomationStageKind.OutputCommit, error.Stage.Kind);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task CancellationThatArrivesInsideSuccessfulCommitDoesNotOverrideSuccess()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        var transactionFactory = new CancelAfterCommitTransactionFactory(cancellation);
        var pipeline = CreatePipeline(
            new RecordingWorkbookGenerationAutomation([]),
            transactionFactory);

        var result = await pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default,
            cancellation.Token);

        Assert.Empty(result.Warnings);
        Assert.Equal(Path.GetFullPath(targetPath), result.CommittedArtifactPath);
        Assert.Equal(0, result.ImportedSourceCount);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("new-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task CleanupFailureRetainsTheStageSpecificOperationFailure()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        var timeout = new WorkbookAutomationTimeoutException(
            new WorkbookAutomationStage(
                WorkbookAutomationStageKind.ModuleImport,
                "Feature.bas"),
            TimeSpan.FromSeconds(30));
        var pipeline = CreatePipeline(
            new ThrowingWorkbookGenerationAutomation(timeout),
            new CleanupFailureTransactionFactory());

        var error = await Assert.ThrowsAsync<BuildCommandException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("module import 'Feature.bas'", error.Message, StringComparison.Ordinal);
        Assert.Contains("retained staging", error.Message, StringComparison.Ordinal);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Same(timeout, aggregate.InnerExceptions[0]);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task SourceIdentityConflictFailsBeforeOwnedExcelOrOutputStagingStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var firstSourcePath = Path.Combine(temp.Path, "First.bas");
        var secondSourcePath = Path.Combine(temp.Path, "Second.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            firstSourcePath,
            "Attribute VB_Name = \"SharedName\"\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            secondSourcePath,
            "Attribute VB_Name = \"sharedname\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var pipeline = CreatePipeline(new RecordingWorkbookGenerationAutomation(events));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [
                new VbaSourceFile(firstSourcePath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(secondSourcePath, VbaSourceKind.StandardModule, null)
            ],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("SharedName", error.Message, StringComparison.Ordinal);
        Assert.Contains(firstSourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(secondSourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task MissingAuthoritativeSourceIdentityFailsBeforeOwnedExcelOrOutputStagingStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "FallbackOnly.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Public Sub Run()\r\nEnd Sub\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var pipeline = CreatePipeline(new RecordingWorkbookGenerationAutomation(events));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("authoritative ModuleIdentity", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task StaticPreflightReportsEveryInvalidIdentityAndProvableConflictInSourceOrder()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var conflictPath = Path.Combine(temp.Path, "AFirst.bas");
        var caseConflictPath = Path.Combine(temp.Path, "BSecond.bas");
        var misplacedPath = Path.Combine(temp.Path, "CThird.bas");
        var missingPath = Path.Combine(temp.Path, "DFourth.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            misplacedPath,
            "Public Sub Run()\r\nEnd Sub\r\nAttribute VB_Name = \"Misplaced\"\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            missingPath,
            "Public Sub Run()\r\nEnd Sub\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            conflictPath,
            "Attribute VB_Name = \"CollisionName\"\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            caseConflictPath,
            "Attribute VB_Name = \"collisionname\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var pipeline = CreatePipeline(new RecordingWorkbookGenerationAutomation(events));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [
                new VbaSourceFile(conflictPath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(misplacedPath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(caseConflictPath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(missingPath, VbaSourceKind.StandardModule, null)
            ],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        var misplacedIndex = error.Message.IndexOf(misplacedPath, StringComparison.OrdinalIgnoreCase);
        var missingIndex = error.Message.IndexOf(missingPath, StringComparison.OrdinalIgnoreCase);
        var conflictIndex = error.Message.IndexOf("Source identity 'CollisionName'", StringComparison.Ordinal);
        var conflictSourceIndex = error.Message.IndexOf(conflictPath, conflictIndex, StringComparison.OrdinalIgnoreCase);
        var caseConflictSourceIndex = error.Message.IndexOf(caseConflictPath, conflictIndex, StringComparison.OrdinalIgnoreCase);
        Assert.True(conflictIndex >= 0);
        Assert.True(conflictSourceIndex > conflictIndex);
        Assert.True(caseConflictSourceIndex > conflictSourceIndex);
        Assert.True(misplacedIndex > caseConflictSourceIndex);
        Assert.True(missingIndex > misplacedIndex);
        Assert.Empty(events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task MisplacedObjectIdentityFailsBeforeOwnedExcelOrOutputStagingStarts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Worker.cls");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            string.Join("\r\n", [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1  'True",
                "END",
                "Attribute VB_Name = \"Worker\"",
                "Attribute VB_Exposed = False",
                "Option Explicit",
                "Attribute VB_Name = \"Misplaced\"",
                string.Empty
            ]),
            new UTF8Encoding(false));
        var events = new List<string>();
        var pipeline = CreatePipeline(new RecordingWorkbookGenerationAutomation(events));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.ClassModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("invalid ModuleIdentity metadata", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task RetainedComponentConflictFailsBeforeSourceImportOrSave()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"thisworkbook\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            Modules = [new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document)]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("retained component 'ThisWorkbook'", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task IncompleteRetainedComponentIdentityFailsBeforeSourceImportOrSave()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Incoming\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            Modules = [new WorkbookModule(" ", WorkbookModuleKind.Document)]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("retained component identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task ExactCodePageRetainedComponentIdentityParticipatesInNamespaceConflicts()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"\u00A0\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            Modules = [new WorkbookModule("\u00A0", WorkbookModuleKind.Document)]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("conflicts with retained component '\u00A0'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("identity at index 0 is incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
    }

    [Fact]
    public async Task ContainingProjectConflictUsesTheActualTemporaryWorkbookName()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"actualproject\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            ProjectName = "ActualProject"
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "ManifestDocumentLabel",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("containing project 'ActualProject'", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task ConclusiveProjectConflictFailsBeforeReferenceNormalization()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"ActualProject\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            ProjectName = "ActualProject"
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [new VbaProjectReference("Missing Library")],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("containing project 'ActualProject'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Missing Library", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task FinalPreflightReinspectsProjectIdentityAfterReferenceNormalization()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Incoming\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            ProjectName = "InitialProject",
            FinalProjectName = "Incoming"
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("containing project 'Incoming'", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, events.Count(item => item == "get-project-name"));
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task FinalPreflightTreatsEverySurvivingComponentAsCollisionAuthority()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Incoming\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var imported = false;
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            FinalModules = [new WorkbookModule("Incoming", WorkbookModuleKind.StandardModule)],
            OnImport = _ => imported = true
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("retained component 'Incoming'", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, events.Count(item => item == "get-modules"));
        Assert.False(imported);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task IncompleteContainingProjectIdentityFailsBeforeSourceImportOrSave()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Incoming\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            ProjectName = " "
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("containing project identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task IncompleteLiveAuthorityDoesNotSuppressConflictsProvedByCompleteSiblings()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var retainedSourcePath = Path.Combine(temp.Path, "Retained.bas");
        var referenceSourcePath = Path.Combine(temp.Path, "Reference.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            retainedSourcePath,
            "Attribute VB_Name = \"ThisWorkbook\"\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            referenceSourcePath,
            "Attribute VB_Name = \"ActualReference\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            ProjectName = " ",
            Modules =
            [
                new WorkbookModule(" ", WorkbookModuleKind.Document),
                new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document)
            ],
            References =
            [
                new WorkbookReference(
                    "Incomplete reference description",
                    IsRemovable: false,
                    NamespaceName: " "),
                new WorkbookReference(
                    "Friendly reference description",
                    IsRemovable: false,
                    NamespaceName: "ActualReference")
            ]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [
                new VbaSourceFile(retainedSourcePath, VbaSourceKind.StandardModule, null),
                new VbaSourceFile(referenceSourcePath, VbaSourceKind.StandardModule, null)
            ],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("containing project identity is incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retained component identity at index 0 is incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active reference identity is incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retained component 'ThisWorkbook'", error.Message, StringComparison.Ordinal);
        Assert.Contains("active reference 'ActualReference'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task ProtectedReferenceConflictUsesItsFinalActualNamespaceName()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"protectednamespace\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            References =
            [
                new WorkbookReference(
                    "Protected Library Description",
                    IsRemovable: false,
                    NamespaceName: "ProtectedNamespace")
            ]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("active reference 'ProtectedNamespace'", error.Message, StringComparison.Ordinal);
        Assert.Contains(sourcePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(events, item => item == "get-references");
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task IncompleteFinalReferenceIdentityFailsBeforeSourceImportOrSave()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Incoming\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            FinalReferences =
            [
                new WorkbookReference(
                    "Incomplete Library Description",
                    IsRemovable: false,
                    NamespaceName: " ")
            ]
        };
        var pipeline = CreatePipeline(automation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("active reference identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task AddedReferenceConflictUsesTheNamespaceNameAdoptedByVbe()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Incoming.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"adoptednamespace\"\r\n",
            new UTF8Encoding(false));
        var events = new List<string>();
        var resolvedReference = new ResolvedVbaProjectReference(
            "Friendly Library Description",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            1,
            0);
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            FinalReferences =
            [
                new WorkbookReference(
                    "Friendly Library Description",
                    IsRemovable: true,
                    NamespaceName: "AdoptedNamespace")
            ]
        };
        var pipeline = new WorkbookMaterializer(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(
                    new FakeVbaProjectReferenceResolver(resolvedReference))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [new VbaProjectReference("Friendly Library Description")],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None));

        Assert.Contains("active reference 'AdoptedNamespace'", error.Message, StringComparison.Ordinal);
        Assert.Equal([resolvedReference], automation.AddedReferences);
        Assert.Equal(3, events.Count(item => item == "get-references"));
        Assert.DoesNotContain("verify", events);
        Assert.DoesNotContain("save", events);
        Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
    }

    [Fact]
    public async Task SourceSnapshotBuildUsesCapturedAdmissionAfterCallerSourceChanges()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        const string admittedText =
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub AdmittedCode()\r\nEnd Sub\r\n";
        const string laterCallerText =
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub LaterCallerCode()\r\nEnd Sub\r\n";
        File.WriteAllText(sourcePath, admittedText, new UTF8Encoding(false, true));
        var events = new List<string>();
        VbeImportSourceFile? importedSource = null;
        var pipeline = CreatePipeline(new RecordingWorkbookGenerationAutomation(events)
        {
            OnImport = source => importedSource = source
        });

        var result = await pipeline.MaterializeSourceSnapshotAsync(
            "Book1",
            templatePath,
            targetPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            WorkbookAutomationTimeouts.Default,
            CancellationToken.None,
            captureCreated: _ =>
            {
                File.WriteAllText(
                    sourcePath,
                    laterCallerText,
                    new UTF8Encoding(false, true));
            });

        Assert.Empty(result.Warnings);
        Assert.Equal("new-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Equal(laterCallerText, File.ReadAllText(sourcePath, new UTF8Encoding(false, true)));
        Assert.NotNull(importedSource);
        Assert.Contains("Public Sub AdmittedCode()", importedSource.ImportVerification.CodeModuleLines);
        Assert.DoesNotContain("Public Sub LaterCallerCode()", importedSource.ImportVerification.CodeModuleLines);
        Assert.Equal(sourcePath, importedSource.DiagnosticSourcePath);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".Book1.*.tmp.xlsm"));
    }

    [Fact]
    public async Task ImportMirrorCleanupFailurePreventsFinalOutputCommit()
    {
        using var temp = TempDirectory.Create();
        var templatePath = Path.Combine(temp.Path, "Template.xlsm");
        var targetPath = Path.Combine(temp.Path, "Book1.xlsm");
        var sourcePath = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(templatePath, "new-workbook", Encoding.UTF8);
        File.WriteAllText(targetPath, "previous-workbook", Encoding.UTF8);
        File.WriteAllText(
            sourcePath,
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        FileStream? stagingLock = null;
        string? importStagingPath = null;
        var events = new List<string>();
        var automation = new RecordingWorkbookGenerationAutomation(events)
        {
            OnImport = source =>
            {
                importStagingPath = Path.GetDirectoryName(source.SourcePath);
                stagingLock = File.Open(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
        };
        var pipeline = CreatePipeline(automation);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.MaterializeSourceSnapshotAsync(
                "Book1",
                templatePath,
                targetPath,
                [],
                [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None));

            Assert.Contains("could not be removed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("save", events);
            Assert.Equal("previous-workbook", File.ReadAllText(targetPath, Encoding.UTF8));
        }
        finally
        {
            stagingLock?.Dispose();
            if (importStagingPath is not null && Directory.Exists(importStagingPath))
            {
                Directory.Delete(importStagingPath, recursive: true);
            }
        }
    }

    private static WorkbookMaterializer CreatePipeline(
        IWorkbookGenerationAutomation automation,
        IWorkbookOutputTransactionFactory? transactionFactory = null,
        VbeImportSourceSetFactory? importSourceSetFactory = null,
        WorkbookAutomationTimeouts? baseTimeouts = null,
        Func<string, string>? inspectionWorkbookStager = null,
        Action<string>? inspectionWorkbookDeleter = null)
        => new(
            new WorkbookSourcePlanner(() => 65001),
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())),
            transactionFactory ?? new WorkbookOutputTransactionFactory(),
            importSourceSetFactory ?? new VbeImportSourceSetFactory(
                () => throw new InvalidOperationException("Import mirror requested ACP again.")),
            baseTimeouts,
            inspectionWorkbookStager,
            inspectionWorkbookDeleter);

    private static ProjectInspectionFixture CreateProjectInspectionFixture(
        TempDirectory temp,
        IReadOnlyList<(string FileName, string Content)> sources,
        Action<ProjectManifest>? configureManifest = null)
    {
        var projectRoot = temp.CreateDirectory($"Project-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(projectRoot, "src", "Book1");
        var templatePath = Path.Combine(sourceDirectory, "Book1.xlsm");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(templatePath, "original-workbook", Encoding.UTF8);
        foreach (var source in sources)
        {
            File.WriteAllText(
                Path.Combine(sourceDirectory, source.FileName),
                source.Content,
                new UTF8Encoding(false));
        }

        var manifest = ProjectManifest.CreateDefault(
            "Project",
            "Book1",
            projectRoot,
            commonModulesRepositoryPath: null);
        configureManifest?.Invoke(manifest);
        var document = manifest.Documents["Book1"];
        var context = new ResolvedProjectContext(
            projectRoot,
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            document,
            sourceDirectory,
            templatePath,
            Path.Combine(projectRoot, "bin", "Book1.xlsm"),
            Path.Combine(projectRoot, "publish", "Book1.xlsm"),
            CommonModulesRepositoryPath: null);
        var sourceCapture = new VbaSourceAdmission(() => 65001)
            .BeginDoctorRun()
            .CaptureDocument(sourceDirectory);
        return new ProjectInspectionFixture(
            new ProjectInspectionIntent(context, sourceCapture),
            templatePath);
    }

    private sealed record ProjectInspectionFixture(
        ProjectInspectionIntent Intent,
        string TemplatePath);

    private sealed class RecordingWorkbookGenerationAutomation(
        List<string> events) : IWorkbookGenerationAutomation
    {
        public Action? BeforeReturn { get; init; }

        public Exception? AfterOperationError { get; init; }

        public Action<VbeImportSourceFile>? OnImport { get; init; }

        public Action<string>? OnRemoveModule { get; init; }

        public Action<string>? OnRemoveReference { get; init; }

        public Func<string, ResolvedVbaProjectReference, VbaProjectReferenceProbeAttemptResult>?
            OnReferenceProbe
        { get; init; }

        public WorkbookAutomationTimeouts? Timeouts { get; private set; }

        public List<string> OpenedWorkbooks { get; } = [];

        public List<ResolvedVbaProjectReference> AddedReferences { get; } = [];

        public IReadOnlyList<WorkbookModule> Modules { get; init; } = [];

        public IReadOnlyList<WorkbookModule>? FinalModules { get; init; }

        public string ProjectName { get; init; } = "VbaProject";

        public string? FinalProjectName { get; init; }

        public IReadOnlyList<WorkbookReference> References { get; init; } = [];

        public IReadOnlyList<WorkbookReference>? FinalReferences { get; init; }

        public VbeImportVerificationReport? VerificationReport { get; init; } =
            VbeImportVerificationReport.Empty;

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            events.Add("open");
            OpenedWorkbooks.Add(workbookPath);
            Timeouts = timeouts;
            var result = await operation(
                new RecordingWorkbookGenerationSession(
                    events,
                    AddedReferences,
                    Modules,
                    FinalModules,
                    ProjectName,
                    FinalProjectName,
                    References,
                    FinalReferences,
                    VerificationReport,
                    OnImport,
                    OnRemoveModule,
                    OnRemoveReference,
                    OnReferenceProbe),
                cancellationToken);
            BeforeReturn?.Invoke();
            if (AfterOperationError is not null)
            {
                throw AfterOperationError;
            }

            return result;
        }
    }

    private sealed class RecordingWorkbookGenerationSession(
        List<string> events,
        List<ResolvedVbaProjectReference> addedReferences,
        IReadOnlyList<WorkbookModule> modules,
        IReadOnlyList<WorkbookModule>? finalModules,
        string projectName,
        string? finalProjectName,
        IReadOnlyList<WorkbookReference> references,
        IReadOnlyList<WorkbookReference>? finalReferences,
        VbeImportVerificationReport? verificationReport,
        Action<VbeImportSourceFile>? onImport = null,
        Action<string>? onRemoveModule = null,
        Action<string>? onRemoveReference = null,
        Func<string, ResolvedVbaProjectReference, VbaProjectReferenceProbeAttemptResult>?
            onReferenceProbe = null) : IWorkbookGenerationSession
    {
        private int moduleReads;
        private int projectNameReads;
        private int referenceReads;

        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
        {
            events.Add("get-project-name");
            projectNameReads++;
            return Task.FromResult(
                projectNameReads > 1 && finalProjectName is not null
                    ? finalProjectName
                    : projectName);
        }

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
        {
            events.Add("get-modules");
            moduleReads++;
            return Task.FromResult(
                moduleReads > 1 && finalModules is not null
                    ? finalModules
                    : modules);
        }

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
        {
            events.Add("get-references");
            referenceReads++;
            return Task.FromResult(
                referenceReads > 2 && finalReferences is not null
                    ? finalReferences
                    : references);
        }

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
        {
            onRemoveReference?.Invoke(referenceName);
            return Task.FromResult(true);
        }

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
        {
            addedReferences.Add(reference);
            return Task.CompletedTask;
        }

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
        {
            onRemoveModule?.Invoke(moduleName);
            return Task.CompletedTask;
        }

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName,
            ResolvedVbaProjectReference candidate,
            CancellationToken cancellationToken)
            => Task.FromResult(onReferenceProbe?.Invoke(referenceName, candidate)
                ?? throw new NotSupportedException(
                    "This recording session was not configured for reference probing."));

        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
        {
            onImport?.Invoke(sourceFile);
            return Task.CompletedTask;
        }

        public Task ExportModuleAsync(
            string moduleName,
            string destinationPath,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
        {
            events.Add("verify");
            return Task.FromResult(verificationReport!);
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            events.Add("save");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBuildAmbiguityProbe(
        ResolvedVbaProjectReference resolvedIdentity)
        : IVbaProjectReferenceAmbiguityProbe
    {
        public List<string> BaselineWorkbookPaths { get; } = [];

        public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
            VbaProjectReferenceProbeBaseline baseline,
            VbaProjectReferenceResolutionBatch registryResolution,
            CancellationToken cancellationToken)
        {
            BaselineWorkbookPaths.Add(baseline.WorkbookPath!);
            return Task.FromResult(registryResolution with
            {
                References = registryResolution.References
                    .Select(reference => reference with
                    {
                        Matches = [resolvedIdentity],
                        Candidates = [resolvedIdentity]
                    })
                    .ToArray()
            });
        }
    }

    private sealed class CancelAfterCommitTransactionFactory(
        CancellationTokenSource cancellation) : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(string templateWorkbookPath, string targetWorkbookPath)
            => new CancelAfterCommitTransaction(
                WorkbookOutputTransaction.Create(templateWorkbookPath, targetWorkbookPath),
                cancellation);
    }

    private sealed class CancelAfterCommitTransaction(
        WorkbookOutputTransaction inner,
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

    private sealed class RecordingTransactionFactory(
        List<string> events,
        Action? beforeCreate = null) : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(
            string templateWorkbookPath,
            string targetWorkbookPath)
        {
            beforeCreate?.Invoke();
            events.Add("transaction-create");
            return WorkbookOutputTransaction.Create(
                templateWorkbookPath,
                targetWorkbookPath);
        }
    }

    private sealed class ForbiddenTransactionFactory : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(
            string templateWorkbookPath,
            string targetWorkbookPath)
            => throw new InvalidOperationException(
                "Project inspection must not create a committable output transaction.");
    }

    private sealed class ThrowingWorkbookGenerationAutomation(
        Exception error) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => Task.FromException<TResult>(error);
    }

    private sealed class CleanupFailureTransactionFactory : IWorkbookOutputTransactionFactory
    {
        public IWorkbookOutputTransaction Create(
            string templateWorkbookPath,
            string targetWorkbookPath)
            => new CleanupFailureTransaction(
                WorkbookOutputTransaction.Create(templateWorkbookPath, targetWorkbookPath));
    }

    private sealed class CleanupFailureTransaction(
        WorkbookOutputTransaction inner) : IWorkbookOutputTransaction
    {
        public string StagingWorkbookPath => inner.StagingWorkbookPath;

        public void Commit() => inner.Commit();

        public void Dispose()
            => throw new BuildCommandException("retained staging requires manual cleanup");
    }
}
