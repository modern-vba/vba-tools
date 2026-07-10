namespace VbaDev.App.Testing;

public sealed record WorkbookTestSelector(string? ModuleName = null, string? ProcedureName = null);

public interface IWorkbookTestRunner
{
    IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath, WorkbookTestSelector selector);
}
