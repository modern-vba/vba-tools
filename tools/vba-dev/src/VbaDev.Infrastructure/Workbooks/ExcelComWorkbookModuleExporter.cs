using System.Runtime.InteropServices;
using VbaDev.App.Export;

namespace VbaDev.Infrastructure.Workbooks;

public sealed class ExcelComWorkbookModuleExporter : IWorkbookModuleExporter
{
    private const int VbextComponentTypeStandardModule = 1;
    private const int VbextComponentTypeClassModule = 2;
    private const int VbextComponentTypeForm = 3;

    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM export automation is supported only on Windows.");
        }

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
            workbookObject = excel.Workbooks.Open(workbookPath, 0, false);
            ExportImportableComponents(workbookObject, destinationDirectory);
        }
        finally
        {
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

    private static void ExportImportableComponents(object workbookObject, string destinationDirectory)
    {
        dynamic workbook = workbookObject;
        dynamic components = workbook.VBProject.VBComponents;
        try
        {
            foreach (var componentObject in components)
            {
                try
                {
                    dynamic component = componentObject;
                    var exportPath = GetExportPath(destinationDirectory, (string)component.Name, (int)component.Type);
                    if (exportPath is not null)
                    {
                        component.Export(exportPath);
                    }
                }
                finally
                {
                    ReleaseComObject(componentObject);
                }
            }
        }
        finally
        {
            ReleaseComObject(components);
        }
    }

    private static string? GetExportPath(string destinationDirectory, string componentName, int componentType)
        => componentType switch
        {
            VbextComponentTypeStandardModule => Path.Combine(destinationDirectory, componentName + ".bas"),
            VbextComponentTypeClassModule => Path.Combine(destinationDirectory, componentName + ".cls"),
            VbextComponentTypeForm => Path.Combine(destinationDirectory, componentName + ".frm"),
            _ => null
        };

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
