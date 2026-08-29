namespace VbaDev.App.CommonModules;

/// <summary>
/// Captures and validates one invocation-owned, immutable CommonModules package snapshot.
/// </summary>
public sealed class CommonModulesPackageSnapshotFactory
{
    private const int CleanupAttempts = 3;
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly CommonModulesPackageReader packageReader;
    private readonly string scratchRoot;
    private readonly Action? beforeLiveStabilityProof;

    /// <summary>
    /// Creates a factory that stores snapshots in the command's temporary workspace.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(CommonModulesPackageReader packageReader)
        : this(
            packageReader,
            Path.Combine(Path.GetTempPath(), "vba-dev-common-modules-snapshot"))
    {
    }

    /// <summary>
    /// Creates a factory that stores snapshots beneath the specified scratch root.
    /// </summary>
    public CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot)
        : this(packageReader, scratchRoot, beforeLiveStabilityProof: null)
    {
    }

    internal CommonModulesPackageSnapshotFactory(
        CommonModulesPackageReader packageReader,
        string scratchRoot,
        Action? beforeLiveStabilityProof)
    {
        this.packageReader = packageReader
            ?? throw new ArgumentNullException(nameof(packageReader));
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.beforeLiveStabilityProof = beforeLiveStabilityProof;
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
        var inventory = ReadInventory(repositoryPath);
        var stagingPath = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingPath);
            var capturedBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = ReadExactBytes(entry.FullName);
                File.WriteAllBytes(Path.Combine(stagingPath, entry.Name), content);
                capturedBytes.Add(entry.Name, content);
            }

            var package = FreezePackage(packageReader.Load(stagingPath));
            beforeLiveStabilityProof?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            ProveLiveInputsStable(repositoryPath, inventory, capturedBytes);
            return new CommonModulesPackageSnapshot(
                scratchRoot,
                stagingPath,
                package,
                capturedBytes);
        }
        catch (Exception captureError)
        {
            try
            {
                var cleanup = CleanupStagingDirectory(scratchRoot, stagingPath);
                if (!cleanup.Deleted)
                {
                    throw new InvalidOperationException(
                        $"The CommonModules package snapshot staging directory could not be removed: '{cleanup.RetainedPath}'.");
                }
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{captureError.Message} The CommonModules package snapshot staging directory could not be removed: '{stagingPath}'.",
                    new AggregateException(captureError, cleanupError));
            }

            throw;
        }
    }

    internal static CommonModulesPackageSnapshotCleanupResult CleanupStagingDirectory(
        string scratchRoot,
        string stagingPath)
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

        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            try
            {
                Directory.Delete(absoluteStagingPath, recursive: true);
                return new CommonModulesPackageSnapshotCleanupResult(
                    Deleted: true,
                    RetainedPath: null);
            }
            catch (DirectoryNotFoundException)
            {
                return new CommonModulesPackageSnapshotCleanupResult(
                    Deleted: true,
                    RetainedPath: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < CleanupAttempts)
                {
                    Thread.Sleep(CleanupRetryDelay);
                }
            }
        }

        return new CommonModulesPackageSnapshotCleanupResult(
            Deleted: false,
            RetainedPath: absoluteStagingPath);
    }

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
}

/// <summary>
/// Reports whether bounded cleanup removed an invocation-owned CommonModules snapshot.
/// </summary>
/// <param name="Deleted">Whether the snapshot workspace is conclusively absent.</param>
/// <param name="RetainedPath">The retained absolute path when deletion did not complete.</param>
public sealed record CommonModulesPackageSnapshotCleanupResult(
    bool Deleted,
    string? RetainedPath);

/// <summary>
/// Owns the staged package bytes and exposes planning and file reads that cannot consult the live repository.
/// </summary>
public sealed class CommonModulesPackageSnapshot : IDisposable
{
    private readonly string scratchRoot;
    private readonly CommonModulesPackage package;
    private readonly IReadOnlyDictionary<string, byte[]> capturedBytes;
    private CommonModulesPackageSnapshotCleanupResult? cleanupResult;

    internal CommonModulesPackageSnapshot(
        string scratchRoot,
        string stagingPath,
        CommonModulesPackage package,
        IReadOnlyDictionary<string, byte[]> capturedBytes)
    {
        this.scratchRoot = scratchRoot;
        StagingPath = stagingPath;
        this.package = package;
        this.capturedBytes = capturedBytes;
    }

    /// <summary>
    /// Gets the invocation-owned staging directory containing the validated package bytes.
    /// </summary>
    public string StagingPath { get; }

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
        cleanupResult ??= CommonModulesPackageSnapshotFactory.CleanupStagingDirectory(
            scratchRoot,
            StagingPath);
        return cleanupResult;
    }

    /// <inheritdoc />
    public void Dispose() => _ = Cleanup();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(cleanupResult is not null, this);
    }
}
