namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes a reference currently present in an open workbook's VBA project.
/// </summary>
/// <param name="Name">The human-visible reference name.</param>
/// <param name="IsRemovable">Whether VBIDE reports that the reference can be removed.</param>
/// <param name="NamespaceName">The actual project or library name exposed by VBIDE.</param>
/// <param name="Guid">The loaded TypeLib GUID, when available.</param>
/// <param name="Major">The loaded TypeLib major version, when available.</param>
/// <param name="Minor">The loaded TypeLib minor version, when available.</param>
/// <param name="FullPath">The library path exposed by the workbook or resolved from its owned process, when available.</param>
public sealed record WorkbookReference(
    string Name,
    bool IsRemovable,
    string? NamespaceName = null,
    string? Guid = null,
    int? Major = null,
    int? Minor = null,
    string? FullPath = null);
