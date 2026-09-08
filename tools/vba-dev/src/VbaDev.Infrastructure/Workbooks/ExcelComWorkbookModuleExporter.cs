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

    /// <inheritdoc />
    public async Task ExportModulesAsync(
        string workbookPath,
        WorkbookExportStaging staging,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
    {
        await generationAutomation.RunAsync(workbookPath, automationTimeouts,
            async (session, token) =>
            {
                var modules = await session.GetModulesAsync(token).ConfigureAwait(false);
                foreach (var module in modules.Where(module => module.Kind.IsImportable())
                    .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase))
                {
                    await staging.WriteModuleAsync(module.Name + GetSourceExtension(module.Kind),
                        path => session.ExportModuleAsync(module.Name, path, token)).ConfigureAwait(false);
                }
                return true;
            }, cancellationToken).ConfigureAwait(false);
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
