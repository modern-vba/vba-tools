namespace VbaDev.App.Debugging;

/// <summary>
/// Defines the versioned CLI and native VBE command contract used by VBA debugging.
/// </summary>
public static class VbaDebugCapabilityContract
{
    /// <summary>
    /// Gets the debug adapter protocol version exposed by <c>vba-dev capabilities</c>.
    /// </summary>
    public const string ProtocolVersion = "1.1";

    /// <summary>
    /// Gets the supported debug adapter transport.
    /// </summary>
    public const string Transport = "stdio";

    /// <summary>
    /// Gets the CLI command that starts the debug adapter.
    /// </summary>
    public const string AdapterCommand = "debug-adapter";

    /// <summary>
    /// Gets the built-in VBE Toggle Breakpoint command identifier.
    /// </summary>
    public const int ToggleBreakpointCommandId = 51;

    /// <summary>
    /// Gets the built-in VBE Run Sub/UserForm command identifier, which also continues from break mode.
    /// </summary>
    public const int RunOrContinueCommandId = 186;
}
