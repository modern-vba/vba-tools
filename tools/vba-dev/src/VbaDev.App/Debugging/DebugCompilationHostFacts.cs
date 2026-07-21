namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies the processor architecture of the owned Excel process.
/// </summary>
public enum DebugExcelProcessArchitecture
{
    /// <summary>
    /// The process architecture could not be established.
    /// </summary>
    Unknown,

    /// <summary>
    /// The owned Excel process is 32-bit x86.
    /// </summary>
    X86,

    /// <summary>
    /// The owned Excel process is 64-bit x64.
    /// </summary>
    X64,

    /// <summary>
    /// The owned Excel process is 64-bit Arm64.
    /// </summary>
    Arm64
}

/// <summary>
/// Describes whether actual host facts prove the VBA compiler built-ins.
/// </summary>
public enum DebugCompilationHostFactsStatus
{
    /// <summary>
    /// One or more required host facts are unknown.
    /// </summary>
    Unknown,

    /// <summary>
    /// All required host facts agree and prove the built-ins.
    /// </summary>
    Verified,

    /// <summary>
    /// Independently observed host facts contradict one another.
    /// </summary>
    Mismatch
}

/// <summary>
/// Contains the compatibility compiler constants proved by an actual Excel/VBE host.
/// </summary>
public sealed record DebugCompilerBuiltInConstants(
    bool Vba6,
    bool Vba7,
    bool Win16,
    bool Win32,
    bool Win64,
    bool Mac);

/// <summary>
/// Contains independently observed facts from the owned Excel/VBE process.
/// </summary>
/// <param name="ExcelVersion">The raw Excel <c>Application.Version</c> value.</param>
/// <param name="VbeVersion">The raw <c>Application.VBE.Version</c> value.</param>
/// <param name="OperatingSystem">The raw Excel <c>Application.OperatingSystem</c> value.</param>
/// <param name="ExcelProcessArchitecture">The architecture read from the exact owned Excel process.</param>
/// <param name="Status">Whether the facts prove a consistent compiler environment.</param>
/// <param name="BuiltInConstants">The proved compatibility constants, or <see langword="null"/> when unproved.</param>
/// <param name="UnavailableReason">A diagnostic reason when built-ins are unproved.</param>
public sealed record DebugCompilationHostFacts(
    string ExcelVersion,
    string VbeVersion,
    string OperatingSystem,
    DebugExcelProcessArchitecture ExcelProcessArchitecture,
    DebugCompilationHostFactsStatus Status,
    DebugCompilerBuiltInConstants? BuiltInConstants,
    string? UnavailableReason);
