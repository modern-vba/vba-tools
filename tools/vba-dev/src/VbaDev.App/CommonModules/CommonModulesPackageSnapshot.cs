using VbaDev.App.FileSystem;

namespace VbaDev.App.CommonModules;

internal interface ICommonModulesPackageSnapshotCleanupObserver
{
    void OnProofComplete(string path);
}

/// <summary>
/// Captures and validates one invocation-owned, immutable CommonModules package snapshot.
/// </summary>
public sealed class CommonModulesPackageSnapshotFactory
{
    private const int CleanupAttempts = 3;
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly CommonModulesPackageReader packageReader;
    private readonly string scratchRoot;
    private readonly Action? beforePackageLoad;
    private readonly Action? beforeLiveStabilityProof;
    private readonly ICommonModulesPackageSnapshotCleanupObserver cleanupObserver;

    /// <summary>
    /// Creates a factory that stores snapshots in the command's temporary workspace.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CommonModulesPackageReader packageReader)
        : this(
            ownershipFactory,
            packageReader,
            Path.Combine(Path.GetTempPath(), "vba-dev-common-modules-snapshot"))
    {
    }

    /// <summary>
    /// Creates a factory that stores snapshots beneath the specified scratch root.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CommonModulesPackageReader packageReader,
        string scratchRoot)
        : this(
            ownershipFactory,
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof: null,
            NoOpCommonModulesPackageSnapshotCleanupObserver.Instance)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforeLiveStabilityProof)
        : this(
            ownershipFactory,
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof,
            NoOpCommonModulesPackageSnapshotCleanupObserver.Instance)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforeLiveStabilityProof,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
        : this(
            ownershipFactory,
            packageReader,
            scratchRoot,
            beforePackageLoad: null,
            beforeLiveStabilityProof,
            cleanupObserver)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforePackageLoad,
        Action? beforeLiveStabilityProof,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        this.ownershipFactory = ownershipFactory;
        this.packageReader = packageReader
            ?? throw new ArgumentNullException(nameof(packageReader));
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.beforePackageLoad = beforePackageLoad;
        this.beforeLiveStabilityProof = beforeLiveStabilityProof;
        this.cleanupObserver = cleanupObserver
            ?? throw new ArgumentNullException(nameof(cleanupObserver));
    }

    /// <summary>
    /// Captures a complete package, validates the staged bytes, and proves the live inputs remained stable.
    /// </summary>
    public CommonModulesPackageSnapshot Capture(
        string commonModulesRepositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonModulesRepositoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryPath = Path.GetFullPath(commonModulesRepositoryPath);
        var ownership = ownershipFactory.Open();
        CommonModulesPackageSnapshotStagingState? staging = null;
        var ownershipTransferred = false;
        try
        {
            var inventory = ReadInventory(repositoryPath);
            staging = CreateStagingDirectory(ownership, scratchRoot);
            var capturedBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = ReadExactBytes(entry.FullName);
                staging.Files.Add(ownership.CreateOnlyFile(
                    staging.DirectoryReceipt,
                    entry.Name,
                    content));
                capturedBytes.Add(entry.Name, content);
            }

            beforePackageLoad?.Invoke();
            var package = FreezePackage(packageReader.LoadCaptured(
                staging.Path,
                capturedBytes));
            beforeLiveStabilityProof?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            ProveLiveInputsStable(repositoryPath, inventory, capturedBytes);
            ownership.ReleaseCreationFence(staging.DirectoryReceipt);
            var snapshot = new CommonModulesPackageSnapshot(
                staging,
                package,
                capturedBytes,
                cleanupObserver);
            ownershipTransferred = true;
            return snapshot;
        }
        catch (Exception captureError)
        {
            if (staging is not null)
            {
                ownership.ReleaseCreationFence(staging.DirectoryReceipt);
                var cleanup = CleanupStagingDirectory(staging, cleanupObserver);
                if (!cleanup.Deleted)
                {
                    throw new CommonModulesPackageSnapshotRetainedException(
                        captureError,
                        cleanup);
                }
            }

            throw;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                ownership.Dispose();
            }
        }
    }

    internal static CommonModulesPackageSnapshotCleanupResult CleanupStagingDirectory(
        CommonModulesPackageSnapshotStagingState staging,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(cleanupObserver);
        ValidateStagingPath(staging.ScratchRoot, staging.Path);

        var removedFiles = new HashSet<string>(PathComparer);
        var retainedPaths = new HashSet<string>(PathComparer);
        var observationIncompletePaths = new HashSet<string>(PathComparer);
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            retainedPaths.Clear();
            observationIncompletePaths.Clear();
            var retryable = false;
            foreach (var file in staging.Files)
            {
                if (removedFiles.Contains(file.Route))
                {
                    continue;
                }

                var fileCleanup = staging.Ownership.TryDelete(
                    file,
                    cleanupObserver.OnProofComplete);
                if (fileCleanup.Removed)
                {
                    removedFiles.Add(file.Route);
                    continue;
                }

                retainedPaths.Add(file.Route);
                retainedPaths.UnionWith(fileCleanup.RetainedPaths);
                if (!fileCleanup.Conclusive)
                {
                    observationIncompletePaths.Add(file.Route);
                    retryable = true;
                }
            }

            if (removedFiles.Count == staging.Files.Count)
            {
                var directoryCleanup = staging.Ownership.TryDeleteEmpty(
                    staging.DirectoryReceipt,
                    cleanupObserver.OnProofComplete);
                if (directoryCleanup.Removed)
                {
                    return new CommonModulesPackageSnapshotCleanupResult(
                        Deleted: true,
                        RetainedPath: null);
                }

                retainedPaths.UnionWith(directoryCleanup.RetainedPaths);
                if (!directoryCleanup.Conclusive)
                {
                    observationIncompletePaths.Add(staging.Path);
                    retryable = true;
                }
            }
            else
            {
                retainedPaths.Add(staging.Path);
            }

            if (!retryable || attempt == CleanupAttempts)
            {
                break;
            }

            Thread.Sleep(CleanupRetryDelay);
        }

        return new CommonModulesPackageSnapshotCleanupResult(
            Deleted: false,
            RetainedPath: staging.Path)
        {
            RetainedEntryPaths = SortPaths(retainedPaths.Append(staging.Path)),
            ObservationIncompletePaths = SortPaths(observationIncompletePaths)
        };
    }

    private static CommonModulesPackageSnapshotStagingState CreateStagingDirectory(
        ExactFileSystemObjectOwnership ownership,
        string scratchRoot)
    {
        Directory.CreateDirectory(scratchRoot);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = Guid.NewGuid().ToString("N");
            var receipt = ownership.TryCreateOnlyDirectory(scratchRoot, name);
            if (receipt is null)
            {
                continue;
            }

            return new CommonModulesPackageSnapshotStagingState(
                ownership,
                scratchRoot,
                receipt);
        }

        throw new IOException(
            $"A unique CommonModules snapshot staging directory could not be created beneath '{scratchRoot}'.");
    }

    private static void ValidateStagingPath(string scratchRoot, string stagingPath)
    {
        var absoluteScratchRoot = Path.GetFullPath(scratchRoot);
        var absoluteStagingPath = Path.GetFullPath(stagingPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(absoluteStagingPath),
                absoluteScratchRoot,
                comparison)
            || !Guid.TryParseExact(Path.GetFileName(absoluteStagingPath), "N", out _))
        {
            throw new InvalidOperationException(
                $"CommonModules package snapshot must be a direct GUID child of its scratch root: {absoluteStagingPath}");
        }
    }

    private static IReadOnlyList<string> SortPaths(IEnumerable<string> paths)
        => paths
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<FileInfo> ReadInventory(string repositoryPath)
    {
        try
        {
            var repository = new DirectoryInfo(repositoryPath);
            if (!repository.Exists)
            {
                throw new CommonModulesManifestException(
                    $"CommonModulesRepository was not found: {repositoryPath}");
            }

            if (repository.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules package root must be an ordinary directory: {repositoryPath}");
            }

            var inventory = new List<FileInfo>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in repository.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false
                }))
            {
                if (!names.Add(entry.Name))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules package contains case-insensitive duplicate entry '{entry.Name}'.");
                }

                if (entry is not FileInfo file
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new CommonModulesManifestException(
                        $"CommonModules package entry must be an ordinary file: {entry.FullName}");
                }

                inventory.Add(file);
            }

            inventory.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
            return inventory;
        }
        catch (CommonModulesManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package inventory could not be read: {repositoryPath}");
        }
    }

    private static byte[] ReadExactBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules package entry could not be read: {path}");
        }
    }

    private static void ProveLiveInputsStable(
        string repositoryPath,
        IReadOnlyList<FileInfo> capturedInventory,
        IReadOnlyDictionary<string, byte[]> capturedBytes)
    {
        var currentInventory = ReadInventory(repositoryPath);
        if (capturedInventory.Count != currentInventory.Count
            || !capturedInventory.Select(entry => entry.Name).SequenceEqual(
                currentInventory.Select(entry => entry.Name),
                StringComparer.Ordinal))
        {
            throw PackageChanged();
        }

        foreach (var currentEntry in currentInventory)
        {
            if (!ReadExactBytes(currentEntry.FullName).AsSpan().SequenceEqual(
                    capturedBytes[currentEntry.Name]))
            {
                throw PackageChanged();
            }
        }
    }

    private static CommonModulesManifestException PackageChanged()
        => new(
            "CommonModules package changed while its immutable snapshot was being captured. "
            + "No source or manifest changes were made. Rerun the command.");

    private static CommonModulesPackage FreezePackage(CommonModulesPackage package)
        => new(Array.AsReadOnly(package.Entries
            .Select(entry => new CommonModuleManifestEntry(
                entry.ModuleFile,
                Array.AsReadOnly(entry.Categories.ToArray()),
                Array.AsReadOnly(entry.Dependencies.ToArray()),
                Array.AsReadOnly(entry.RequiredReferences.ToArray())))
            .ToArray()));

    private sealed class NoOpCommonModulesPackageSnapshotCleanupObserver
        : ICommonModulesPackageSnapshotCleanupObserver
    {
        public static NoOpCommonModulesPackageSnapshotCleanupObserver Instance { get; } = new();

        public void OnProofComplete(string path)
        {
        }
    }
}

