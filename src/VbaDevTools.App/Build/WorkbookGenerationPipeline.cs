using VbaDevTools.App.Workbooks;

namespace VbaDevTools.App.Build;

public sealed class WorkbookGenerationPipeline
{
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;

    public WorkbookGenerationPipeline(IWorkbookBuildAutomation workbookBuildAutomation)
    {
        this.workbookBuildAutomation = workbookBuildAutomation;
    }

    public void Generate(
        string templateWorkbookPath,
        string targetWorkbookPath,
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
            using (var session = workbookBuildAutomation.OpenWorkbook(tempWorkbookPath))
            {
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
        }
        finally
        {
            DeleteIfExists(tempWorkbookPath);
        }
    }

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
