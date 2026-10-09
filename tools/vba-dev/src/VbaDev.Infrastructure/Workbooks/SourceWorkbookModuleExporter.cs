using VbaDev.App.Export;
using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>Exports the exact project source workbook, including live VBE edits.</summary>
public sealed class SourceWorkbookModuleExporter(ISourceWorkbookAutomation automation)
    : IWorkbookModuleExporter
{
    public Task ExportModulesAsync(
        string workbookPath,
        WorkbookExportStaging staging,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
        => automation.RunAsync(
            workbookPath,
            automationTimeouts,
            async (session, token) =>
            {
                var modules = await session.GetModulesAsync(token).ConfigureAwait(false);
                foreach (var module in modules.Where(module => module.Kind.IsImportable())
                    .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase))
                {
                    await staging.WriteModuleAsync(
                        module.Name + GetSourceExtension(module.Kind),
                        path => session.ExportModuleAsync(module.Name, path, token)).ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);

    private static string GetSourceExtension(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => ".bas",
            WorkbookModuleKind.ClassModule => ".cls",
            WorkbookModuleKind.Form => ".frm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
