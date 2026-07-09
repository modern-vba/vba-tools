using System.Runtime.InteropServices;
using VbaDev.App.Testing;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class ExcelComWorkbookTestRunner : IWorkbookTestRunner
{
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
        object? workbookObject = null;
        object? sheetObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            workbookObject = excel.Workbooks.Open(workbookPath, 0, false);
            dynamic workbook = workbookObject;
            excel.Run($"'{workbook.Name}'!{UnitTestEntryPoint}");
            sheetObject = workbook.Worksheets(UnitTestSheetName);
            return ReadResultRows(sheetObject);
        }
        finally
        {
            ReleaseComObject(sheetObject);
            if (workbookObject is not null)
            {
                try
                {
                    dynamic workbook = workbookObject;
                    workbook.Close(false);
                }
                finally
                {
                    ReleaseComObject(workbookObject);
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
                    ReleaseComObject(excelObject);
                }
            }
        }
    }

    private static IReadOnlyList<WorkbookTestResultRow> ReadResultRows(object sheetObject)
    {
        dynamic sheet = sheetObject;
        var lastRow = (int)sheet.Cells(sheet.Rows.Count, 1).End(XlUp).Row;
        var results = new List<WorkbookTestResultRow>();
        for (var row = 2; row <= lastRow; row++)
        {
            var category = Convert.ToString(sheet.Cells(row, 1).Value2) ?? string.Empty;
            var testName = Convert.ToString(sheet.Cells(row, 2).Value2) ?? string.Empty;
            var result = Convert.ToString(sheet.Cells(row, 3).Value2) ?? string.Empty;
            var message = Convert.ToString(sheet.Cells(row, 4).Value2) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(testName))
            {
                continue;
            }

            results.Add(new WorkbookTestResultRow(category, testName, result, message));
        }

        return results;
    }

    private static void ReleaseComObject(object? value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
