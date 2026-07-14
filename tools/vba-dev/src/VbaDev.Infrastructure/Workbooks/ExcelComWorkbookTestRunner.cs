using System.Runtime.InteropServices;
using VbaDev.App.Testing;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Runs VBA unit tests inside Excel through COM automation.
/// </summary>
public sealed class ExcelComWorkbookTestRunner : IWorkbookTestRunner
{
    private const int XlUp = -4162;
    private const string UnitTestEntryPoint = "UnitTestMain";
    private const string UnitTestSheetName = "UNIT_TEST_SHEET";

    /// <summary>
    /// Runs the UnitTestMain macro and reads result rows from the unit-test worksheet.
    /// </summary>
    /// <param name="workbookPath">The workbook path containing tests.</param>
    /// <param name="selector">The optional module or procedure selector passed to UnitTestMain.</param>
    /// <returns>The raw workbook test result rows.</returns>
    public IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath, WorkbookTestSelector selector)
    {
        ExcelComWorkbookSession? session = null;
        object? worksheetsObject = null;
        object? sheetObject = null;

        try
        {
            session = ExcelComWorkbookSession.Open(workbookPath, enableAutomationSecurityLow: true);
            dynamic excel = session.ExcelObject;
            dynamic workbook = session.WorkbookObject;
            var entryPoint = $"'{workbook.Name}'!{UnitTestEntryPoint}";
            if (!string.IsNullOrWhiteSpace(selector.ProcedureName))
            {
                excel.Run(entryPoint, selector.ModuleName, selector.ProcedureName);
            }
            else if (!string.IsNullOrWhiteSpace(selector.ModuleName))
            {
                excel.Run(entryPoint, selector.ModuleName);
            }
            else
            {
                excel.Run(entryPoint);
            }

            worksheetsObject = workbook.Worksheets;
            dynamic worksheets = worksheetsObject;
            sheetObject = worksheets(UnitTestSheetName);
            return ReadResultRows(sheetObject);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            ComObjectReleaser.Release(sheetObject);
            ComObjectReleaser.Release(worksheetsObject);
            session?.Dispose();
        }
    }

    private static IReadOnlyList<WorkbookTestResultRow> ReadResultRows(object sheetObject)
    {
        var lastRow = GetLastResultRow(sheetObject);
        var results = new List<WorkbookTestResultRow>();
        for (var row = 2; row <= lastRow; row++)
        {
            var category = GetCellText(sheetObject, row, 1);
            var testName = GetCellText(sheetObject, row, 2);
            var result = GetCellText(sheetObject, row, 3);
            var message = GetCellText(sheetObject, row, 4);
            if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(testName))
            {
                continue;
            }

            results.Add(new WorkbookTestResultRow(category, testName, result, message));
        }

        return results;
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
