using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

public sealed class VbaDevSnapshotWorkbookBuilder : IVbaDebugWorkbookBuilder
{
    private readonly IVbaDevBuildProcess buildProcess;
    private readonly TransportedDebugSourceSnapshotValidator snapshotValidator;

    public VbaDevSnapshotWorkbookBuilder(
        IVbaDevBuildProcess buildProcess)
        : this(
            buildProcess,
            TransportedDebugSourceSnapshotValidator.CreateForCurrentWindowsSession())
    {
    }

    public VbaDevSnapshotWorkbookBuilder(
        IVbaDevBuildProcess buildProcess,
        TransportedDebugSourceSnapshotValidator snapshotValidator)
    {
        this.buildProcess = buildProcess ?? throw new ArgumentNullException(nameof(buildProcess));
        this.snapshotValidator = snapshotValidator
            ?? throw new ArgumentNullException(nameof(snapshotValidator));
    }

    public async Task<VbaDevSnapshotBuildResult> BuildAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceLease);
        ValidateRequest(vbaDevPath, request);
        var validatedSnapshot = snapshotValidator.Validate(request.SourceSnapshot);
        IVbaDebugGenerationWorkspace? generationWorkspace = null;
        try
        {
            generationWorkspace = workspaceLease.CreateGenerationWorkspace(
                request.GenerationId,
                request.WorkbookFileName);
            var sourceSnapshotPath = generationWorkspace.SourceSnapshotPath;
            var workbookPath = generationWorkspace.WorkbookPath;
            MaterializeSnapshot(
                validatedSnapshot,
                generationWorkspace);
            generationWorkspace.SealSourceSnapshot();
            var arguments = new[]
            {
                "build",
                "--project", Path.GetFullPath(request.ProjectRoot),
                "--document", request.DocumentName,
                "--source-snapshot", sourceSnapshotPath,
                "--output", workbookPath
            };
            var processResult = await buildProcess
                .RunAsync(Path.GetFullPath(vbaDevPath), arguments, cancellationToken)
                .ConfigureAwait(false);
            if (processResult.ExitCode != 0)
            {
                var diagnostics = new List<string>
                {
                    $"vba-dev snapshot build exited with code {processResult.ExitCode}."
                };
                if (!string.IsNullOrWhiteSpace(processResult.StandardOutput))
                {
                    diagnostics.Add($"stdout:{Environment.NewLine}{processResult.StandardOutput.TrimEnd()}");
                }
                if (!string.IsNullOrWhiteSpace(processResult.StandardError))
                {
                    diagnostics.Add($"stderr:{Environment.NewLine}{processResult.StandardError.TrimEnd()}");
                }
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, diagnostics));
            }
            generationWorkspace.VerifySourceSnapshot();
            if (!WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(workbookPath))
            {
                throw new InvalidOperationException(
                    "vba-dev reported a successful snapshot build without producing the requested workbook.");
            }
            generationWorkspace.PinGeneratedWorkbook();

            var result = new VbaDevSnapshotBuildResult(generationWorkspace)
            {
                Output = SplitOutput(processResult.StandardOutput)
                    .Concat(SplitOutput(processResult.StandardError))
                    .ToArray()
            };
            generationWorkspace = null;
            return result;
        }
        catch
        {
            if (generationWorkspace is not null)
            {
                try
                {
                    await generationWorkspace.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The original build failure remains authoritative.
                }
            }
            throw;
        }
    }

    private static string[] SplitOutput(string output)
        => output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void ValidateRequest(
        string vbaDevPath,
        VbaDevSnapshotBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(vbaDevPath) || !File.Exists(vbaDevPath))
        {
            throw new ArgumentException(
                "The supplied vba-dev path must identify an existing absolute file.",
                nameof(vbaDevPath));
        }
        ArgumentNullException.ThrowIfNull(request.GenerationId);
        if (!Path.IsPathFullyQualified(request.ProjectRoot) || !Directory.Exists(request.ProjectRoot))
        {
            throw new ArgumentException(
                "The snapshot build project root must identify an existing absolute directory.",
                nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.DocumentName))
        {
            throw new ArgumentException("The snapshot build document name is required.", nameof(request));
        }
        if (
            !WindowsVbaDebugWorkspacePath.IsUnambiguousEntryName(
                request.WorkbookFileName) ||
            !string.Equals(
                Path.GetFileName(request.WorkbookFileName),
                request.WorkbookFileName,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetExtension(request.WorkbookFileName),
                ".xlsm",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The debug workbook file name must be a path-free .xlsm file name.",
                nameof(request));
        }
        if (request.SourceSnapshot.SchemaVersion != 2)
        {
            throw new ArgumentException(
                $"Unsupported source snapshot schema version {request.SourceSnapshot.SchemaVersion}.",
                nameof(request));
        }
    }

    private static void MaterializeSnapshot(
        ValidatedTransportedDebugSourceSnapshot snapshot,
        IVbaDebugGenerationWorkspace generationWorkspace)
    {
        foreach (var source in snapshot.Sources)
        {
            using var stream = generationWorkspace.CreateSourceFile(source.RelativePath);
            stream.Write(source.Bytes);
            stream.Flush(flushToDisk: true);
        }
    }

}

