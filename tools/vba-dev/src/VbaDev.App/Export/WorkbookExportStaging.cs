using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

/// <summary>
/// Records only files declared and completed by the export producer. The command
/// owns process-release gating, destination commitment, and cleanup-result policy.
/// </summary>
public sealed class WorkbookExportStaging : IDisposable
{
    private readonly ExactFileSystemObjectOwnership ownership;
    private readonly InvocationScratch scratch;
    private readonly Dictionary<string, ExactFileSystemObjectOwnership.DirectoryReceipt> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExactFileSystemObjectOwnership.PendingFileCapture> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExactFileSystemObjectOwnership.FileReceipt> files = [];
    private bool productionCompleted;
    private bool disposed;
    private InvocationScratchCleanupEvidence? cleanup;

    private WorkbookExportStaging(ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.DirectoryReceipt directory)
    {
        this.ownership = ownership;
        scratch = new InvocationScratch(ownership);
        scratch.Register(directory);
        directories.Add(string.Empty, directory);
        Path = directory.Route;
        ownership.ReleaseCreationFence(directory);
    }

    /// <summary>Gets the invocation-private staging path for diagnostics.</summary>
    public string Path { get; }

    internal static WorkbookExportStaging Create(IExactFileSystemObjectOwnershipFactory factory)
    {
        var ownership = factory.Open();
        try
        {
            var root = ownership.TryCreateOnlyDirectory(System.IO.Path.GetTempPath(), $"vba-dev-export-{Guid.NewGuid():N}")
                ?? throw new IOException("A unique export staging directory could not be created.");
            return new WorkbookExportStaging(ownership, root);
        }
        catch { ownership.Dispose(); throw; }
    }

    /// <summary>
    /// Writes one source unit into an absent route and records its completed source
    /// and optional form sidecar before the producer's Excel session is retired.
    /// </summary>
    public async Task WriteModuleAsync(string relativePath, Func<string, Task> write)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (productionCompleted) throw new InvalidOperationException("Export production is already complete.");
        var route = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        var relative = System.IO.Path.GetRelativePath(Path, route);
        if (System.IO.Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Export source must remain in its staging workspace: {relativePath}");
        EnsureDirectory(System.IO.Path.GetDirectoryName(relative) ?? string.Empty);
        RequireAbsent(route);
        var sidecar = System.IO.Path.GetExtension(route).Equals(".frm", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.ChangeExtension(route, ".frx") : null;
        if (sidecar is not null && !pending.ContainsKey(sidecar)) RequireAbsent(sidecar);
        await write(route).ConfigureAwait(false);
        CaptureProducedFile(route);
        if (sidecar is not null && !pending.ContainsKey(sidecar) && File.Exists(sidecar))
            CaptureProducedFile(sidecar);
    }

    private void CaptureProducedFile(string route)
    {
        try { pending.Add(route, ownership.CapturePendingSavedFile(route)); }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new IOException($"The produced export file could not be captured: {route}", error);
        }
    }

    internal void CompleteProduction()
    {
        if (productionCompleted) return;
        productionCompleted = true;
        var failures = new List<string>();
        foreach (var (route, observation) in pending)
        {
            var completed = ownership.CompleteStableCapture(observation);
            if (completed.Capture is { } capture)
            {
                scratch.Register(capture.Receipt);
                files.Add(capture.Receipt);
            }
            else failures.Add($"{route} ({completed.Observation})");
        }
        if (failures.Count > 0)
            throw new IOException($"Export staging ownership could not be proved: {string.Join(", ", failures)}");
    }

    internal void ProveUnchanged()
    {
        var knownFiles = files.Select(file => file.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownDirectories = directories.Values.Select(directory => directory.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.Values)
        {
            if (ownership.Observe(directory) != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
                throw new IOException($"Export staging directory changed or could not be proved: {directory.Route}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Route))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || !knownDirectories.Contains(entry))
                        throw new IOException($"Export staging contains an unregistered directory: {entry}");
                }
                else if (DocumentSourceSetLayout.IsVbaSourceOrSidecar(entry) && !knownFiles.Contains(entry))
                    throw new IOException($"Export staging contains an unregistered source or sidecar: {entry}");
            }
        }
        foreach (var file in files)
            if (ownership.Observe(file) != ExactFileSystemObjectOwnership.ObservationResult.Unchanged)
                throw new IOException($"Export staging changed or could not be proved before destination commitment: {file.Route}");
    }

    internal InvocationScratchCleanupEvidence Cleanup()
    {
        if (cleanup is not null) return cleanup;
        try { return cleanup = scratch.Cleanup(); }
        finally { Dispose(); }
    }

    /// <summary>Closes ownership resources without deleting dependent scratch.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }

    private void EnsureDirectory(string relative)
    {
        if (directories.ContainsKey(relative)) return;
        var parent = System.IO.Path.GetDirectoryName(relative) ?? string.Empty;
        EnsureDirectory(parent);
        var created = ownership.TryCreateOnlyDirectory(directories[parent].Route, System.IO.Path.GetFileName(relative))
            ?? throw new IOException($"Export staging directory already exists: {relative}");
        scratch.Register(created);
        directories.Add(relative, created);
        ownership.ReleaseCreationFence(created);
    }

    private static void RequireAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Export staging route is already occupied: {path}");
    }
}
