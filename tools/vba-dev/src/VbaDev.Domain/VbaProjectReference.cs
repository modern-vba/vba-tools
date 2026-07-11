namespace VbaDev.Domain;

/// <summary>
/// Names a VBA project library reference as it appears to workbook authors and manifests.
/// </summary>
/// <param name="Name">The human-visible reference description, such as an Office object library name.</param>
public sealed record VbaProjectReference(string Name);
