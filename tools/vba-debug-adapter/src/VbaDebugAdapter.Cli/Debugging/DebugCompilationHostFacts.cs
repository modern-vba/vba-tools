namespace VbaDebugAdapter.Debugging;

public enum DebugExcelProcessArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64
}

public enum DebugCompilationHostFactsStatus
{
    Unknown,
    Verified,
    Mismatch
}

public sealed record DebugCompilerBuiltInConstants(
    bool Vba6,
    bool Vba7,
    bool Win16,
    bool Win32,
    bool Win64,
    bool Mac);

public sealed record DebugCompilationHostFacts(
    string ExcelVersion,
    string VbeVersion,
    string OperatingSystem,
    DebugExcelProcessArchitecture ExcelProcessArchitecture,
    DebugCompilationHostFactsStatus Status,
    DebugCompilerBuiltInConstants? BuiltInConstants,
    string? UnavailableReason);
