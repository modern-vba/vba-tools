using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

public sealed class VbaDevSnapshotWorkbookBuilder : IVbaDebugWorkbookBuilder
{
    private readonly IVbaDevBuildProcess buildProcess;
    private readonly TransportedDebugSourceSnapshotValidator snapshotValidator;
    private readonly Lazy<BuilderWorkspaceContext> workspaceContext;

    public VbaDevSnapshotWorkbookBuilder(
        string workspaceRoot,
        IVbaDevBuildProcess buildProcess)
        : this(
            workspaceRoot,
            buildProcess,
            TransportedDebugSourceSnapshotValidator.CreateForCurrentWindowsSession())
    {
    }

    public VbaDevSnapshotWorkbookBuilder(
        string workspaceRoot,
        IVbaDevBuildProcess buildProcess,
        TransportedDebugSourceSnapshotValidator snapshotValidator)
        : this(
            new VbaDebugWorkspaceRootBinding(workspaceRoot),
            buildProcess,
            snapshotValidator,
            beforeCreateSourceFile: null)
    {
    }

    internal VbaDevSnapshotWorkbookBuilder(
        string workspaceRoot,
        IVbaDevBuildProcess buildProcess,
        TransportedDebugSourceSnapshotValidator snapshotValidator,
        Action<string>? beforeCreateSourceFile)
        : this(
            new VbaDebugWorkspaceRootBinding(workspaceRoot),
            buildProcess,
            snapshotValidator,
            beforeCreateSourceFile)
    {
    }

    internal VbaDevSnapshotWorkbookBuilder(
        VbaDebugWorkspaceRootBinding workspaceRootBinding,
        IVbaDevBuildProcess buildProcess,
        TransportedDebugSourceSnapshotValidator snapshotValidator,
        Action<string>? beforeCreateSourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceRootBinding);
        this.buildProcess = buildProcess ?? throw new ArgumentNullException(nameof(buildProcess));
        this.snapshotValidator = snapshotValidator
            ?? throw new ArgumentNullException(nameof(snapshotValidator));
        workspaceContext = new Lazy<BuilderWorkspaceContext>(
            () =>
            {
                var creator = new WindowsVbaDebugWorkspaceCreator(
                    workspaceRootBinding.Resolve(),
                    afterCreateDirectoryBeforeOpen: null,
                    beforeCreateSourceFile: beforeCreateSourceFile);
                return new BuilderWorkspaceContext(
                    creator.WorkspaceRoot,
                    creator,
                    new WindowsVbaDebugWorkspaceTreeDeleter(
                        creator.WorkspaceRoot));
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<VbaDevSnapshotBuildResult> BuildAsync(
        string vbaDevPath,
        string sessionId,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(vbaDevPath, sessionId, request);
        var validatedSnapshot = snapshotValidator.Validate(request.SourceSnapshot);
        var workspace = workspaceContext.Value;
        var generationPath = Path.Combine(
            workspace.WorkspaceRoot,
            "workspaces",
            sessionId,
            "generations",
            $"generation-{request.Generation:D10}");
        var generationClaimed = false;
        IVbaDebugGenerationWorkspaceCreationScope? generationWorkspace = null;
        try
        {
            generationWorkspace = workspace.Creator.ClaimGeneration(
                sessionId,
                request.Generation);
            generationClaimed = true;
            var sourceSnapshotPath = generationWorkspace.SourcePath;
            var workbookPath = Path.Combine(
                generationWorkspace.OutputPath,
                request.WorkbookFileName);
            MaterializeSnapshot(
                validatedSnapshot,
                sourceSnapshotPath,
                generationWorkspace);
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
            if (!File.Exists(workbookPath))
            {
                throw new InvalidOperationException(
                    "vba-dev reported a successful snapshot build without producing the requested workbook.");
            }

            var result = new VbaDevSnapshotBuildResult(
                Path.GetFullPath(sourceSnapshotPath),
                Path.GetFullPath(workbookPath),
                Path.GetFullPath(generationWorkspace.GenerationPath))
            {
                WorkspaceOwnership = generationWorkspace,
                Output = processResult.StandardOutput
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
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
                    generationWorkspace.DeleteOwnedTree();
                }
                catch
                {
                    // The original build failure remains authoritative.
                }
                finally
                {
                    generationWorkspace.Dispose();
                }
            }
            else if (generationClaimed)
            {
                TryDeleteDirectory(workspace.TreeDeleter, generationPath);
            }
            throw;
        }
    }

    private static void ValidateRequest(
        string vbaDevPath,
        string sessionId,
        VbaDevSnapshotBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Path.IsPathFullyQualified(vbaDevPath) || !File.Exists(vbaDevPath))
        {
            throw new ArgumentException(
                "The supplied vba-dev path must identify an existing absolute file.",
                nameof(vbaDevPath));
        }
        if (!IsCanonicalSessionId(sessionId))
        {
            throw new ArgumentException(
                "The adapter session ID must contain 32 lowercase hexadecimal characters.",
                nameof(sessionId));
        }
        if (request.Generation < 0)
        {
            throw new ArgumentException(
                "The snapshot build generation must be nonnegative.",
                nameof(request));
        }
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
            string.IsNullOrWhiteSpace(request.WorkbookFileName) ||
            request.WorkbookFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
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
        if (request.SourceSnapshot.SchemaVersion != 1)
        {
            throw new ArgumentException(
                $"Unsupported source snapshot schema version {request.SourceSnapshot.SchemaVersion}.",
                nameof(request));
        }
    }

