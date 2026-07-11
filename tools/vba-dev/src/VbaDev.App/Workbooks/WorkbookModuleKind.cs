namespace VbaDev.App.Workbooks;

/// <summary>
/// Identifies the kind of a VBA component currently present in a workbook.
/// </summary>
public enum WorkbookModuleKind
{
    /// <summary>
    /// A standard code module.
    /// </summary>
    StandardModule,

    /// <summary>
    /// A class module.
    /// </summary>
    ClassModule,

    /// <summary>
    /// A user form module.
    /// </summary>
    Form,

    /// <summary>
    /// A host document module such as ThisWorkbook or a worksheet module.
    /// </summary>
    Document,

    /// <summary>
    /// A component kind that is not imported or removed by VbaDev.
    /// </summary>
    Other
}

/// <summary>
/// Provides helper operations for workbook module kinds.
/// </summary>
public static class WorkbookModuleKindExtensions
{
    /// <summary>
    /// Determines whether modules of this kind are replaced during source import.
    /// </summary>
    /// <param name="kind">The workbook module kind.</param>
    /// <returns>True for standard, class, and form modules.</returns>
    public static bool IsImportable(this WorkbookModuleKind kind)
        => kind is WorkbookModuleKind.StandardModule or WorkbookModuleKind.ClassModule or WorkbookModuleKind.Form;
}
