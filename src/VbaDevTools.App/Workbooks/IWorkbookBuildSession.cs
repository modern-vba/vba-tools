namespace VbaDevTools.App.Workbooks;

public interface IWorkbookBuildSession : IDisposable
{
    IReadOnlyList<WorkbookModule> GetModules();

    void RemoveModule(string moduleName);

    void ImportModule(VbaSourceFile sourceFile);

    void Save();
}
