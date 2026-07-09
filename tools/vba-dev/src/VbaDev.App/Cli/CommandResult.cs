namespace VbaDev.App.Cli;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public static CommandResult Success(string output) => new(0, output, string.Empty);

    public static CommandResult Failure(string output) => new(1, output, string.Empty);

    public static CommandResult UsageError(string error) => new(1, string.Empty, error + Environment.NewLine);

    public static CommandResult NotImplemented(string message) => new(2, string.Empty, message + Environment.NewLine);
}
