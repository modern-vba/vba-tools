namespace VbaDebugAdapter.Debugging;

/// <summary>An explicitly recognized rejection of the transported source input.</summary>
internal sealed class DebugSourceRejectedException : DebugSetupException
{
    internal DebugSourceRejectedException(string message) : base(message) { }

    internal DebugSourceRejectedException(string message, Exception cause) : base(message, cause) { }
}
