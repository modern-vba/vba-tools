using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>Strict debug import authority without independent source-error analysis or output generation.</summary>
internal sealed record PreparedSourceWorkbookDebug(
    VbeImportSourceSet SourceSet,
    WorkbookMaterializationNamePreflightReport SourcePreflight,
    WorkbookAutomationTimeouts Timeouts);
