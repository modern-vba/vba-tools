using System.Runtime.InteropServices;
using VbaDev.App.Testing;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class ExcelComWorkbookTestRunner : IWorkbookTestRunner
{
    private const int MsoAutomationSecurityLow = 1;
    private const int XlUp = -4162;
    private const string UnitTestEntryPoint = "UnitTestMain";
    private const string UnitTestSheetName = "UNIT_TEST_SHEET";

    public IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM test automation is supported only on Windows.");
        }

        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is not available.");
        object? excelObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        object? worksheetsObject = null;
        object? sheetObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            excel.AutomationSecurity = MsoAutomationSecurityLow;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(workbookPath, 0, false);
            dynamic workbook = workbookObject;
            excel.Run($"'{workbook.Name}'!{UnitTestEntryPoint}");
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
            ComObjectReleaser.Release(workbooksObject);
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                finally
                {
                    ComObjectReleaser.Release(workbookObject);
                }
            }

            if (excelObject is not null)
            {
                try
                {
                    dynamic excel = excelObject;
                    excel.Quit();
                }
                finally
                {
                    ComObjectReleaser.Release(excelObject);
                }
            }

            ComObjectReleaser.CollectReleasedComObjects();
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