internal sealed class CommonModulesPackageSnapshotStagingState
{
    public CommonModulesPackageSnapshotStagingState(
        ExactFileSystemObjectOwnership ownership,
        string scratchRoot,
        ExactFileSystemObjectOwnership.DirectoryReceipt directoryReceipt)
    {
        Ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
        ScratchRoot = System.IO.Path.GetFullPath(scratchRoot);
        DirectoryReceipt = directoryReceipt
            ?? throw new ArgumentNullException(nameof(directoryReceipt));
    }

    public ExactFileSystemObjectOwnership Ownership { get; }

    public string ScratchRoot { get; }

    public string Path => DirectoryReceipt.Route;

    public ExactFileSystemObjectOwnership.DirectoryReceipt DirectoryReceipt { get; }

    public List<ExactFileSystemObjectOwnership.FileReceipt> Files { get; } = [];
}

/// <summary>
/// Reports whether bounded cleanup removed an invocation-owned CommonModules snapshot.
/// </summary>
/// <param name="Deleted">Whether the snapshot workspace is conclusively absent.</param>
/// <param name="RetainedPath">The retained absolute path when deletion did not complete.</param>
public sealed record CommonModulesPackageSnapshotCleanupResult(
    bool Deleted,
    string? RetainedPath)
{
    /// <summary>
    /// Gets the exact retained staging entries observed during cleanup.
    /// </summary>
    public IReadOnlyList<string> RetainedEntryPaths { get; init; } = [];

    /// <summary>
    /// Gets retained paths whose identity or state could not be proved conclusively.
    /// </summary>
    public IReadOnlyList<string> ObservationIncompletePaths { get; init; } = [];

    /// <summary>
    /// Gets whether every retained entry was observed conclusively.
    /// </summary>
    public bool IsConclusive => ObservationIncompletePaths.Count == 0;
}

