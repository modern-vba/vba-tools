namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes one invocation-owned, active-code-page VBA source staged for VBIDE import.
/// </summary>
public sealed record VbeImportSourceFile
{
    internal VbeImportSourceFile(
        string sourcePath,
        VbaSourceKind kind,
        string? binaryPath,
        VbeImportVerification importVerification,
        string? diagnosticSourcePath = null,
        VbeModuleIdentityAuthority? moduleIdentityAuthority = null)
    {
        SourcePath = sourcePath;
        Kind = kind;
        BinaryPath = binaryPath;
        ImportVerification = importVerification;
        DiagnosticSourcePath = diagnosticSourcePath ?? sourcePath;
        ModuleIdentityAuthority = moduleIdentityAuthority
            ?? VbeModuleIdentityAuthority.Authoritative(importVerification.ComponentName);
    }

    /// <summary>
    /// Gets the staged .bas, .cls, or .frm source path.
    /// </summary>
    public string SourcePath { get; }

    /// <summary>
    /// Gets the expected VBA component kind.
    /// </summary>
    public VbaSourceKind Kind { get; }

    /// <summary>
    /// Gets the staged .frx sidecar path for a form, when present.
    /// </summary>
    public string? BinaryPath { get; }

    /// <summary>
    /// Gets the exact component contract to verify after import.
    /// </summary>
    public VbeImportVerification ImportVerification { get; }

    /// <summary>
    /// Gets the caller-visible source path used in preflight diagnostics.
    /// </summary>
    public string DiagnosticSourcePath { get; }

    internal VbeModuleIdentityAuthority ModuleIdentityAuthority { get; }

    /// <summary>
    /// Gets the flat source file name presented to VBIDE.
    /// </summary>
    public string FileName => Path.GetFileName(SourcePath);
}

/// <summary>
/// Describes the exact component identity and code-module projection expected after import.
/// </summary>
public sealed record VbeImportVerification
{
    internal VbeImportVerification(
        string componentName,
        VbaSourceKind componentKind,
        IReadOnlyList<string> codeModuleLines,
        string originalEncoding)
    {
        ComponentName = componentName;
        ComponentKind = componentKind;
        CodeModuleLines = Array.AsReadOnly(codeModuleLines.ToArray());
        OriginalEncoding = originalEncoding;
    }

    /// <summary>
    /// Gets the exported VBA component name.
    /// </summary>
    public string ComponentName { get; }

    /// <summary>
    /// Gets the exported VBA component kind.
    /// </summary>
    public VbaSourceKind ComponentKind { get; }

    /// <summary>
    /// Gets the exact projected VBIDE CodeModule lines.
    /// </summary>
    public IReadOnlyList<string> CodeModuleLines { get; }

    /// <summary>
    /// Gets the canonical encoding token detected in caller-owned input.
    /// </summary>
    public string OriginalEncoding { get; }
}

/// <summary>
/// Captures the observable imported component facts used by the verification boundary.
/// </summary>
/// <param name="ComponentName">The actual VBComponent name.</param>
/// <param name="ComponentKind">The actual VBComponent kind.</param>
/// <param name="CodeModuleLines">Every actual CodeModule line in order.</param>
public sealed record VbeImportedComponent(
    string ComponentName,
    VbaSourceKind ComponentKind,
    IReadOnlyList<string> CodeModuleLines);

/// <summary>
/// Verifies that VBIDE imported one component without changing its projected identity or code,
/// except for accepted identifier-only recasing.
/// </summary>
public static class VbeImportedComponentVerifier
{
    /// <summary>
    /// Requires exact component name, kind, and line structure. Returns one non-fatal warning
    /// only when every projected-code difference is accepted identifier recasing.
    /// </summary>
    public static VbeIdentifierRecasingWarning? Verify(
        VbeImportVerification expected,
        VbeImportedComponent actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (!expected.ComponentName.Equals(actual.ComponentName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"VBIDE imported component name '{actual.ComponentName}' instead of expected '{expected.ComponentName}'.");
        }

        if (expected.ComponentKind != actual.ComponentKind)
        {
            throw new InvalidOperationException(
                $"VBIDE imported component '{actual.ComponentName}' as kind '{actual.ComponentKind}' instead of expected '{expected.ComponentKind}'.");
        }

        if (expected.CodeModuleLines.Count != actual.CodeModuleLines.Count)
        {
            throw new InvalidOperationException(
                $"VBIDE imported component '{actual.ComponentName}' with line count {actual.CodeModuleLines.Count} instead of expected {expected.CodeModuleLines.Count}.");
        }

        for (var index = 0; index < expected.CodeModuleLines.Count; index++)
        {
            if (!expected.CodeModuleLines[index].Equals(
                actual.CodeModuleLines[index],
                StringComparison.Ordinal))
            {
                if (VbeIdentifierRecasingClassifier.TryClassify(
                        expected,
                        actual,
                        out var pairs))
                {
                    return new VbeIdentifierRecasingWarning(
                        actual.ComponentName,
                        pairs);
                }

                throw new InvalidOperationException(
                    $"VBIDE imported component '{actual.ComponentName}' with unexpected text at line {index + 1}.");
            }
        }

        return null;
    }
}
