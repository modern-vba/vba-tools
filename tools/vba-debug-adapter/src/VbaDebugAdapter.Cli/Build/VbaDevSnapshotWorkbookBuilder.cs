using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

internal sealed class VbaDevSnapshotWorkbookBuilder : IVbaDebugWorkbookBuilder
{
    private readonly IVbaDevBuildProcess buildProcess;

    internal VbaDevSnapshotWorkbookBuilder(
        IVbaDevBuildProcess buildProcess)
    {
        this.buildProcess = buildProcess ?? throw new ArgumentNullException(nameof(buildProcess));
    }

    public async Task<VbaDevSnapshotBuildResult> BuildAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken)
    {
        IVbaDebugGenerationWorkspace? generationWorkspace = null;
        DebugFailureOutcome? processCleanupOutcome = null;
        var processInvocationStarted = false;
        try
        {
            ArgumentNullException.ThrowIfNull(workspaceLease);
            ValidateRequest(vbaDevPath, request);
            generationWorkspace = workspaceLease.CreateGenerationWorkspace(
                request.SourceSet.GenerationId,
                request.WorkbookFileName);
            var sourceSnapshotPath = generationWorkspace.SourceSnapshotPath;
            var workbookPath = generationWorkspace.WorkbookPath;
            request.SourceSet.MaterializeInto(generationWorkspace);
            generationWorkspace.SealSourceSnapshot();
            var sourceOrigins = request.SourceSet.CaptureOrigins(generationWorkspace);
            var arguments = new[]
            {
                "build",
                "--project", Path.GetFullPath(request.ProjectRoot),
                "--document", request.DocumentName,
                "--source-snapshot", sourceSnapshotPath,
                "--output", workbookPath
            };
            VbaDevBuildProcessResult processResult;
            processInvocationStarted = true;
            try
            {
                processResult = await buildProcess
                    .RunAsync(Path.GetFullPath(vbaDevPath), arguments, cancellationToken)
                    .ConfigureAwait(false);
                processCleanupOutcome = processResult.CleanupOutcome;
            }
            catch (Exception invocationFailure)
            {
                processCleanupOutcome = (invocationFailure as IDebugFailureEvidence)?.FailureOutcome;
                throw;
            }
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
                throw new SnapshotBuildFailedException(
                    DebugSnapshotBuildReport.Capture(request, sourceOrigins, processResult),
                    string.Join(Environment.NewLine, diagnostics));
            }
            processCleanupOutcome = CompleteProcessEvidence(processCleanupOutcome);
            processCleanupOutcome.ThrowWithEvidence();
            generationWorkspace.VerifySourceSnapshot();
            if (!WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(workbookPath))
            {
                throw new InvalidOperationException(
                    "vba-dev reported a successful snapshot build without producing the requested workbook.");
            }
            generationWorkspace.PinGeneratedWorkbook();

            var result = new VbaDevSnapshotBuildResult(
                generationWorkspace, CompleteProcessEvidence(processCleanupOutcome))
            {
                Report = DebugSnapshotBuildReport.Capture(request, sourceOrigins, processResult),
                Output = SplitOutput(processResult.StandardOutput)
                    .Concat(SplitOutput(processResult.StandardError))
                    .ToArray()
            };
            generationWorkspace = null;
            return result;
        }
        catch (Exception exception)
        {
            var completion = new DebugFailureCompletion(exception);
            if (processInvocationStarted)
            {
                completion.Merge(CompleteProcessEvidence(processCleanupOutcome));
            }
            else
            {
                foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
                {
                    completion.AddEvidence(new("build-process-admission", "vba-dev", kind, true,
                        "Build failed before invoking the companion process; no companion resources were acquired."));
                }
            }
            if (generationWorkspace is not null)
            {
                try
                {
                    await generationWorkspace.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    completion.AddFailure("build-generation-cleanup", generationWorkspace.GenerationWorkspacePath,
                        DebugResourceKind.FileSystem, cleanupException,
                        retainedPath: generationWorkspace.GenerationWorkspacePath);
                }
                if (generationWorkspace is IDebugResourceOwnerEvidence { CleanupOutcome: { } outcome })
                {
                    completion.Merge(outcome);
                }
            }
            completion.Complete().ThrowWithEvidence();
            throw;
        }
    }

    private static DebugFailureOutcome CompleteProcessEvidence(DebugFailureOutcome? ownerOutcome)
    {
        var completion = new DebugFailureCompletion();
        if (ownerOutcome is not null)
        {
            completion.Merge(ownerOutcome);
        }
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
        {
            if (ownerOutcome?.Evidence.Any(item => item.Kind == kind) != true)
            {
                completion.AddEvidence(new("build-process-cleanup", "vba-dev", kind, false,
                    "The invocation owner did not supply terminal release evidence."));
            }
        }
        return completion.Complete();
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
    }

}

