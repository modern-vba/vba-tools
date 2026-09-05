using VbaDev.App.Export;
using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Exports VBA modules from Excel workbooks through COM automation.
/// </summary>
public sealed class ExcelComWorkbookModuleExporter : IWorkbookModuleExporter
{
    private readonly IWorkbookGenerationAutomation generationAutomation;

    /// <summary>
    /// Creates an exporter backed by the strongly owned Excel generation lifecycle.
    /// </summary>
    public ExcelComWorkbookModuleExporter()
        : this(new ExcelComWorkbookGenerationAutomation())
    {
    }

    /// <summary>
    /// Creates an exporter with an explicit owned workbook automation adapter.
    /// </summary>
    public ExcelComWorkbookModuleExporter(IWorkbookGenerationAutomation generationAutomation)
    {
        this.generationAutomation = generationAutomation;
    }

    /// <summary>
    /// Exports standard modules, class modules, and forms from an Excel workbook.
    /// </summary>
    /// <param name="workbookPath">The workbook path to export from.</param>
    /// <param name="destinationDirectory">The destination directory for exported sources.</param>
    public void ExportModules(string workbookPath, string destinationDirectory)
        => ExportModulesAsync(workbookPath, destinationDirectory, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Exports standard modules, class modules, and forms from an Excel workbook.
    /// </summary>
    /// <param name="workbookPath">The workbook path to export from.</param>
    /// <param name="destinationDirectory">The destination directory for exported sources.</param>
    /// <param name="cancellationToken">Cancels bounded workbook automation.</param>
    public async Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
        => await ExportModulesAsync(
                workbookPath,
                destinationDirectory,
                WorkbookAutomationTimeouts.Default,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Exports modules with caller-resolved bounded workbook automation timeouts.
    /// </summary>
    public async Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        await generationAutomation.RunAsync(
                workbookPath,
                automationTimeouts,
                async (session, operationCancellationToken) =>
                {
                    var modules = await session.GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    foreach (var module in modules
                                 .Where(module => module.Kind.IsImportable())
                                 .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var destinationPath = Path.Combine(
                            destinationDirectory,
                            module.Name + GetSourceExtension(module.Kind));
                        await session.ExportModuleAsync(
                                module.Name,
                                destinationPath,
                                operationCancellationToken)
                            .ConfigureAwait(false);
                    }
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetSourceExtension(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => ".bas",
            WorkbookModuleKind.ClassModule => ".cls",
            WorkbookModuleKind.Form => ".frm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
