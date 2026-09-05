using System.Runtime.InteropServices;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelComWorkbookTestSession
{
    IReadOnlyList<WorkbookTestResultRow> RunTests(WorkbookTestSelector selector);
}

internal interface IExcelComWorkbookTestBoundary : IDisposable
{
    string WorkbookName { get; }

    void RunMacro(string entryPoint, IReadOnlyList<string?> arguments);

    int GetLastResultRow();

    string GetCellText(int row, int column);
}

/// <summary>
/// Runs VBA unit tests inside Excel through COM automation.
/// </summary>
public sealed class ExcelComWorkbookTestRunner : IWorkbookTestRunner
{
    private const int XlUp = -4162;
    private const string UnitTestEntryPoint = "UnitTestMain";
    private const string UnitTestSheetName = "UNIT_TEST_SHEET";
    private readonly ExcelComWorkbookGenerationAutomation automation;

    public ExcelComWorkbookTestRunner()
        : this(new ExcelComWorkbookGenerationAutomation())
    {
    }

    internal ExcelComWorkbookTestRunner(ExcelComWorkbookGenerationAutomation automation)
    {
        this.automation = automation;
    }

    /// <summary>
    /// Runs the UnitTestMain macro and reads result rows from the unit-test worksheet.
    /// </summary>
    public IReadOnlyList<WorkbookTestResultRow> RunTests(
        string workbookPath,
        WorkbookTestSelector selector)
        => RunTestsAsync(
                workbookPath,
                selector,
                TimeSpan.FromSeconds(600),
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Runs the UnitTestMain macro and reads result rows from the unit-test worksheet.
    /// </summary>
    /// <param name="workbookPath">The workbook path containing tests.</param>
    /// <param name="selector">The optional module or procedure selector passed to UnitTestMain.</param>
    /// <returns>The raw workbook test result rows.</returns>
    public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        string workbookPath,
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
        => automation.RunWorkbookTestsAsync(
            workbookPath,
            automationTimeouts,
            executionTimeout,
            selector,
            cancellationToken);

    internal static IReadOnlyList<WorkbookTestResultRow> RunTests(
        ExcelComWorkbookSession session,
        WorkbookTestSelector selector)
    {
        try
        {
            using var boundary = new ExcelComWorkbookTestBoundary(session);
            return RunTests(boundary, selector);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    internal static IReadOnlyList<WorkbookTestResultRow> RunTests(
        IExcelComWorkbookTestBoundary boundary,
        WorkbookTestSelector selector)
    {
        var entryPoint = $"'{boundary.WorkbookName}'!{UnitTestEntryPoint}";
        if (!string.IsNullOrEmpty(selector.ProcedureName))
        {
            boundary.RunMacro(entryPoint, [selector.ModuleName, selector.ProcedureName]);
        }
        else if (!string.IsNullOrEmpty(selector.ModuleName))
        {
            boundary.RunMacro(entryPoint, [selector.ModuleName]);
        }
        else
        {
            boundary.RunMacro(entryPoint, []);
        }

        return ReadResultRows(boundary);
    }

    private static IReadOnlyList<WorkbookTestResultRow> ReadResultRows(
        IExcelComWorkbookTestBoundary boundary)
    {
        var lastRow = boundary.GetLastResultRow();
        var results = new List<WorkbookTestResultRow>();
        for (var row = 2; row <= lastRow; row++)
        {
            var category = boundary.GetCellText(row, 1);
            var testName = boundary.GetCellText(row, 2);
            var result = boundary.GetCellText(row, 3);
            var message = boundary.GetCellText(row, 4);
            if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(testName))
            {
                continue;
            }

            try
            {
                _ = new WorkbookTestSelector(category, testName);
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException(
                    $"Workbook test result row {row} has an invalid identity: {exception.Message}",
                    exception);
            }

            results.Add(new WorkbookTestResultRow(category, testName, result, message));
        }

        return results;
    }

    private sealed class ExcelComWorkbookTestBoundary(
        ExcelComWorkbookSession session) : IExcelComWorkbookTestBoundary
    {
        private object? worksheetsObject;
        private object? sheetObject;

        public string WorkbookName
        {
            get
            {
                dynamic workbook = session.WorkbookObject;
                return Convert.ToString(workbook.Name) ?? string.Empty;
            }
        }

        public void RunMacro(string entryPoint, IReadOnlyList<string?> arguments)
        {
            dynamic excel = session.ExcelObject;
            switch (arguments.Count)
            {
                case 0:
                    excel.Run(entryPoint);
                    return;
                case 1:
                    excel.Run(entryPoint, arguments[0]);
                    return;
                case 2:
                    excel.Run(entryPoint, arguments[0], arguments[1]);
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported UnitTestMain argument count: {arguments.Count}.");
            }
        }

        public int GetLastResultRow()
            => ExcelComWorkbookTestRunner.GetLastResultRow(GetSheet());

        public string GetCellText(int row, int column)
            => ExcelComWorkbookTestRunner.GetCellText(GetSheet(), row, column);

        public void Dispose()
        {
            ComObjectReleaser.Release(sheetObject);
            ComObjectReleaser.Release(worksheetsObject);
        }

        private object GetSheet()
        {
            if (sheetObject is not null)
            {
                return sheetObject;
            }

            dynamic workbook = session.WorkbookObject;
            worksheetsObject = workbook.Worksheets;
            dynamic worksheets = worksheetsObject;
            sheetObject = worksheets(UnitTestSheetName);
            return sheetObject;
        }
    }

    private static int GetLastResultRow(object sheetObject)
    {
        dynamic sheet = sheetObject;
        object? rowsObject = null;
        object? cellsObject = null;
        object? lastCellObject = null;
        object? endCellObject = null;
        try
        {
            rowsObject = sheet.Rows;
            cellsObject = sheet.Cells;
            dynamic rows = rowsObject;
            dynamic cells = cellsObject;
            lastCellObject = cells(rows.Count, 1);
            dynamic lastCell = lastCellObject;
            endCellObject = lastCell.End(XlUp);
            dynamic endCell = endCellObject;
            return (int)endCell.Row;
        }
        finally
        {
            ComObjectReleaser.Release(endCellObject);
            ComObjectReleaser.Release(lastCellObject);
            ComObjectReleaser.Release(cellsObject);
            ComObjectReleaser.Release(rowsObject);
        }
    }

    private static string GetCellText(object sheetObject, int row, int column)
    {
        dynamic sheet = sheetObject;
        object? cellsObject = null;
        object? cellObject = null;
        try
        {
            cellsObject = sheet.Cells;
            dynamic cells = cellsObject;
            cellObject = cells(row, column);
            dynamic cell = cellObject;
            return Convert.ToString(cell.Value2) ?? string.Empty;
        }
        finally
        {
            ComObjectReleaser.Release(cellObject);
            ComObjectReleaser.Release(cellsObject);
        }
    }
}
