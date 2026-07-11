namespace VbaDev.App.Workbooks;

/// <summary>
/// Identifies a concrete VBA project reference that can be added to a workbook.
/// </summary>
/// <param name="Name">The human-visible reference name.</param>
/// <param name="Guid">The TypeLib GUID used by VBIDE reference APIs.</param>
/// <param name="Major">The major TypeLib version.</param>
/// <param name="Minor">The minor TypeLib version.</param>
public sealed record ResolvedVbaProjectReference(string Name, string Guid, int Major, int Minor);
