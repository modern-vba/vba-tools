namespace VbaDev.App.Workbooks;

public interface IWorkbookBuildAutomation
{
    IWorkbookBuildSession OpenWorkbook(string workbookPath);
}
