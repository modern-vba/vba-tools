namespace VbaDev.App.Workbooks;

public interface IWorkbookBuildSession : IDisposable
{
    IReadOnlyList<WorkbookModule> GetModules();

    IReadOnlyList<WorkbookReference> GetReferences();

    bool RemoveReference(string referenceName);

    void AddReference(ResolvedVbaProjectReference reference);

    void RemoveModule(string moduleName);

    void ImportModule(VbaSourceFile sourceFile);

    void Save();
}