/// <summary>
/// Reports capture failure together with the structured cleanup evidence for a retained snapshot.
/// </summary>
public sealed class CommonModulesPackageSnapshotRetainedException
    : InvalidOperationException
{
    /// <summary>
    /// Creates a retained-snapshot failure.
    /// </summary>
    public CommonModulesPackageSnapshotRetainedException(
        Exception captureFailure,
        CommonModulesPackageSnapshotCleanupResult cleanupResult)
        : base(
            $"{captureFailure.Message} The CommonModules package snapshot staging directory "
            + $"could not be removed: '{cleanupResult.RetainedPath}'.",
            captureFailure)
    {
        CleanupResult = cleanupResult;
    }

    /// <summary>
    /// Gets the exact cleanup result that caused the workspace to be retained.
    /// </summary>
    public CommonModulesPackageSnapshotCleanupResult CleanupResult { get; }
}

/// <summary>
/// Owns the staged package bytes and exposes planning and file reads that cannot consult the live repository.
/// </summary>
public sealed class CommonModulesPackageSnapshot : IDisposable
{
    private readonly CommonModulesPackageSnapshotStagingState staging;
    private readonly CommonModulesPackage package;
    private readonly IReadOnlyDictionary<string, byte[]> capturedBytes;
    private readonly ICommonModulesPackageSnapshotCleanupObserver cleanupObserver;
    private CommonModulesPackageSnapshotCleanupResult? cleanupResult;

