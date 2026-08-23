namespace VbaDebugAdapter.Debugging;

public class DebugSetupException : Exception
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
