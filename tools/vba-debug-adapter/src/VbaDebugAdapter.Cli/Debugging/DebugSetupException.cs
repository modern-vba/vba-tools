namespace VbaDebugAdapter.Debugging;

public class DebugSetupException : Exception
{
    internal VbeNativeFailureFacts? NativeFailure { get; private set; }

    internal DebugSetupException WithNativeFailure(
        VbeNativeFailureFacts facts)
    {
        NativeFailure = facts;
        return this;
    }

    public DebugSetupException(string message)
        : base(message)
    {
    }

    public DebugSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
