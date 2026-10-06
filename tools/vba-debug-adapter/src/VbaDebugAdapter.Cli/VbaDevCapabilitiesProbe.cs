using VbaDebugAdapter.Build;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Cli;

public sealed class ProcessVbaDevCapabilitiesProbe : IVbaDevCapabilitiesProbe
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly IVbaDevBuildProcess processRunner;
    private readonly TimeSpan retryDelay;

    public ProcessVbaDevCapabilitiesProbe()
        : this(new ProcessVbaDevBuildProcess(), DefaultRetryDelay)
    {
    }

    internal ProcessVbaDevCapabilitiesProbe(IVbaDevBuildProcess processRunner)
        : this(processRunner, DefaultRetryDelay)
    {
    }

    internal ProcessVbaDevCapabilitiesProbe(
        IVbaDevBuildProcess processRunner,
        TimeSpan retryDelay)
    {
        this.processRunner = processRunner
            ?? throw new ArgumentNullException(nameof(processRunner));
        if (retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }
        this.retryDelay = retryDelay;
    }

    public async Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
        string vbaDevPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vbaDevPath);
        if (!Path.IsPathFullyQualified(vbaDevPath))
        {
            throw new ArgumentException(
                "The supplied vba-dev path must be absolute.",
                nameof(vbaDevPath));
        }

        var executablePath = Path.GetFullPath(vbaDevPath);
        var arguments = new[] { "capabilities", "--format", "json" };
        var first = await processRunner.RunAsync(
            executablePath, arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (first.ExitCode >= 0 || !HasProvedTerminalCleanup(first.CleanupOutcome))
        {
            return new VbaDevCapabilitiesProbeResult(
                first.ExitCode, first.StandardOutput, first.StandardError);
        }

        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        VbaDevBuildProcessResult second;
        try
        {
            second = await processRunner.RunAsync(
                executablePath, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            throw new VbaDevCapabilityRetryFailureException(
                executablePath, first.ExitCode, failure);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return new VbaDevCapabilitiesProbeResult(
            second.ExitCode,
            second.StandardOutput,
            second.StandardError)
        {
            AttemptCount = 2,
            FirstAbnormalExitCode = first.ExitCode
        };
    }

    private static bool HasProvedTerminalCleanup(DebugFailureOutcome? outcome)
        => outcome is { HasCleanupFailure: false } &&
            outcome.Evidence.Any(item => item.Kind == DebugResourceKind.Process &&
                item.Stage == "companion-exit" && item.Released);
}

internal sealed class VbaDevCapabilityRetryFailureException : InvalidOperationException,
    IDebugFailureEvidence
{
    public VbaDevCapabilityRetryFailureException(
        string executablePath,
        int firstAbnormalExitCode,
        Exception retryFailure)
        : base($"The vba-dev capabilities retry for '{executablePath}' failed after an " +
            $"abnormal first exit ({firstAbnormalExitCode}, " +
            $"0x{unchecked((uint)firstAbnormalExitCode):X8}).", retryFailure)
    {
        FirstAbnormalExitCode = firstAbnormalExitCode;
        FailureOutcome = retryFailure is IDebugFailureEvidence evidence
            ? evidence.FailureOutcome
            : new DebugFailureCompletion(retryFailure).Complete();
    }

    public int FirstAbnormalExitCode { get; }

    public DebugFailureOutcome FailureOutcome { get; }
}

public sealed record VbaDevCapabilitiesProbeResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    internal int AttemptCount { get; init; } = 1;
    internal int? FirstAbnormalExitCode { get; init; }
}

public interface IVbaDevCapabilitiesProbe
{
    Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
        string vbaDevPath,
        CancellationToken cancellationToken);
}
