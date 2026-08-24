using System.Runtime.InteropServices;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Verifies disposable copies of project templates in dedicated owned Excel processes.
/// </summary>
public sealed class ExcelProjectMaterializationDiagnosticPort
    : IProjectMaterializationDiagnosticPort
{
    private readonly IWorkbookGenerationAutomation workbookAutomation;
    private readonly Func<string, string> stageTemplateWorkbook;
    private readonly Action<string> deleteStagedWorkbook;

    /// <summary>
    /// Creates the production project materialization adapter.
    /// </summary>
    public ExcelProjectMaterializationDiagnosticPort()
        : this(
            new ExcelComWorkbookBuildAutomation(),
            StageTemplateWorkbook,
            DeleteStagedWorkbook)
    {
    }

    internal ExcelProjectMaterializationDiagnosticPort(
        IWorkbookGenerationAutomation workbookAutomation,
        Func<string, string> stageTemplateWorkbook,
        Action<string> deleteStagedWorkbook)
    {
        this.workbookAutomation = workbookAutomation;
        this.stageTemplateWorkbook = stageTemplateWorkbook;
        this.deleteStagedWorkbook = deleteStagedWorkbook;
    }

    /// <inheritdoc />
    public async Task<ProjectMaterializationDiagnosticRun> RunAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        foreach (var (documentName, document) in project.Manifest.Documents
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var checkId = $"project.workbookMaterialization/{documentName}";
            var templatePath = project.ResolvePath(document.TemplatePath);
            if (!File.Exists(templatePath))
            {
                results.Add(DiagnosticResult.Skip(
                    checkId,
                    $"The source template does not exist: {templatePath}."));
                continue;
            }

            string? stagedWorkbookPath = null;
            try
            {
                stagedWorkbookPath = stageTemplateWorkbook(templatePath);
                DiagnosticResult? vbideResult = null;
                await workbookAutomation.RunAsync(
                    stagedWorkbookPath,
                    WorkbookAutomationTimeouts.Default,
                    async (session, operationCancellationToken) =>
                    {
                        try
                        {
                            await session.GetModulesAsync(operationCancellationToken)
                                .ConfigureAwait(false);
                            vbideResult = DiagnosticResult.Pass(
                                checkId,
                                "A disposable template copy opened with accessible VBProject state.");
                        }
                        catch (COMException exception)
                        {
                            vbideResult = DiagnosticResult.Fail(
                                checkId,
                                $"The disposable template VBProject could not be accessed: {exception.Message}");
                        }

                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                results.Add(vbideResult!);
            }
            catch (WorkbookAutomationTimeoutException exception)
            {
                results.Add(DiagnosticResult.Unverified(checkId, exception.Message));
            }
            catch (WorkbookAutomationCanceledException exception)
            {
                results.Add(DiagnosticResult.Unverified(checkId, exception.Message));
                return new ProjectMaterializationDiagnosticRun(
                    results,
                    Complete: false,
                    Canceled: true);
            }
            catch (WorkbookAutomationCleanupException exception)
            {
                results.Add(DiagnosticResult.Unverified(checkId, exception.Message));
                return new ProjectMaterializationDiagnosticRun(
                    results,
                    Complete: false);
            }
            catch (Exception exception)
            {
                results.Add(DiagnosticResult.Fail(
                    checkId,
                    $"The disposable template could not be materialized: {exception.Message}"));
            }
            finally
            {
                if (stagedWorkbookPath is not null)
                {
                    deleteStagedWorkbook(stagedWorkbookPath);
                }
            }
        }

        return new ProjectMaterializationDiagnosticRun(results);
    }

    private static string StageTemplateWorkbook(string templatePath)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-doctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var stagedWorkbookPath = Path.Combine(
            directory,
            Path.GetFileName(templatePath));
        File.Copy(templatePath, stagedWorkbookPath);
        return stagedWorkbookPath;
    }

    private static void DeleteStagedWorkbook(string stagedWorkbookPath)
    {
        if (File.Exists(stagedWorkbookPath))
        {
            File.Delete(stagedWorkbookPath);
        }

        var directory = Path.GetDirectoryName(stagedWorkbookPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory);
        }
    }
}
