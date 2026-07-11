namespace VbaDev.App.Workbooks;

/// <summary>
/// Identifies the VBA component kind represented by an exported source file.
/// </summary>
public enum VbaSourceKind
{
    /// <summary>
    /// A standard .bas module.
    /// </summary>
    StandardModule,

    /// <summary>
    /// A class .cls module.
    /// </summary>
    ClassModule,

    /// <summary>
    /// A form .frm module with an optional .frx sidecar.
    /// </summary>
    Form
}
