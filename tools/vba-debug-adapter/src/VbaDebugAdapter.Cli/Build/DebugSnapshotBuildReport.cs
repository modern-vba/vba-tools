using System.Collections.Immutable;

namespace VbaDebugAdapter.Build;

/// <summary>Retains one companion invocation's output and admitted source origins beyond scratch cleanup.</summary>
public sealed class DebugSnapshotBuildReport
{
    private DebugSnapshotBuildReport(VbaDevSnapshotBuildRequest request,
        ImmutableArray<DebugSnapshotSourceOrigin> origins, VbaDevBuildProcessResult result)
    {
        ProjectRoot = Path.GetFullPath(request.ProjectRoot);
        DocumentName = request.DocumentName;
        Generation = request.GenerationId.Value;
        Origins = origins;
        ExitCode = result.ExitCode;
        Stdout = result.StandardOutput;
        Stderr = result.StandardError;
    }

    internal static DebugSnapshotBuildReport Capture(VbaDevSnapshotBuildRequest request,
        ImmutableArray<DebugSnapshotSourceOrigin> origins, VbaDevBuildProcessResult result)
        => new(request, origins, result);

    public string SchemaVersion => "1.0";
    public string ProjectRoot { get; }
    public string DocumentName { get; }
    public int Generation { get; }
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public ImmutableArray<DebugSnapshotSourceOrigin> Origins { get; }
}

public sealed record DebugSnapshotSourceOrigin(string SnapshotUri, string? SourceUri);

internal sealed class SnapshotBuildFailedException(DebugSnapshotBuildReport report, string message)
    : InvalidOperationException(message)
{
    internal DebugSnapshotBuildReport Report { get; } = report;
}
