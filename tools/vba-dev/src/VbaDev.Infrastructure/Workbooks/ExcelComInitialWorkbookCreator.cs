using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Creates initial macro-enabled workbooks through Excel COM.
/// </summary>
public sealed class ExcelComInitialWorkbookCreator : IInitialWorkbookCreator
{
    private const int XlOpenXmlWorkbookMacroEnabled = 52;

    /// <summary>
    /// Creates a new .xlsm workbook and returns the default reference descriptions present in its VBA project.
    /// </summary>
    /// <param name="workbookPath">The workbook path to create.</param>
    /// <returns>The default VBA project reference descriptions.</returns>
    public IReadOnlyList<string> CreateInitialWorkbook(string workbookPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        using var session = ExcelComWorkbookSession.Create();
        dynamic workbook = session.WorkbookObject;
        var referenceDescriptions = ReadReferenceDescriptions(session.WorkbookObject);
        workbook.SaveAs(workbookPath, XlOpenXmlWorkbookMacroEnabled);
        return referenceDescriptions;
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
