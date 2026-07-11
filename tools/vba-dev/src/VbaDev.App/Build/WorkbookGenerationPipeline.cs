using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Generates workbook outputs by copying a template, normalizing references, and importing VBA source files.
/// </summary>
public sealed class WorkbookGenerationPipeline
{
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;

    /// <summary>
    /// Creates the workbook generation pipeline.
    /// </summary>
    /// <param name="workbookBuildAutomation">The workbook automation port used to edit VBA projects.</param>
    /// <param name="referenceNormalizer">The service that reconciles workbook references with manifest references.</param>
    public WorkbookGenerationPipeline(
        IWorkbookBuildAutomation workbookBuildAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
    {
        this.workbookBuildAutomation = workbookBuildAutomation;
        this.referenceNormalizer = referenceNormalizer;
    }

    /// <summary>
    /// Generates a target workbook from a template with the supplied references and source files.
    /// </summary>
    /// <param name="documentName">The manifest document name used in warnings.</param>
    /// <param name="templateWorkbookPath">The workbook template to copy before import.</param>
    /// <param name="targetWorkbookPath">The final workbook path to replace atomically where possible.</param>
    /// <param name="desiredReferences">The manifest references that should remain in the workbook.</param>
    /// <param name="sourceFiles">The VBA source files to import after removing importable modules.</param>
    /// <returns>The generation warnings produced while preserving protected workbook references.</returns>
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

    /// <summary>
    /// Contains non-fatal warnings emitted while generating a workbook.
    /// </summary>
    /// <param name="Warnings">The warnings that should be included in command output.</param>
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
