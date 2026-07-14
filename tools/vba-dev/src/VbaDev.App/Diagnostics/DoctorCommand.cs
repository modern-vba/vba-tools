using VbaDev.App.Cli;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Runs doctor diagnostics and renders the command report.
/// </summary>
public sealed class DoctorCommand
{
    private readonly DoctorDiagnosticPipeline diagnosticPipeline;
    private readonly DoctorReportRenderer reportRenderer;

    /// <summary>
    /// Creates the doctor command.
    /// </summary>
    /// <param name="diagnosticPipeline">The pipeline that collects doctor diagnostics.</param>
    /// <param name="reportRenderer">The renderer that maps diagnostics to command output.</param>
    public DoctorCommand(
        DoctorDiagnosticPipeline diagnosticPipeline,
        DoctorReportRenderer reportRenderer)
    {
        this.diagnosticPipeline = diagnosticPipeline;
        this.reportRenderer = reportRenderer;
    }

    /// <summary>
    /// Runs doctor diagnostics and formats the combined report.
    /// </summary>
    /// <param name="request">The doctor command input.</param>
    /// <returns>A command result whose exit code fails only when at least one diagnostic fails.</returns>
    public CommandResult Run(DoctorCommandRequest request)
        => reportRenderer.Render(diagnosticPipeline.Run(request));
}
