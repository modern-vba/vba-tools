using VbaDebugAdapter.Build;

namespace VbaDebugAdapter.Cli;

public sealed class ProcessVbaDevCapabilitiesProbe : IVbaDevCapabilitiesProbe
{
    private readonly IVbaDevBuildProcess processRunner;

    public ProcessVbaDevCapabilitiesProbe()
        : this(new ProcessVbaDevBuildProcess())
    {
    }

    internal ProcessVbaDevCapabilitiesProbe(IVbaDevBuildProcess processRunner)
    {
        this.processRunner = processRunner
            ?? throw new ArgumentNullException(nameof(processRunner));
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

        var processResult = await processRunner.RunAsync(
            Path.GetFullPath(vbaDevPath),
            ["capabilities", "--format", "json"],
            cancellationToken).ConfigureAwait(false);

        return new VbaDevCapabilitiesProbeResult(
            processResult.ExitCode,
            processResult.StandardOutput,
            processResult.StandardError);
    }
}

public sealed record VbaDevCapabilitiesProbeResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IVbaDevCapabilitiesProbe
{
    Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
        string vbaDevPath,
        CancellationToken cancellationToken);
}
