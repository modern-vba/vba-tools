namespace VbaLanguageServer.Workspace;

/// <summary>
/// Provides a one-shot file gate for deterministic project-validation process tests.
/// </summary>
internal sealed class BlockingVbaProjectValidationBuildObserver
    : IVbaProjectSnapshotBuildObserver
{
    internal const string StartedFileEnvironmentVariable =
        "VBA_TOOLS_PROJECT_VALIDATION_STARTED_FILE";
    internal const string ReleaseFileEnvironmentVariable =
        "VBA_TOOLS_PROJECT_VALIDATION_RELEASE_FILE";

    private readonly string startedFile;
    private readonly string releaseFile;
    private int claimed;

    private BlockingVbaProjectValidationBuildObserver(
        string startedFile,
        string releaseFile)
    {
        this.startedFile = startedFile;
        this.releaseFile = releaseFile;
    }

    internal static IVbaProjectSnapshotBuildObserver CreateFromEnvironment()
    {
        var startedFile = Environment.GetEnvironmentVariable(
            StartedFileEnvironmentVariable);
        var releaseFile = Environment.GetEnvironmentVariable(
            ReleaseFileEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(startedFile)
            && !string.IsNullOrWhiteSpace(releaseFile)
                ? new BlockingVbaProjectValidationBuildObserver(
                    startedFile,
                    releaseFile)
                : NullVbaProjectSnapshotBuildObserver.Instance;
    }

    public void BeforeBuildProjectValidation(
        string activeUri,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
        {
            return;
        }

        WriteSignal(startedFile, activeUri);
        WaitForRelease(releaseFile, cancellationToken);
    }

    public void BeforeStore(
        long workspaceVersion,
        CancellationToken cancellationToken)
    {
    }

    private static void WaitForRelease(
        string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                $"The project-validation gate path has no directory: {path}");
        Directory.CreateDirectory(directory);
        using var released = new ManualResetEventSlim(initialState: false);
        using var watcher = new FileSystemWatcher(
            directory,
            Path.GetFileName(path));
        FileSystemEventHandler signalRelease = (_, _) =>
        {
            if (File.Exists(path))
            {
                released.Set();
            }
        };
        RenamedEventHandler signalRename = (_, _) =>
        {
            if (File.Exists(path))
            {
                released.Set();
            }
        };
        watcher.Created += signalRelease;
        watcher.Changed += signalRelease;
        watcher.Renamed += signalRename;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
        {
            released.Set();
        }

        released.Wait(cancellationToken);
    }

    private static void WriteSignal(string path, string activeUri)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporaryPath, activeUri);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
