using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

/// <summary>Produces declared VBA source units in an invocation-owned staging workspace.</summary>
public interface IWorkbookModuleExporter
{
    /// <summary>Exports through the staging producer boundary using caller-resolved timeouts.</summary>
    Task ExportModulesAsync(
        string workbookPath,
        WorkbookExportStaging staging,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken);
}