public sealed record VbaDevSnapshotBuildRequest(
    string ProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    TransportedDebugSourceSnapshot SourceSnapshot)
{
    public DebugGenerationId GenerationId { get; init; } = DebugGenerationId.Initial;
}

public sealed record TransportedDebugSourceSnapshot(
    int SchemaVersion,
    IReadOnlyList<TransportedDebugSource> Sources)
{
    public TransportedDebugSourcePosition? ActiveSource { get; init; }

    public IReadOnlyList<TransportedDebugSourceBreakpoint> Breakpoints { get; init; } = [];
}

public sealed record TransportedDebugSource(
    string RelativePath,
    string? SourceUri,
    string? Encoding,
    string ContentBase64);

public sealed record TransportedDebugSourcePosition(
    string SourceUri,
    int Line,
    int Character);

public sealed record TransportedDebugSourceBreakpoint(
    string SourceUri,
    int Line);

public sealed class VbaDevSnapshotBuildResult : IAsyncDisposable
{
    private IVbaDebugGenerationWorkspace? generationWorkspace;

    public VbaDevSnapshotBuildResult(
        IVbaDebugGenerationWorkspace generationWorkspace)
    {
        this.generationWorkspace = generationWorkspace
            ?? throw new ArgumentNullException(nameof(generationWorkspace));
        GenerationId = generationWorkspace.GenerationId;
        GenerationWorkspacePath = Path.GetFullPath(
            generationWorkspace.GenerationWorkspacePath);
        SourceSnapshotPath = Path.GetFullPath(
            generationWorkspace.SourceSnapshotPath);
        WorkbookPath = Path.GetFullPath(generationWorkspace.WorkbookPath);
    }

    public DebugGenerationId GenerationId { get; }

    public string GenerationWorkspacePath { get; }

    public string SourceSnapshotPath { get; }

    public string WorkbookPath { get; }

    public IReadOnlyList<string> Output { get; init; } = [];

    internal IVbaDebugGenerationWorkspace TransferGenerationOwnership()
        => Interlocked.Exchange(ref generationWorkspace, null)
            ?? throw new InvalidOperationException(
                "The debug generation workspace ownership has already been transferred or disposed.");

    public async ValueTask DisposeAsync()
    {
        var ownedWorkspace = Interlocked.Exchange(ref generationWorkspace, null);
        if (ownedWorkspace is not null)
        {
            await ownedWorkspace.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed record VbaDevBuildProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IVbaDevBuildProcess
{
    Task<VbaDevBuildProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public interface IVbaDebugWorkbookBuilder
{
    Task<VbaDevSnapshotBuildResult> BuildAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken);
}
