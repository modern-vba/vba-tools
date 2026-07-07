namespace VbaDevTools.App.Workbooks;

public interface IWorkbookBuildAutomation
{
    IWorkbookBuildSession OpenWorkbook(string workbookPath);
}
