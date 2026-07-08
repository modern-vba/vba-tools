using System.Collections;
using System.Runtime.InteropServices;
using VbaDevTools.App.Workbooks;

namespace VbaDevTools.Infrastructure.Workbooks;

public sealed class ExcelComInitialWorkbookCreator : IInitialWorkbookCreator
{
    private const int XlOpenXmlWorkbookMacroEnabled = 52;

    public IReadOnlyList<string> CreateInitialWorkbook(string workbookPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM workbook creation is supported only on Windows.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is not available.");
        object? excelObject = null;
        object? workbookObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            workbookObject = excel.Workbooks.Add();
            dynamic workbook = workbookObject;
            var referenceDescriptions = ReadReferenceDescriptions(workbookObject);
            workbook.SaveAs(workbookPath, XlOpenXmlWorkbookMacroEnabled);
            workbook.Close(false);
            excel.Quit();
            return referenceDescriptions;
        }
        finally
        {
            ReleaseComObject(workbookObject);
            ReleaseComObject(excelObject);
        }
    }

    private static IReadOnlyList<string> ReadReferenceDescriptions(object workbookObject)
    {
        var referenceDescriptions = new List<string>();
        object? referencesObject = null;

        try
        {
            dynamic workbook = workbookObject;
            referencesObject = workbook.VBProject.References;
            foreach (var referenceObject in (IEnumerable)referencesObject)
            {
                try
                {
                    dynamic reference = referenceObject;
                    var description = (string?)reference.Description;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        referenceDescriptions.Add(description.Trim());
                    }
                }
                finally
                {
                    ReleaseComObject(referenceObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(referencesObject);
        }

        return referenceDescriptions;
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
