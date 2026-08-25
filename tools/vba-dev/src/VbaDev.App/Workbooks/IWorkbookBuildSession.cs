namespace VbaDev.App.Workbooks;

/// <summary>
/// Represents an open workbook automation session that can inspect and mutate a VBA project.
/// </summary>
public interface IWorkbookBuildSession : IDisposable
{
    /// <summary>
    /// Gets the actual name of the open workbook's VBA project.
    /// </summary>
    string GetProjectName();

    /// <summary>
    /// Gets the modules currently present in the workbook's VBA project.
    /// </summary>
    /// <returns>The current workbook modules.</returns>
    IReadOnlyList<WorkbookModule> GetModules();

    /// <summary>
    /// Gets the references currently present in the workbook's VBA project.
    /// </summary>
    /// <returns>The current workbook references.</returns>
    IReadOnlyList<WorkbookReference> GetReferences();

    /// <summary>
    /// Removes a reference by name when the host allows it.
    /// </summary>
    /// <param name="referenceName">The reference name to remove.</param>
    /// <returns>True when the reference was removed.</returns>
    bool RemoveReference(string referenceName);

    /// <summary>
    /// Adds a resolved reference to the workbook's VBA project.
    /// </summary>
    /// <param name="reference">The reference identity to add.</param>
    void AddReference(ResolvedVbaProjectReference reference);

    /// <summary>
    /// Probes one ambiguous reference candidate against the current workbook state and rolls it back.
    /// </summary>
    VbaProjectReferenceProbeAttemptResult TryResolveReference(
        string referenceName,
        ResolvedVbaProjectReference candidate)
        => throw new NotSupportedException(
            "This workbook session does not support in-session reference probing.");

    /// <summary>
    /// Removes a module from the workbook's VBA project.
    /// </summary>
    /// <param name="moduleName">The module name to remove.</param>
    void RemoveModule(string moduleName);

    /// <summary>
    /// Imports an exported VBA source file into the workbook's VBA project.
    /// </summary>
    /// <param name="sourceFile">The source file and optional form sidecar to import.</param>
    void ImportModule(VbeImportSourceFile sourceFile);

    /// <summary>
    /// Exports one VBA component to an explicit source path.
    /// </summary>
    /// <param name="moduleName">The component name to export.</param>
    /// <param name="destinationPath">The complete destination source path.</param>
    void ExportModule(string moduleName, string destinationPath)
        => throw new NotSupportedException(
            "This workbook session does not support module export.");

    /// <summary>
    /// Verifies every component imported during this session before the workbook is saved.
    /// </summary>
    void VerifyImportedModules();

    /// <summary>
    /// Saves the workbook after automation changes.
    /// </summary>
    void Save();
}
