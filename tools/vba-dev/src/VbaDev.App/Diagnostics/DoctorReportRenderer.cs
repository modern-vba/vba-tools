using System.Text;
using VbaDev.App.Cli;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Renders doctor diagnostics into the command result contract.
/// </summary>
public sealed class DoctorReportRenderer
{
    /// <summary>
    /// Renders a doctor command result.
    /// </summary>
    /// <param name="results">The diagnostic results to render.</param>
    /// <returns>The doctor command result.</returns>
    public CommandResult Render(IReadOnlyList<DiagnosticResult> results)
    {
        var output = RenderText(results);
        var exitCode = results.Any(result => result.Status == DiagnosticStatus.Fail) ? 1 : 0;
        return new CommandResult(exitCode, output, string.Empty);
    }

    private static string RenderText(IReadOnlyList<DiagnosticResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-dev doctor");
        builder.AppendLine();
        foreach (var result in results)
        {
            builder.AppendLine($"[{RenderStatus(result.Status)}] {result.Name}: {result.Message}");
        }

        return builder.ToString();
    }

    private static string RenderStatus(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => "PASS",
            DiagnosticStatus.Warn => "WARN",
            DiagnosticStatus.Fail => "FAIL",
            DiagnosticStatus.Skip => "SKIP",
            _ => status.ToString().ToUpperInvariant()
        };
}
