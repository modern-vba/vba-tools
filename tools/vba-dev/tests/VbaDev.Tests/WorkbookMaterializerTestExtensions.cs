using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

internal static class WorkbookMaterializerTestExtensions
{
    internal static async Task<WorkbookMaterializationResult> MaterializeSourceSnapshotAsync(
        this WorkbookMaterializer materializer,
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> expectedSourceFiles,
        WorkbookAutomationTimeouts requestedTimeouts,
        CancellationToken cancellationToken,
        int activeCodePage = 65001,
        Action<BuildSourceSnapshotCapture>? captureCreated = null)
    {
        var projectRoot = Path.GetDirectoryName(Path.GetFullPath(templateWorkbookPath))
            ?? throw new InvalidOperationException(
                $"Template workbook does not have a parent directory: {templateWorkbookPath}");
        var persistentSourcePath = Path.Combine(
            projectRoot,
            "missing-project-source",
            documentName);
        var ordinaryBinPath = Path.Combine(
            projectRoot,
            "ordinary-bin",
            $"{documentName}.xlsm");
        var ordinaryPublishPath = Path.Combine(
            projectRoot,
            "ordinary-publish",
            $"{documentName}.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(ordinaryBinPath)!);
        var ordinaryBinBytes = Encoding.UTF8.GetBytes("ordinary-bin-sentinel");
        File.WriteAllBytes(ordinaryBinPath, ordinaryBinBytes);

        var manifest = ProjectManifest.CreateDefault(
            "MaterializerTests",
            documentName,
            projectRoot,
            commonModulesRepositoryPath: null,
            references: desiredReferences) with
        {
            CommandDefaults = new CommandDefaults(
                ExcelAutomation: new ExcelAutomationCommandDefaults(
                    checked((int)requestedTimeouts.WorkbookOpen.TotalSeconds),
                    checked((int)requestedTimeouts.WorkbookSave.TotalSeconds)))
        };
        var document = manifest.Documents[documentName] with
        {
            SourcePath = Path.GetRelativePath(projectRoot, persistentSourcePath),
            TemplatePath = Path.GetRelativePath(projectRoot, templateWorkbookPath),
            BinPath = Path.GetRelativePath(projectRoot, ordinaryBinPath),
            PublishPath = Path.GetRelativePath(projectRoot, ordinaryPublishPath)
        };
        manifest.Documents[documentName] = document;
        var context = new ResolvedProjectContext(
            projectRoot,
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName),
            manifest,
            documentName,
            document,
            persistentSourcePath,
            Path.GetFullPath(templateWorkbookPath),
            ordinaryBinPath,
            ordinaryPublishPath,
            CommonModulesRepositoryPath: null);

        var captureRoot = Path.Combine(
            projectRoot,
            "source-snapshot-capture",
            Guid.NewGuid().ToString("N"));
        using var sourceCapture = new BuildSourceSnapshotCaptureFactory(
                captureRoot,
                new VbaSourceAdmission(
                    () => activeCodePage,
                    _ => expectedSourceFiles
                        .SelectMany(source => new[] { source.SourcePath, source.BinaryPath })
                        .OfType<string>()
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
            .Create(projectRoot, cancellationToken);
        Assert.Equal(
            expectedSourceFiles
                .Select(source => Path.GetFullPath(source.SourcePath))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            sourceCapture.Admission.Sources.Select(source => source.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        captureCreated?.Invoke(sourceCapture);

        try
        {
            return await materializer.MaterializeAsync(
                    new WorkbookMaterializationIntent.SourceSnapshotBuild(
                        context,
                        sourceCapture,
                        Path.GetFullPath(targetWorkbookPath)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Assert.False(Directory.Exists(persistentSourcePath));
            Assert.Equal(ordinaryBinBytes, File.ReadAllBytes(ordinaryBinPath));
        }
    }

}
