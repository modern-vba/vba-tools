using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Actively verifies Excel automation readiness in one dedicated owned process.
/// </summary>
public sealed class ExcelEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
{
    private readonly IWorkbookGenerationAutomation workbookAutomation;
    private readonly Func<string> createProbeWorkbook;
    private readonly Action<string> deleteProbeWorkbook;
    private readonly Func<bool> isWindows;

    /// <summary>
    /// Creates the production Excel environment diagnostics adapter.
    /// </summary>
    public ExcelEnvironmentDiagnosticPort()
        : this(
            new ExcelComWorkbookGenerationAutomation(),
            ExcelBootstrapWorkbookFile.Create,
            ExcelBootstrapWorkbookFile.Delete,
            OperatingSystem.IsWindows)
    {
    }

    internal ExcelEnvironmentDiagnosticPort(
        IWorkbookGenerationAutomation workbookAutomation,
        Func<string> createProbeWorkbook,
        Action<string> deleteProbeWorkbook,
        Func<bool> isWindows)
    {
        this.workbookAutomation = workbookAutomation;
        this.createProbeWorkbook = createProbeWorkbook;
        this.deleteProbeWorkbook = deleteProbeWorkbook;
        this.isWindows = isWindows;
    }

    /// <inheritdoc />
    public async Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (!isWindows())
        {
            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Fail(
                    "platform.windows",
                    "Active Excel automation diagnostics require Windows."),
                DiagnosticResult.Skip(
                    "excel.comStartup",
                    "The check requires a supported Windows platform."),
                DiagnosticResult.Skip(
                    "excel.processOwnership",
                    "The check requires a supported Windows platform."),
                DiagnosticResult.Skip(
                    "excel.vbideProjectAccess",
                    "The check requires a supported Windows platform."),
                DiagnosticResult.Skip(
                    "excel.processCleanup",
                    "No owned Excel process was started.")
            ]);
        }

        string? probeWorkbookPath = null;
        DiagnosticResult? vbideResult = null;
        try
        {
            probeWorkbookPath = createProbeWorkbook();
            await workbookAutomation.RunAsync(
                probeWorkbookPath,
                WorkbookAutomationTimeouts.Default with
                {
                    WorkbookOpen = TimeSpan.FromSeconds(60),
                    ModuleImport = TimeSpan.FromSeconds(60)
                },
                async (session, operationCancellationToken) =>
                {
                    try
                    {
                        await session.GetModulesAsync(operationCancellationToken)
                            .ConfigureAwait(false);
                        vbideResult = DiagnosticResult.Pass(
                            "excel.vbideProjectAccess",
                            "The owned workbook VBProject was accessed successfully.");
                    }
                    catch (COMException exception)
                    {
                        vbideResult = DiagnosticResult.Fail(
                            "excel.vbideProjectAccess",
                            $"The owned workbook VBProject could not be accessed: {exception.Message}");
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Pass(
                    "platform.windows",
                    "Windows supports active Excel automation diagnostics."),
                DiagnosticResult.Pass(
                    "excel.comStartup",
                    "A dedicated Excel COM instance started successfully."),
                DiagnosticResult.Pass(
                    "excel.processOwnership",
                    "The Excel process is exclusively owned by this diagnostic invocation."),
                vbideResult!,
                DiagnosticResult.Pass(
                    "excel.processCleanup",
                    "The owned Excel process was released successfully.")
            ]);
        }
        catch (WorkbookAutomationTimeoutException exception)
            when (exception.Stage.Kind == WorkbookAutomationStageKind.ExcelStartup)
        {
            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Pass(
                    "platform.windows",
                    "Windows supports active Excel automation diagnostics."),
                DiagnosticResult.Unverified(
                    "excel.comStartup",
                    exception.Message),
                DiagnosticResult.Skip(
                    "excel.processOwnership",
                    "Excel startup did not reach a conclusive result."),
                DiagnosticResult.Skip(
                    "excel.vbideProjectAccess",
                    "The check requires conclusive Excel startup and process ownership."),
                DiagnosticResult.Pass(
                    "excel.processCleanup",
                    "Owned-process cleanup completed after the startup timeout.")
            ]);
        }
        catch (WorkbookAutomationTimeoutException exception)
        {
            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Pass(
                    "platform.windows",
                    "Windows supports active Excel automation diagnostics."),
                DiagnosticResult.Pass(
                    "excel.comStartup",
                    "A dedicated Excel COM instance started successfully."),
                DiagnosticResult.Pass(
                    "excel.processOwnership",
                    "The Excel process is exclusively owned by this diagnostic invocation."),
                vbideResult ?? DiagnosticResult.Unverified(
                    "excel.vbideProjectAccess",
                    exception.Message),
                DiagnosticResult.Pass(
                    "excel.processCleanup",
                    "Owned-process cleanup completed after the active probe timeout.")
            ]);
        }
        catch (WorkbookAutomationCleanupException exception)
        {
            var reachedWorkbook = vbideResult is not null;
            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Pass(
                    "platform.windows",
                    "Windows supports active Excel automation diagnostics."),
                reachedWorkbook
                    ? DiagnosticResult.Pass(
                        "excel.comStartup",
                        "A dedicated Excel COM instance started successfully.")
                    : DiagnosticResult.Unverified(
                        "excel.comStartup",
                        "Excel startup did not reach a conclusive result."),
                reachedWorkbook
                    ? DiagnosticResult.Pass(
                        "excel.processOwnership",
                        "The Excel process is exclusively owned by this diagnostic invocation.")
                    : DiagnosticResult.Skip(
                        "excel.processOwnership",
                        "Excel startup did not reach a conclusive result."),
                vbideResult ?? DiagnosticResult.Skip(
                    "excel.vbideProjectAccess",
                    "The check requires conclusive Excel startup and process ownership."),
                DiagnosticResult.Unverified(
                    "excel.processCleanup",
                    exception.Message)
            ],
            Complete: false);
        }
        catch (WorkbookAutomationCanceledException exception)
        {
            var reachedWorkbook = vbideResult is not null;
            var startupCompleted = reachedWorkbook ||
                                   exception.Stage.Kind != WorkbookAutomationStageKind.ExcelStartup;
            return new EnvironmentDiagnosticRun(
            [
                DiagnosticResult.Pass(
                    "platform.windows",
                    "Windows supports active Excel automation diagnostics."),
                startupCompleted
                    ? DiagnosticResult.Pass(
                        "excel.comStartup",
                        "A dedicated Excel COM instance started successfully.")
                    : DiagnosticResult.Unverified(
                        "excel.comStartup",
                        exception.Message),
                startupCompleted
                    ? DiagnosticResult.Pass(
                        "excel.processOwnership",
                        "The Excel process is exclusively owned by this diagnostic invocation.")
                    : DiagnosticResult.Skip(
                        "excel.processOwnership",
                        "Excel startup did not reach a conclusive result."),
                vbideResult ?? (startupCompleted
                    ? DiagnosticResult.Unverified(
                        "excel.vbideProjectAccess",
                        "The active VBIDE check did not complete before cancellation.")
                    : DiagnosticResult.Skip(
                        "excel.vbideProjectAccess",
                        "The VBIDE check requires conclusive Excel startup and process ownership.")),
                DiagnosticResult.Pass(
                    "excel.processCleanup",
                    "The owned Excel process was released after cancellation.")
            ],
            Complete: false,
            Canceled: true);
        }
        finally
        {
            if (probeWorkbookPath is not null)
            {
                deleteProbeWorkbook(probeWorkbookPath);
            }
        }
    }
}
