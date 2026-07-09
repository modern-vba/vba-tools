namespace VbaDev.App.Testing;

public interface IWorkbookTestRunner
{
    IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath);
}
