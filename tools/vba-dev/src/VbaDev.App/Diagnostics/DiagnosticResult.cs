namespace VbaDev.App.Diagnostics;

/// <summary>
/// Contains one named doctor diagnostic result.
/// </summary>
/// <param name="Status">The diagnostic status used for rendering and exit-code decisions.</param>
/// <param name="Name">The stable diagnostic name shown in output.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
public sealed record DiagnosticResult(DiagnosticStatus Status, string Name, string Message)
{
    /// <summary>
    /// Creates a passing diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A passing diagnostic result.</returns>
    public static DiagnosticResult Pass(string name, string message) => new(DiagnosticStatus.Pass, name, message);

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A warning diagnostic result.</returns>
    public static DiagnosticResult Warn(string name, string message) => new(DiagnosticStatus.Warn, name, message);

    /// <summary>
    /// Creates a failing diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A failing diagnostic result.</returns>
    public static DiagnosticResult Fail(string name, string message) => new(DiagnosticStatus.Fail, name, message);

    /// <summary>
    /// Creates a skipped diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A skipped diagnostic result.</returns>
    public static DiagnosticResult Skip(string name, string message) => new(DiagnosticStatus.Skip, name, message);
}
