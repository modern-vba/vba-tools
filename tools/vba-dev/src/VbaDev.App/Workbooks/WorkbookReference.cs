namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes a reference currently present in an open workbook's VBA project.
/// </summary>
/// <param name="Name">The human-visible reference name.</param>
/// <param name="IsRemovable">Whether VBIDE reports that the reference can be removed.</param>
public sealed record WorkbookReference(string Name, bool IsRemovable);
