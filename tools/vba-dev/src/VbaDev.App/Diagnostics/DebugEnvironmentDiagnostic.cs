using VbaDev.App.Debugging;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Orchestrates the fail-closed native Excel/VBE readiness checks used by doctor.
/// </summary>
public sealed class DebugEnvironmentDiagnostic : IEnvironmentDiagnosticPort
{
    private const string CleanupDiagnosticName = "Temporary debug probe cleanup";

    private readonly IDebugEnvironmentProbeFactory probeFactory;
    private readonly Func<bool> isWindows;
    private readonly TimeSpan timeout;

    /// <summary>
    /// Creates the native debug environment diagnostic.
    /// </summary>
    public DebugEnvironmentDiagnostic(
        IDebugEnvironmentProbeFactory probeFactory,
        Func<bool> isWindows,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(probeFactory);
        ArgumentNullException.ThrowIfNull(isWindows);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.probeFactory = probeFactory;
        this.isWindows = isWindows;
        this.timeout = timeout;
    }

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics()
    {
        var results = new List<DiagnosticResult>
        {
            DiagnosticResult.Pass(
                "VBA debug capability contract",
                $"Protocol {VbaDebugCapabilityContract.ProtocolVersion} over " +
                $"{VbaDebugCapabilityContract.Transport} via '{VbaDebugCapabilityContract.AdapterCommand}'.")
        };

        if (!isWindows())
        {
            results.Add(DiagnosticResult.Fail(
                "Windows VBE debugging",
                "Native VBE debugging is unsupported on this operating system; Windows is required."));
            results.Add(DiagnosticResult.Pass(
                CleanupDiagnosticName,
                "No temporary probe state was created."));
            return results;
        }

        results.Add(DiagnosticResult.Pass(
            "Windows VBE debugging",
            "The operating system supports native Excel/VBE automation."));
        RunProbeAsync(results).GetAwaiter().GetResult();
        return results;
    }

    private async Task RunProbeAsync(List<DiagnosticResult> results)
    {
        IDebugEnvironmentProbeSession? session = null;
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            session = await probeFactory.StartAsync(timeoutSource.Token).ConfigureAwait(false);
            results.Add(DiagnosticResult.Pass(
                "Excel COM availability",
                "Microsoft Excel started through COM automation."));
            results.Add(DiagnosticResult.Pass(
                "Owned Excel process",
                $"The native debug probe owns Excel PID {session.ProcessId}."));
            results.Add(session.StrongProcessOwnershipEstablished
                ? DiagnosticResult.Pass(
                    "Windows Job ownership",
                    "The Excel process is assigned to a kill-on-close Windows Job Object.")
                : DiagnosticResult.Fail(
                    "Windows Job ownership",
                    "Kill-on-close Windows Job ownership was not established for the Excel process."));
            results.AddRange(session.StartupDiagnostics);
            if (!session.StrongProcessOwnershipEstablished)
            {
                return;
            }

            var probeResults = await session.RunAsync(timeoutSource.Token).ConfigureAwait(false);
            results.AddRange(probeResults);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            results.Add(DiagnosticResult.Fail(
                session is null ? "Excel COM availability" : "Native VBE readiness",
                $"The native debug readiness probe exceeded its {timeout.TotalSeconds:0.###}-second timeout."));
        }
        catch (DebugEnvironmentProbeStartException ex)
        {
            results.Add(timeoutSource.IsCancellationRequested &&
                        ex.InnerException is OperationCanceledException
                ? DiagnosticResult.Fail(
                    session is null ? "Excel COM availability" : "Native VBE readiness",
                    $"The native debug readiness probe exceeded its {timeout.TotalSeconds:0.###}-second timeout.")
                : DiagnosticResult.Fail(ex.DiagnosticName, ex.Message));
            if (ex.CleanupException is not null)
            {
                results.Add(DiagnosticResult.Fail(
                    CleanupDiagnosticName,
                    ex.CleanupException.Message));
            }
            else if (ex.CleanupVerified)
            {
                results.Add(DiagnosticResult.Pass(
                    CleanupDiagnosticName,
                    "All process and temporary file state created before startup failed was removed."));
            }
            else
            {
                results.Add(DiagnosticResult.Fail(
                    CleanupDiagnosticName,
                    "Cleanup was not verified after the native debug probe failed to start."));
            }
        }
        catch (Exception ex)
        {
            results.Add(DiagnosticResult.Fail(
                session is null ? "Excel COM availability" : "Native VBE readiness",
                ex.Message));
        }
        finally
        {
            if (session is null)
            {
                if (!results.Any(result =>
                        result.Name.Equals(CleanupDiagnosticName, StringComparison.Ordinal)))
                {
                    results.Add(DiagnosticResult.Fail(
                        CleanupDiagnosticName,
                        "Cleanup was not verified because the native debug probe did not return an owned session."));
                }
            }
            else
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    results.Add(DiagnosticResult.Pass(
                        CleanupDiagnosticName,
                        "The owned Excel process and all temporary probe artifacts were removed."));
                }
                catch (Exception ex)
                {
                    results.Add(DiagnosticResult.Fail(CleanupDiagnosticName, ex.Message));
                }
            }
        }
    }
}
