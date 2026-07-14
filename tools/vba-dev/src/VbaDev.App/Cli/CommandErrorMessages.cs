namespace VbaDev.App.Cli;

/// <summary>
/// Formats shared command error messages for expected infrastructure failures.
/// </summary>
public static class CommandErrorMessages
{
    /// <summary>
    /// Formats a failure from Excel COM automation with environment guidance.
    /// </summary>
    /// <param name="operation">The command operation that was using Excel automation.</param>
    /// <param name="exception">The COM exception reported by the automation layer.</param>
    /// <returns>A multi-line user-facing error message.</returns>
    public static string ExcelComAutomationFailed(string operation, Exception exception)
        => string.Join(
            Environment.NewLine,
            $"Excel COM {operation} automation failed: {exception.Message}",
            "Excel automation requires an interactive Windows desktop session with permission to start and control Excel.",
            "If this was launched by a coding agent, CI job, or sandboxed shell, rerun it with Excel/COM automation allowed or outside the sandbox.");

    /// <summary>
    /// Formats an unhandled failure with context for common .NET and sandbox symptoms.
    /// </summary>
    /// <param name="exception">The exception that escaped command handling.</param>
    /// <returns>A multi-line user-facing error message.</returns>
    public static string UnexpectedFailure(Exception exception)
        => string.Join(
            Environment.NewLine,
            $"vba-dev failed unexpectedly: {exception.GetType().Name}: {exception.Message}",
            "Windows may report this as 0xE0434352, the generic .NET unhandled-exception code.",
            "If this was launched by a coding agent, CI job, or sandboxed shell, check whether the sandbox blocked Excel/COM automation or required file access.");
}
