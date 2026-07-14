namespace VbaDev.App.Cli;

/// <summary>
/// Contains the process exit code and streams produced by a tooling command.
/// </summary>
/// <param name="ExitCode">The process exit code to return from the CLI entry point.</param>
/// <param name="StandardOutput">The text to write to standard output.</param>
/// <param name="StandardError">The text to write to standard error.</param>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Creates a successful command result.
    /// </summary>
    /// <param name="output">The standard output text.</param>
    /// <returns>A zero-exit-code result.</returns>
    public static CommandResult Success(string output) => new(0, output, string.Empty);

    /// <summary>
    /// Creates a failed command result that writes failure details to standard output.
    /// </summary>
    /// <param name="output">The standard output text.</param>
    /// <returns>A nonzero result for command-level failures.</returns>
    public static CommandResult Failure(string output) => new(1, output, string.Empty);

    /// <summary>
    /// Creates a usage error that writes the message to standard error.
    /// </summary>
    /// <param name="error">The user-facing usage or validation error.</param>
    /// <returns>A nonzero result for invalid command input.</returns>
    public static CommandResult UsageError(string error) => new(1, string.Empty, error + Environment.NewLine);

    /// <summary>
    /// Creates a result for command surfaces that are intentionally not implemented yet.
    /// </summary>
    /// <param name="message">The user-facing not-implemented message.</param>
    /// <returns>A result with the not-implemented exit code.</returns>
    public static CommandResult NotImplemented(string message) => new(2, string.Empty, message + Environment.NewLine);
}
