using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Adapts the normal manifest-aware workbook build command to debug launch orchestration.
/// </summary>
public sealed class BuildCommandDebugWorkbookBuilder : IDebugWorkbookBuilder
{
    private readonly Func<ResolvedProjectContext, CancellationToken, Task<CommandResult>> runBuild;
    private readonly Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>>? resolveBuildSources;
    private readonly Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>, CancellationToken, Task<CommandResult>>?
        runBuildWithSources;

    /// <summary>
    /// Creates a debug workbook builder over the normal build command.
    /// </summary>
    public BuildCommandDebugWorkbookBuilder(BuildCommand buildCommand)
        : this(buildCommand.RunAsync)
    {
    }

    /// <summary>
    /// Creates a snapshot-aware debug workbook builder over the normal source planner and build command.
    /// </summary>
    public BuildCommandDebugWorkbookBuilder(
        WorkbookSourcePlanner sourcePlanner,
        BuildCommand buildCommand)
        : this(sourcePlanner.ResolveBuildSourceFiles, buildCommand.RunAsync)
    {
    }

    internal BuildCommandDebugWorkbookBuilder(
        Func<ResolvedProjectContext, CommandResult> runBuild)
        : this((context, _) => Task.Run(() => runBuild(context), CancellationToken.None))
    {
    }

    internal BuildCommandDebugWorkbookBuilder(
        Func<ResolvedProjectContext, CancellationToken, Task<CommandResult>> runBuild)
    {
        this.runBuild = runBuild;
    }

    internal BuildCommandDebugWorkbookBuilder(
        Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>> resolveBuildSources,
        Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>, CommandResult> runBuild)
        : this(
            resolveBuildSources,
            (context, sources, _) => Task.Run(
                () => runBuild(context, sources),
                CancellationToken.None))
    {
    }

    internal BuildCommandDebugWorkbookBuilder(
        Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>> resolveBuildSources,
        Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>, CancellationToken, Task<CommandResult>> runBuild)
    {
        this.resolveBuildSources = resolveBuildSources;
        runBuildWithSources = runBuild;
        this.runBuild = (context, cancellationToken) =>
            runBuild(context, resolveBuildSources(context), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DebugWorkbookBuildResult> BuildAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await runBuild(context, cancellationToken).ConfigureAwait(false);
        return ToDebugBuildResult(result);
    }

    /// <summary>
    /// Builds from an immutable source snapshot after staging the exact validated source bytes.
    /// </summary>
    public async Task<DebugWorkbookBuildResult> BuildAsync(
        ResolvedProjectContext context,
        DebugSourceSnapshot sourceSnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resolveBuildSources is null || runBuildWithSources is null)
        {
            throw new InvalidOperationException(
                "The debug workbook builder was not configured for snapshot-aware builds.");
        }

        var plannedSources = resolveBuildSources(context);
        using var stagedSources = DebugBuildSourceStaging.Create(
            sourceSnapshot,
            plannedSources,
            cancellationToken);
        var result = await runBuildWithSources(
            context,
            stagedSources.Sources,
            cancellationToken).ConfigureAwait(false);
        return ToDebugBuildResult(result);
    }