    internal CommonModulesPackageSnapshot(
        CommonModulesPackageSnapshotStagingState staging,
        CommonModulesPackage package,
        IReadOnlyDictionary<string, byte[]> capturedBytes,
        ICommonModulesPackageSnapshotCleanupObserver cleanupObserver)
    {
        this.staging = staging;
        this.package = package;
        this.capturedBytes = capturedBytes;
        this.cleanupObserver = cleanupObserver;
    }

    /// <summary>
    /// Gets the invocation-owned staging directory containing the validated package bytes.
    /// </summary>
    public string StagingPath => staging.Path;

    /// <summary>
    /// Gets the canonical manifest entries parsed exclusively from the staged manifest bytes.
    /// </summary>
    public IReadOnlyList<CommonModuleManifestEntry> Entries
    {
        get
        {
            ThrowIfDisposed();
            return package.Entries;
        }
    }

    /// <summary>
    /// Resolves a dependency and reference plan using only the manifest parsed from staged bytes.
    /// </summary>
    public CommonModulesSelectionPlan ResolveRequestedPlan(
        IReadOnlyList<string> requestedModules)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requestedModules);
        return CommonModulesDependencyResolver.ResolveRequestedPlan(
            package.Entries,
            requestedModules);
    }

    /// <summary>
    /// Returns a copy of one exact package file captured in staging.
    /// </summary>
    public byte[] ReadFileBytes(string fileName)
    {
        if (!TryReadFileBytes(fileName, out var content))
        {
            throw new CommonModulesManifestException(
                $"CommonModules snapshot file was not found: {fileName}");
        }

        return content;
    }

    /// <summary>
    /// Tries to return a copy of one exact package file captured in staging.
    /// </summary>
    public bool TryReadFileBytes(string fileName, out byte[] content)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (capturedBytes.TryGetValue(fileName, out var capturedContent))
        {
            content = capturedContent.ToArray();
            return true;
        }

        content = [];
        return false;
    }

    /// <summary>
    /// Applies bounded deletion retries and reports a retained path without changing transaction outcome policy.
    /// </summary>
    public CommonModulesPackageSnapshotCleanupResult Cleanup()
    {
        if (cleanupResult is null)
        {
            try
            {
                cleanupResult = CommonModulesPackageSnapshotFactory.CleanupStagingDirectory(
                    staging,
                    cleanupObserver);
            }
            finally
            {
                staging.Ownership.Dispose();
            }
        }

        return cleanupResult;
    }

    /// <inheritdoc />
    public void Dispose() => _ = Cleanup();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(cleanupResult is not null, this);
    }
}