internal sealed class VbaDevSnapshotBuildRequest
{
    internal VbaDevSnapshotBuildRequest(
        string projectRoot,
        string documentName,
        string workbookFileName,
        AdmittedDebugBuildSourceSet sourceSet)
    {
        ProjectRoot = projectRoot;
        DocumentName = documentName;
        WorkbookFileName = workbookFileName;
        SourceSet = sourceSet;
    }

    internal string ProjectRoot { get; }

    internal string DocumentName { get; }

    internal string WorkbookFileName { get; }

    internal AdmittedDebugBuildSourceSet SourceSet { get; }

    internal DebugGenerationId GenerationId => SourceSet.GenerationId;
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

public sealed class VbaDevSnapshotBuildResult : IAsyncDisposable, IDebugResourceOwnerEvidence
{
    private readonly object disposalGate = new();
    private readonly DebugFailureOutcome processCleanupOutcome;
    private IVbaDebugGenerationWorkspace? generationWorkspace;
    private Task? disposal;
    private DebugFailureOutcome? cleanupOutcome;

    public VbaDevSnapshotBuildResult(
        IVbaDebugGenerationWorkspace generationWorkspace)
        : this(generationWorkspace, new DebugFailureCompletion().Complete())
    {
    }

    internal VbaDevSnapshotBuildResult(
        IVbaDebugGenerationWorkspace generationWorkspace,
        DebugFailureOutcome processCleanupOutcome)
    {
        this.processCleanupOutcome = processCleanupOutcome;
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

    public DebugSnapshotBuildReport? Report { get; init; }

    DebugFailureOutcome? IDebugResourceOwnerEvidence.CleanupOutcome => cleanupOutcome;

    internal IVbaDebugGenerationWorkspace TransferGenerationOwnership()
    {
        lock (disposalGate)
        {
            return Interlocked.Exchange(ref generationWorkspace, null)
                ?? throw new InvalidOperationException(
                "The debug generation workspace ownership has already been transferred or disposed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null)
            {
                var ownedWorkspace = Interlocked.Exchange(ref generationWorkspace, null);
                disposal = DisposeWorkspaceAsync(ownedWorkspace);
            }
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeWorkspaceAsync(IVbaDebugGenerationWorkspace? workspace)
    {
        var completion = new DebugFailureCompletion();
        completion.Merge(processCleanupOutcome);
        if (workspace is not null)
        {
            try
            {
                await workspace.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                completion.AddFailure("build-result-cleanup", workspace.GenerationWorkspacePath,
                    DebugResourceKind.FileSystem, exception, retainedPath: workspace.GenerationWorkspacePath);
            }
            if (workspace is IDebugResourceOwnerEvidence { CleanupOutcome: { } ownerOutcome })
            {
                completion.Merge(ownerOutcome);
            }
            else
            {
                completion.AddEvidence(new("build-result-cleanup", workspace.GenerationWorkspacePath,
                    DebugResourceKind.Handle, false, "The generation owner did not supply release evidence.",
                    RetainedPath: workspace.GenerationWorkspacePath));
            }
        }
        cleanupOutcome = completion.Complete();
        cleanupOutcome.Throw();
    }
}

public sealed record VbaDevBuildProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    internal DebugFailureOutcome? CleanupOutcome { get; init; }
}

public interface IVbaDevBuildProcess
{
    Task<VbaDevBuildProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal interface IVbaDebugWorkbookBuilder
{
    Task<VbaDevSnapshotBuildResult> BuildAsync(
        string vbaDevPath,
        IVbaDebugSessionWorkspaceLease workspaceLease,
        VbaDevSnapshotBuildRequest request,
        CancellationToken cancellationToken);
}