    private static DebugWorkbookBuildResult ToDebugBuildResult(CommandResult result)
    {
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new DebugSetupException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Workbook build failed before the debug Excel process could start."
                    : detail.Trim());
        }

        var output = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new DebugWorkbookBuildResult(output);
    }

    private sealed class DebugBuildSourceStaging : IDisposable
    {
        private readonly string stagingPath;

        private DebugBuildSourceStaging(
            string stagingPath,
            IReadOnlyList<VbaSourceFile> sources)
        {
            this.stagingPath = stagingPath;
            Sources = sources;
        }

        public IReadOnlyList<VbaSourceFile> Sources { get; }

        public static DebugBuildSourceStaging Create(
            DebugSourceSnapshot sourceSnapshot,
            IReadOnlyList<VbaSourceFile> plannedSources,
            CancellationToken cancellationToken)
        {
            var snapshotByPath = CreateSnapshotIndex(sourceSnapshot);
            var plannedByPath = CreatePlannedSourceIndex(plannedSources);
            ValidateInventory(snapshotByPath, plannedByPath);

            var stagingPath = Path.Combine(
                Path.GetTempPath(),
                "vba-dev-debug-build",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingPath);
            try
            {
                var stagedSources = new List<VbaSourceFile>(plannedSources.Count);
                foreach (var source in plannedSources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = Path.GetFullPath(source.SourcePath);
                    var sourceBytes = ReadSourceBytes(sourcePath);
                    var capturedText = snapshotByPath[sourcePath].Text;
                    if (!VbaSourceFileTextReader.Decode(sourceBytes).Equals(
                        capturedText,
                        StringComparison.Ordinal))
                    {
                        throw new DebugSetupException(
                            $"Debug source snapshot content does not match build source '{sourcePath}'.");
                    }

                    var stagedSourcePath = Path.Combine(stagingPath, Path.GetFileName(sourcePath));
                    File.WriteAllBytes(stagedSourcePath, sourceBytes);
                    var stagedBinaryPath = StageBinarySidecar(source, stagingPath);
                    stagedSources.Add(new VbaSourceFile(
                        stagedSourcePath,
                        source.Kind,
                        stagedBinaryPath));
                }

                return new DebugBuildSourceStaging(stagingPath, stagedSources);
            }
            catch
            {
                DeleteStagingDirectory(stagingPath);
                throw;
            }
        }

        public void Dispose() => DeleteStagingDirectory(stagingPath);

        private static Dictionary<string, DebugSourceFileSnapshot> CreateSnapshotIndex(
            DebugSourceSnapshot sourceSnapshot)
        {
            var result = new Dictionary<string, DebugSourceFileSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var source in sourceSnapshot.Sources)
            {
                if (!Path.IsPathFullyQualified(source.Path))
                {
                    throw new DebugSetupException(
                        $"Debug source snapshot path must be absolute: '{source.Path}'.");
                }

                var path = Path.GetFullPath(source.Path);
                if (!result.TryAdd(path, source))
                {
                    throw new DebugSetupException(
                        $"Debug source snapshot contains duplicate build source '{path}'.");
                }
            }

            return result;
        }

        private static Dictionary<string, VbaSourceFile> CreatePlannedSourceIndex(
            IReadOnlyList<VbaSourceFile> plannedSources)
        {
            var result = new Dictionary<string, VbaSourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in plannedSources)
            {
                var path = Path.GetFullPath(source.SourcePath);
                if (!result.TryAdd(path, source))
                {
                    throw new DebugSetupException(
                        $"The normal build source plan contains duplicate source '{path}'.");
                }
            }

            return result;
        }

        private static void ValidateInventory(
            IReadOnlyDictionary<string, DebugSourceFileSnapshot> snapshotByPath,
            IReadOnlyDictionary<string, VbaSourceFile> plannedByPath)
        {
            var missing = plannedByPath.Keys
                .Where(path => !snapshotByPath.ContainsKey(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var unexpected = snapshotByPath.Keys
                .Where(path => !plannedByPath.ContainsKey(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length == 0 && unexpected.Length == 0)
            {
                return;
            }

            var details = new List<string>();
            if (missing.Length != 0)
            {
                details.Add($"missing from snapshot: {string.Join(", ", missing)}");
            }

            if (unexpected.Length != 0)
            {
                details.Add($"unexpected in snapshot: {string.Join(", ", unexpected)}");
            }

            throw new DebugSetupException(
                "Debug source snapshot inventory does not match the normal build source plan; " +
                string.Join("; ", details) + ".");
        }

        private static byte[] ReadSourceBytes(string sourcePath)
        {
            try
            {
                return File.ReadAllBytes(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DebugSetupException(
                    $"Debug build source could not be read: '{sourcePath}'.",
                    ex);
            }
        }

        private static string? StageBinarySidecar(VbaSourceFile source, string stagingPath)
        {
            if (source.BinaryPath is null)
            {
                return null;
            }

            var binaryPath = Path.GetFullPath(source.BinaryPath);
            var binaryBytes = ReadSourceBytes(binaryPath);
            var stagedBinaryPath = Path.Combine(stagingPath, Path.GetFileName(binaryPath));
            File.WriteAllBytes(stagedBinaryPath, binaryBytes);
            return stagedBinaryPath;
        }

        private static void DeleteStagingDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
