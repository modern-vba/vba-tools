namespace VbaDevTools.App.Workbooks;

public interface IInitialWorkbookCreator
{
    IReadOnlyList<string> CreateInitialWorkbook(string workbookPath);
}
