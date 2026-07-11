namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the exported VBA module kind inferred from a source document.
/// </summary>
public enum VbaModuleKind
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
    /// A form .frm module.
    /// </summary>
    FormModule
}
