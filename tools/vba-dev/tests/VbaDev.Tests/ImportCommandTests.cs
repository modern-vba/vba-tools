using System.Text;
using VbaDev.App.Import;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class ImportCommandTests
{
    [Fact]
    public async Task ImportCommandDoesNotRequireProjectContextAndSavesAfterSuccessfulImport()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(
            new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule),
            new WorkbookModule("OldClass", WorkbookModuleKind.ClassModule),
            new WorkbookModule("OldForm", WorkbookModuleKind.Form),
            new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document),
            new WorkbookModule("Sheet1", WorkbookModuleKind.Document),
            new WorkbookModule("Other", WorkbookModuleKind.Other));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                workbookBuildAutomation: automation));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            ["import", "--from", sourceDirectory, "--to", targetWorkbook],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Imported 1 source file", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(sourceDirectory, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(targetWorkbook, standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Empty(standardError.ToString());
        Assert.Equal([targetWorkbook], automation.OpenedWorkbooks);
        Assert.Equal(
            [
                "remove:OldModule",
                "remove:OldClass",
                "remove:OldForm",
                "import:Module1.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public async Task ImportStdinCancellationCanCancelWhileOwnedWorkbookIsOpening()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation
        {
            WaitForCancellationOnOpen = true
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookBuildAutomation: automation);
        using var standardInput = new SignalThenFrameStream(
            automation.CancelableOpenStarted,
            "cancel\n"u8.ToArray());
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            [
                "import",
                "--from",
                sourceDirectory,
                "--to",
                targetWorkbook,
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(130, exitCode);
        Assert.False(automation.CancellationRequestedAtOpen);
        Assert.True(automation.CancellationObserved);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public async Task ImportStdinCancellationUsesNativeBoundedGenerationAndWaitsForCleanup()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeNativeImportGenerationAutomation();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookBuildAutomation: automation);
        using var standardInput = new MemoryStream("cancel\n"u8.ToArray());
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            [
                "import",
                "--from",
                sourceDirectory,
                "--to",
                targetWorkbook,
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(130, exitCode);
        Assert.Equal(1, automation.GenerationRuns);
        Assert.Equal(0, automation.LegacyOpenCalls);
        Assert.Equal(WorkbookAutomationTimeouts.Default, automation.Timeouts);
        Assert.True(automation.CancellationObserved);
        Assert.True(automation.CleanupFinished);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public async Task ImportStdinCancellationDoesNotReturn130WhenCleanupProofFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeNativeImportGenerationAutomation
        {
            FailCleanupProof = true
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            workbookBuildAutomation: automation);
        using var standardInput = new MemoryStream("cancel\n"u8.ToArray());
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            [
                "import",
                "--from",
                sourceDirectory,
                "--to",
                targetWorkbook,
                "--cancellation-transport",
                "stdin-v1"
            ],
            standardInput,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, automation.GenerationRuns);
        Assert.Equal(0, automation.LegacyOpenCalls);
        Assert.True(automation.CancellationObserved);
        Assert.True(automation.CleanupFinished);
        Assert.Contains("could not be verified", standardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void ImportCommandResolvesRelativePathsFromWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("work");
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        var targetWorkbook = Path.Combine(workingDirectory, "target.xlsm");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(workingDirectory, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", "src", "--to", "target.xlsm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([targetWorkbook], automation.OpenedWorkbooks);
        var importedSource = Assert.Single(automation.ImportedSources);
        Assert.Equal("Module1.bas", importedSource.FileName);
        Assert.NotEqual(Path.Combine(sourceDirectory, "Module1.bas"), importedSource.SourcePath);
        Assert.False(File.Exists(importedSource.SourcePath));
    }

    [Fact]
    public void ImportCommandSelectsRecursiveSourcesInStableNameOrderAndPairsFormFrx()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        WriteText(Path.Combine(sourceDirectory, "z", "Zeta.cls"), "VERSION 1.0 CLASS");
        WriteText(Path.Combine(sourceDirectory, "forms", "Dialog.frm"), "VERSION 5.00");
        WriteBytes(Path.Combine(sourceDirectory, "forms", "Dialog.frx"), [1, 2, 3]);
        WriteText(Path.Combine(sourceDirectory, "Alpha.bas"), "Attribute VB_Name = \"Alpha\"");
        WriteBytes(Path.Combine(sourceDirectory, "nested", "Orphan.frx"), [9, 9, 9]);
        WriteText(Path.Combine(sourceDirectory, "nested", "Nested.bas"), "Attribute VB_Name = \"Nested\"");
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Alpha.bas", "Dialog.frm", "Nested.bas", "Zeta.cls"], automation.ImportedSources.Select(source => source.FileName));
        var importedForm = Assert.Single(automation.ImportedSources, source => source.Kind == VbaSourceKind.Form);
        Assert.NotNull(importedForm.BinaryPath);
        Assert.Equal(Path.GetDirectoryName(importedForm.SourcePath), Path.GetDirectoryName(importedForm.BinaryPath));
        Assert.Equal("Dialog.frx", Path.GetFileName(importedForm.BinaryPath));
        Assert.False(File.Exists(importedForm.BinaryPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(sourceDirectory, "forms", "Dialog.frx")));
    }

    [Fact]
    public void ImportCommandFailsBeforeOpeningWorkbookWhenRecursiveSourceNamesCollide()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        WriteText(Path.Combine(sourceDirectory, "first", "Shared.bas"), "Attribute VB_Name = \"Shared\"");
        WriteText(Path.Combine(sourceDirectory, "second", "shared.bas"), "Attribute VB_Name = \"shared\"");
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Shared.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("first", "Shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("second", "shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void ImportCommandDoesNotApplyBuildOrPublishManifestBehavior()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Excluded.bas"),
            "'#ExcludePublish\nAttribute VB_Name = \"Excluded\"",
            Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true));
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Excluded.bas", "save"], automation.Events);
        Assert.Contains("Unlisted Library", automation.References.Select(reference => reference.Name));
    }

    [Theory]
    [InlineData(new[] { "import", "--to", "target.xlsm" }, "--from is required.")]
    [InlineData(new[] { "import", "--from", "src" }, "--to is required.")]
    [InlineData(new[] { "import", "--from=", "--to", "target.xlsm" }, "target.xlsm")]
    [InlineData(new[] { "import", "--from", "src", "--to=" }, "--to")]
    [InlineData(new[] { "import", "-f", "src", "--to", "target.xlsm" }, "-f")]
    [InlineData(new[] { "import", "--from", "src", "-t", "target.xlsm" }, "-t")]
    [InlineData(new[] { "import", "--from", "src", "--to", "target.xlsm", "--project", "." }, "--project")]
    [InlineData(new[] { "import", "--from", "src", "--to", "target.xlsm", "--document", "Book1" }, "--document")]
    public void ImportCommandRejectsInvalidArgumentContract(string[] args, string expectedError)
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportCommandRejectsInvalidSourceAndTargetPathsBeforeOpeningWorkbook()
    {
        using var temp = TempDirectory.Create();
        var validSource = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        var sourceFile = Path.Combine(temp.Path, "Source.bas");
        var targetDirectory = temp.CreateDirectory("target-dir");
        File.WriteAllText(Path.Combine(validSource, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(sourceFile, "Attribute VB_Name = \"Source\"", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var missingSource = application.Run(["import", "--from", Path.Combine(temp.Path, "missing-src"), "--to", targetWorkbook]);
        var fileSource = application.Run(["import", "--from", sourceFile, "--to", targetWorkbook]);
        var missingTarget = application.Run(["import", "--from", validSource, "--to", Path.Combine(temp.Path, "missing.xlsm")]);
        var directoryTarget = application.Run(["import", "--from", validSource, "--to", targetDirectory]);

        Assert.Equal(1, missingSource.ExitCode);
        Assert.Contains("Import source directory was not found", missingSource.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, fileSource.ExitCode);
        Assert.Contains($"Import source path is not a directory: {sourceFile}", fileSource.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, missingTarget.ExitCode);
        Assert.Contains("Import target workbook was not found", missingTarget.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, directoryTarget.ExitCode);
        Assert.Contains($"Import target workbook is not a file: {targetDirectory}", directoryTarget.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void ImportCommandFailsBeforeOpeningWorkbookWhenNoImportableSourcesExist()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "notes.txt"), "notes", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "Orphan.frx"), [1, 2, 3]);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"No importable VBA source files were found in: {sourceDirectory}", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void ImportCommandDoesNotSaveWhenFlushFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule))
        {
            ThrowOnRemove = true
        };
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("remove failed", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void ImportCommandDoesNotSaveWhenImportFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule))
        {
            ThrowOnImport = true
        };
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("remove:OldModule", automation.Events);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void ImportCommandRejectsLossySourceBeforeOpeningExcelAndPreservesCallerFiles()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var sourcePath = Path.Combine(sourceDirectory, "Lossy.bas");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        const string sourceText = "Attribute VB_Name = \"Lossy\"\r\nPublic Const Minus As String = \"−\"\r\n";
        var sourceBytes = new UTF8Encoding(false, true).GetBytes(sourceText);
        byte[] targetBytes = [1, 2, 3, 4];
        File.WriteAllBytes(sourcePath, sourceBytes);
        File.WriteAllBytes(targetWorkbook, targetBytes);
        var automation = new FakeWorkbookBuildAutomation();
        var codePageReads = 0;
        var command = new ImportCommand(
            automation,
            new VbeImportSourceSetFactory(() =>
            {
                codePageReads++;
                return 1252;
            }));

        var result = command.Run(new ImportCommandRequest(
            sourceDirectory,
            targetWorkbook,
            temp.Path));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Windows code page 1252", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, codePageReads);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
        Assert.Equal(targetBytes, File.ReadAllBytes(targetWorkbook));
    }

    [Fact]
    public void ImportCommandDoesNotSaveWhenImportedProjectionVerificationFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            new UTF8Encoding(false));
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation
        {
            ThrowOnVerify = true
        };
        var application = CommandLineTestFactory.Create(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("verification failed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("import:Module1.bas", automation.Events);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void ImportMirrorCleanupFailurePreventsVerificationAndInPlaceSave()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"\r\n",
            new UTF8Encoding(false));
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        FileStream? stagingLock = null;
        string? stagingPath = null;
        var automation = new FakeWorkbookBuildAutomation();
        automation.OnImport = () =>
        {
            var source = Assert.Single(automation.ImportedSources);
            stagingPath = Path.GetDirectoryName(source.SourcePath);
            stagingLock = File.Open(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        var command = new ImportCommand(
            automation,
            new VbeImportSourceSetFactory(() => 65001));

        try
        {
            var result = command.Run(new ImportCommandRequest(
                sourceDirectory,
                targetWorkbook,
                temp.Path));

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("could not be removed", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, automation.VerifyCalls);
            Assert.Equal(0, automation.SaveCalls);
            Assert.Equal("workbook", File.ReadAllText(targetWorkbook, Encoding.UTF8));
        }
        finally
        {
            stagingLock?.Dispose();
            if (stagingPath is not null && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private sealed class FakeNativeImportGenerationAutomation :
        IWorkbookBuildAutomation,
        IWorkbookGenerationAutomation
    {
        public int GenerationRuns { get; private set; }

        public int LegacyOpenCalls { get; private set; }

        public WorkbookAutomationTimeouts? Timeouts { get; private set; }

        public bool CancellationObserved { get; private set; }

        public bool CleanupFinished { get; private set; }

        public bool FailCleanupProof { get; init; }

        public List<string> Events { get; } = [];

        public IWorkbookBuildSession OpenWorkbook(string workbookPath)
        {
            LegacyOpenCalls++;
            throw new InvalidOperationException("The legacy import automation path was used.");
        }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            GenerationRuns++;
            Timeouts = timeouts;
            try
            {
                return await operation(
                    new FakeNativeImportGenerationSession(Events),
                    cancellationToken);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                CleanupFinished = true;
                Exception cancellationError = FailCleanupProof
                    ? new WorkbookAutomationCleanupException(
                        "The owned Excel process release could not be verified.",
                        ex)
                    : ex;
                throw new WorkbookAutomationCanceledException(
                    new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                    cancellationToken,
                    cancellationError);
            }
        }
    }

    private sealed class FakeNativeImportGenerationSession(List<string> events) :
        IWorkbookGenerationSession
    {
        public async Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookReference>>([]);

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveModuleAsync(
            string moduleName,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task VerifyAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            events.Add("save");
            return Task.CompletedTask;
        }
    }

    private sealed class SignalThenFrameStream(Task signal, byte[] frame) : Stream
    {
        private bool frameRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (frameRead)
            {
                return 0;
            }

            await signal.WaitAsync(cancellationToken);
            frame.CopyTo(buffer);
            frameRead = true;
            return frame.Length;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
