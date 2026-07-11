namespace VbaDev.App.Diagnostics;

/// <summary>
/// Represents the severity and execution state of one environment diagnostic.
/// </summary>
public enum DiagnosticStatus
{
    /// <summary>
    /// The diagnostic check passed.
    /// </summary>
    Pass,

    /// <summary>
    /// The diagnostic found a condition that may require attention but does not fail the command.
    /// </summary>
    Warn,

    /// <summary>
    /// The diagnostic found a condition that makes doctor return a failing exit code.
    /// </summary>
    Fail,

    /// <summary>
    /// The diagnostic could not run because a prerequisite context was unavailable.
    /// </summary>
    Skip
}
