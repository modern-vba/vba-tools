namespace VbaDev.App.Cli;

public static class CommandErrorMessages
{
    public static string ExcelComAutomationFailed(string operation, Exception exception)
        => string.Join(
            Environment.NewLine,
            $"Excel COM {operation} automation failed: {exception.Message}",
            "Excel automation requires an interactive Windows desktop session with permission to start and control Excel.",
            "If this was launched by a coding agent, CI job, or sandboxed shell, rerun it with Excel/COM automation allowed or outside the sandbox.");

    public static string UnexpectedFailure(Exception exception)
        => string.Join(
            Environment.NewLine,
            $"vba-dev failed unexpectedly: {exception.GetType().Name}: {exception.Message}",
            "Windows may report this as 0xE0434352, the generic .NET unhandled-exception code.",
            "If this was launched by a coding agent, CI job, or sandboxed shell, check whether the sandbox blocked Excel/COM automation or required file access.");
}
