namespace VbaDev.Infrastructure.Debugging;

internal class DebugSetupException : Exception
{
    public DebugSetupException(string message)
        : base(message)
    {
    }

    public DebugSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record DebugProcessExit(int ExitCode);

internal enum DebugExcelProcessArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}
