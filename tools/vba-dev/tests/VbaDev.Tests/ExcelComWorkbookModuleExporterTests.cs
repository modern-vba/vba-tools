using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComWorkbookModuleExporterTests
{
    [Fact]
    public void LegacyWorkbookBuildSessionDoesNotRequireModuleExportCapability()
    {
        using IWorkbookBuildSession session = new LegacyWorkbookBuildSession();

        var error = Assert.Throws<NotSupportedException>(() =>
            session.ExportModule("Module1", "Module1.bas"));

        Assert.Contains("module export", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyWorkbookGenerationSessionDoesNotRequireModuleExportCapability()
    {
        IWorkbookGenerationSession session = new LegacyWorkbookGenerationSession();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.ExportModuleAsync("Module1", "Module1.bas", CancellationToken.None));

        Assert.Contains("module export", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportModulesAsyncUsesOwnedSessionForEachImportableModule()
    {
        using var temp = TempDirectory.Create();
        var workbookPath = Path.Combine(temp.Path, "Book1.xlsm");
        var destinationPath = temp.CreateDirectory("exported-source");
        File.WriteAllText(workbookPath, "workbook");
        var session = new ExportRecordingWorkbookGenerationSession([
            new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document),
            new WorkbookModule("ModuleB", WorkbookModuleKind.StandardModule),
            new WorkbookModule("Dialog", WorkbookModuleKind.Form),
            new WorkbookModule("ClassA", WorkbookModuleKind.ClassModule)
        ]);
        var exporter = new ExcelComWorkbookModuleExporter(
            new ExportRecordingWorkbookGenerationAutomation(session));

        await exporter.ExportModulesAsync(
            workbookPath,
            destinationPath,
            CancellationToken.None);

        Assert.Equal("ClassA", File.ReadAllText(Path.Combine(destinationPath, "ClassA.cls")));
        Assert.Equal("Dialog", File.ReadAllText(Path.Combine(destinationPath, "Dialog.frm")));
        Assert.Equal("ModuleB", File.ReadAllText(Path.Combine(destinationPath, "ModuleB.bas")));
        Assert.False(File.Exists(Path.Combine(destinationPath, "ThisWorkbook.cls")));
    }

    private sealed class ExportRecordingWorkbookGenerationAutomation(
        IWorkbookGenerationSession session) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => operation(session, cancellationToken);
    }

    private sealed class LegacyWorkbookBuildSession : IWorkbookBuildSession
    {
        public IReadOnlyList<WorkbookModule> GetModules() => [];

        public IReadOnlyList<WorkbookReference> GetReferences() => [];

        public bool RemoveReference(string referenceName) => false;

        public void AddReference(ResolvedVbaProjectReference reference)
        {
        }

        public void RemoveModule(string moduleName)
        {
        }

        public void ImportModule(VbeImportSourceFile sourceFile)
        {
        }

        public void VerifyImportedModules()
        {
        }

        public void Save()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class LegacyWorkbookGenerationSession : IWorkbookGenerationSession
    {
        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookModule>>([]);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkbookReference>>([]);

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task VerifyAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ExportRecordingWorkbookGenerationSession(
        IReadOnlyList<WorkbookModule> modules) : IWorkbookGenerationSession
    {
        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => Task.FromResult(modules);

        public Task ExportModuleAsync(
            string moduleName,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(destinationPath, moduleName);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task VerifyAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SaveAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
