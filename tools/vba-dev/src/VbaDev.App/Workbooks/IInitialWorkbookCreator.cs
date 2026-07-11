namespace VbaDev.App.Workbooks;

/// <summary>
/// Creates the initial source template workbook for a new workbook-backed project.
/// </summary>
public interface IInitialWorkbookCreator
{
    /// <summary>
    /// Creates an initial macro-enabled workbook and returns its default VBA project references.
    /// </summary>
    /// <param name="workbookPath">The workbook path to create.</param>
    /// <returns>The reference names present in the created workbook.</returns>
    IReadOnlyList<string> CreateInitialWorkbook(string workbookPath);
}
