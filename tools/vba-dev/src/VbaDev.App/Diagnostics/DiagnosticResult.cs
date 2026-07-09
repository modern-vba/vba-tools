namespace VbaDev.App.Diagnostics;

public sealed record DiagnosticResult(DiagnosticStatus Status, string Name, string Message)
{
    public static DiagnosticResult Pass(string name, string message) => new(DiagnosticStatus.Pass, name, message);

    public static DiagnosticResult Warn(string name, string message) => new(DiagnosticStatus.Warn, name, message);

    public static DiagnosticResult Fail(string name, string message) => new(DiagnosticStatus.Fail, name, message);

    public static DiagnosticResult Skip(string name, string message) => new(DiagnosticStatus.Skip, name, message);
}
