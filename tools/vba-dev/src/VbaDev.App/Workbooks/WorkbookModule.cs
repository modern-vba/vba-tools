namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes a module currently present in an open workbook's VBA project.
/// </summary>
/// <param name="Name">The VBA component name.</param>
/// <param name="Kind">The workbook module kind reported by automation.</param>
public sealed record WorkbookModule(string Name, WorkbookModuleKind Kind);