    private static void MaterializeSnapshot(
        ValidatedTransportedDebugSourceSnapshot snapshot,
        string sourceSnapshotPath,
        IVbaDebugGenerationWorkspaceCreationScope generationWorkspace)
    {
        foreach (var source in snapshot.Sources)
        {
            var filePath = Path.GetFullPath(Path.Combine(
                sourceSnapshotPath,
                source.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsStrictDescendant(filePath, sourceSnapshotPath))
            {
                throw new InvalidOperationException(
                    $"The transported source path escapes its session workspace: '{source.RelativePath}'.");
            }
            using var stream = generationWorkspace.CreateSourceFile(source.RelativePath);
            stream.Write(source.Bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private static bool IsStrictDescendant(string filePath, string directoryPath)
    {
        var relative = Path.GetRelativePath(directoryPath, filePath);
        return relative.Length > 0 &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative);
    }

    private static bool IsCanonicalSessionId(string value)
        => value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDeleteDirectory(
        WindowsVbaDebugWorkspaceTreeDeleter workspaceTreeDeleter,
        string directoryPath)
    {
        try
        {
            if (WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(directoryPath))
            {
                using var cleanupScope = workspaceTreeDeleter.OpenScope(directoryPath);
                cleanupScope.DeleteDirectory();
            }
        }
        catch
        {
            // The original failure remains authoritative; cleanup diagnostics are added later.
        }
    }

    private sealed record BuilderWorkspaceContext(
        string WorkspaceRoot,
        WindowsVbaDebugWorkspaceCreator Creator,
        WindowsVbaDebugWorkspaceTreeDeleter TreeDeleter);
}

public sealed record VbaDevSnapshotBuildRequest(
    string ProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    TransportedDebugSourceSnapshot SourceSnapshot)
{
    public int Generation { get; init; }
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

public sealed record VbaDevSnapshotBuildResult(
    string SourceSnapshotPath,
    string WorkbookPath,
    string SessionWorkspacePath) : IAsyncDisposable
{
    private int disposed;

    public IReadOnlyList<string> Output { get; init; } = [];

    internal IVbaDebugOwnedWorkspaceCreationScope? WorkspaceOwnership { get; init; }

    internal bool HasWorkspaceOwnership => WorkspaceOwnership is not null;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                WorkspaceOwnership?.DeleteOwnedTree();
            }
            finally
            {
                WorkspaceOwnership?.Dispose();
            }
        }
        return ValueTask.CompletedTask;
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
        string sessionId,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken);
}
