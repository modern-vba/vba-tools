namespace VbaDev.App.Export;

public interface IWorkbookModuleExporter
{
    void ExportModules(string workbookPath, string destinationDirectory);
}
