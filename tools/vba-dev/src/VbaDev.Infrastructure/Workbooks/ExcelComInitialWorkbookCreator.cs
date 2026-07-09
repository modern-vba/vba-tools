using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

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
        object? workbooksObject = null;
        object? workbookObject = null;

        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Add();
            dynamic workbook = workbookObject;
            var referenceDescriptions = ReadReferenceDescriptions(workbookObject);
            workbook.SaveAs(workbookPath, XlOpenXmlWorkbookMacroEnabled);
            return referenceDescriptions;
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
                    ComObjectReleaser.Release(workbookObject);
                }
            }

            ComObjectReleaser.Release(workbooksObject);
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

    private static IReadOnlyList<string> ReadReferenceDescriptions(object workbookObject)
    {
        var referenceDescriptions = new List<string>();
        object? referencesObject = null;
        object? vbProjectObject = null;

        try
        {
            dynamic workbook = workbookObject;
            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            referencesObject = vbProject.References;
            dynamic references = referencesObject;
            var referenceCount = (int)references.Count;
            for (var index = 1; index <= referenceCount; index++)
            {
                object? referenceObject = null;
                try
                {
                    referenceObject = references.Item(index);
                    dynamic reference = referenceObject;
                    var description = (string?)reference.Description;
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        referenceDescriptions.Add(description.Trim());
                    }
                }
                finally
                {
                    ComObjectReleaser.Release(referenceObject);
                }
            }
        }
        finally
        {
            ComObjectReleaser.Release(referencesObject);
            ComObjectReleaser.Release(vbProjectObject);
        }

        return referenceDescriptions;
    }
}
