namespace VbaDev.App.Testing;

using VbaDev.App.Workbooks;
using VbaTools.Syntax;

/// <summary>
/// Selects the VBA tests to run inside a workbook.
/// </summary>
public sealed record WorkbookTestSelector
{
    /// <summary>
    /// Creates an exact VBA test selector.
    /// </summary>
    /// <param name="moduleName">The optional test module <c>IDENTIFIER</c>.</param>
    /// <param name="procedureName">The optional test procedure <c>IDENTIFIER</c>.</param>
    public WorkbookTestSelector(string? moduleName = null, string? procedureName = null)
    {
        if (moduleName is not null
            && (!VbaIdentifier.IsIdentifier(moduleName)
                || moduleName.EnumerateRunes().Take(32).Count() > 31))
        {
            throw new InvalidOperationException(
                "Test module selector must be an exact VBA IDENTIFIER of 1 to 31 characters.");
        }

        if (procedureName is not null
            && (procedureName.Length > 255 || !VbaIdentifier.IsIdentifier(procedureName)))
        {
            throw new InvalidOperationException(
                "Test procedure selector must be an exact VBA IDENTIFIER of 1 to 255 characters.");
        }

        ModuleName = moduleName;
        ProcedureName = procedureName;
    }

    /// <summary>Gets the optional test module name.</summary>
    public string? ModuleName { get; }

    /// <summary>Gets the optional test procedure name within the selected module.</summary>
    public string? ProcedureName { get; }
}
