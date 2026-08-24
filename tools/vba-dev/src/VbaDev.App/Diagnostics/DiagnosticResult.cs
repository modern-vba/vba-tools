namespace VbaDev.App.Diagnostics;

/// <summary>
/// Contains one named doctor diagnostic result.
/// </summary>
/// <param name="Status">The diagnostic status used for rendering and exit-code decisions.</param>
/// <param name="Name">The stable diagnostic name shown in output.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
public sealed record DiagnosticResult(DiagnosticStatus Status, string Name, string Message)
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyDetails =
        new Dictionary<string, object?>();

    /// <summary>
    /// Gets stable machine-readable evidence for this check.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Details { get; init; } = EmptyDetails;

    /// <summary>
    /// Gets the stable machine-readable check identity.
    /// </summary>
    public string Id { get; init; } = Name;

    /// <summary>
    /// Gets the measured check duration in milliseconds.
    /// </summary>
    public long DurationMilliseconds { get; init; }

    /// <summary>
    /// Creates a passing diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A passing diagnostic result.</returns>
    public static DiagnosticResult Pass(string name, string message) => new(DiagnosticStatus.Pass, name, message);

    public static DiagnosticResult Pass(string id, string name, string message)
        => new(DiagnosticStatus.Pass, name, message) { Id = id };

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A warning diagnostic result.</returns>
    public static DiagnosticResult Warn(string name, string message) => new(DiagnosticStatus.Warn, name, message);

    public static DiagnosticResult Warn(string id, string name, string message)
        => new(DiagnosticStatus.Warn, name, message) { Id = id };

    /// <summary>
    /// Creates a failing diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A failing diagnostic result.</returns>
    public static DiagnosticResult Fail(string name, string message) => new(DiagnosticStatus.Fail, name, message);

    public static DiagnosticResult Fail(string id, string name, string message)
        => new(DiagnosticStatus.Fail, name, message) { Id = id };

    /// <summary>
    /// Creates an inconclusive diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The human-readable diagnostic message.</param>
    /// <returns>An unverified diagnostic result.</returns>
    public static DiagnosticResult Unverified(string name, string message)
        => new(DiagnosticStatus.Unverified, name, message);

    public static DiagnosticResult Unverified(string id, string name, string message)
        => new(DiagnosticStatus.Unverified, name, message) { Id = id };

    /// <summary>
    /// Creates a skipped diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A skipped diagnostic result.</returns>
    public static DiagnosticResult Skip(string name, string message) => new(DiagnosticStatus.Skip, name, message);

    public static DiagnosticResult Skip(string id, string name, string message)
        => new(DiagnosticStatus.Skip, name, message) { Id = id };
}
