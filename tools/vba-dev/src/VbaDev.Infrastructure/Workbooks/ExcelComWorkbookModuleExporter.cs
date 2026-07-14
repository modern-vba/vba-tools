using VbaDev.App.Export;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Exports VBA modules from Excel workbooks through COM automation.
/// </summary>
public sealed class ExcelComWorkbookModuleExporter : IWorkbookModuleExporter
{
    private const int VbextComponentTypeStandardModule = 1;
    private const int VbextComponentTypeClassModule = 2;
    private const int VbextComponentTypeForm = 3;

    /// <summary>
    /// Exports standard modules, class modules, and forms from an Excel workbook.
    /// </summary>
    /// <param name="workbookPath">The workbook path to export from.</param>
    /// <param name="destinationDirectory">The destination directory for exported sources.</param>
    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        using var session = ExcelComWorkbookSession.Open(workbookPath);
        ExportImportableComponents(session.WorkbookObject, destinationDirectory);
    }

    private static void ExportImportableComponents(object workbookObject, string destinationDirectory)
    {
        dynamic workbook = workbookObject;
        object? vbProjectObject = null;
        object? componentsObject = null;
        try
        {
            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            componentsObject = vbProject.VBComponents;
            dynamic components = componentsObject;
            var componentCount = (int)components.Count;
            for (var index = 1; index <= componentCount; index++)
            {
                object? componentObject = null;
                try
                {
                    componentObject = components.Item(index);
                    dynamic component = componentObject;
                    var exportPath = GetExportPath(destinationDirectory, (string)component.Name, (int)component.Type);
                    if (exportPath is not null)
                    {
                        component.Export(exportPath);
                    }
                }
                finally
                {
                    ComObjectReleaser.Release(componentObject);
                }
            }
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(vbProjectObject);
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

}
