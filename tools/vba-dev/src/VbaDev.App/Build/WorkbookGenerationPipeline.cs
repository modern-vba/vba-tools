using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

public sealed class WorkbookGenerationPipeline
{
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;

    public WorkbookGenerationPipeline(
        IWorkbookBuildAutomation workbookBuildAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
    {
        this.workbookBuildAutomation = workbookBuildAutomation;
        this.referenceNormalizer = referenceNormalizer;
    }

    public WorkbookGenerationResult Generate(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        var targetDirectory = Path.GetDirectoryName(targetWorkbookPath)
            ?? throw new BuildCommandException($"Target workbook path is invalid: {targetWorkbookPath}");
        Directory.CreateDirectory(targetDirectory);

        var tempWorkbookPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileNameWithoutExtension(targetWorkbookPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(targetWorkbookPath)}");

        try
        {
            File.Copy(templateWorkbookPath, tempWorkbookPath, overwrite: false);
            IReadOnlyList<string> warnings;
            using (var session = workbookBuildAutomation.OpenWorkbook(tempWorkbookPath))
            {
                warnings = referenceNormalizer.Normalize(session, documentName, desiredReferences);
                foreach (var component in session.GetModules().Where(component => component.Kind.IsImportable()))
                {
                    session.RemoveModule(component.Name);
                }

                foreach (var sourceFile in sourceFiles)
                {
                    session.ImportModule(sourceFile);
                }

                session.Save();
            }

            ReplaceTarget(tempWorkbookPath, targetWorkbookPath);
            return new WorkbookGenerationResult(warnings);
        }
        finally
        {
            DeleteIfExists(tempWorkbookPath);
        }
    }

    public sealed record WorkbookGenerationResult(IReadOnlyList<string> Warnings);

    private static void ReplaceTarget(string tempWorkbookPath, string targetWorkbookPath)
    {
        try
        {
            File.Move(tempWorkbookPath, targetWorkbookPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
