using System.ComponentModel;
using VbaDev.App.FileSystem;
using DirectoryReceipt = VbaDev.App.FileSystem.ExactFileSystemObjectOwnership.DirectoryReceipt;
using FileReceipt = VbaDev.App.FileSystem.ExactFileSystemObjectOwnership.FileReceipt;
using ArtifactObservation = VbaDev.App.FileSystem.ExactFileSystemObjectOwnership.ObservationResult;

namespace VbaDev.App.Projects;

internal interface INewProjectArtifactRollbackObserver
{
    void OnProofComplete(string path);
}

internal interface INewProjectDirectoryCreationObserver
{
    void OnDirectoryCreated(string path);
}

internal sealed class NewProjectArtifactTracker : IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly List<FileReceipt> createdFiles = [];
    private readonly List<DirectoryReceipt> createdDirectories = [];
    private readonly HashSet<string> removedOwnedPaths = new(PathComparer);
    private readonly Dictionary<string, RetainedCreationFailure> retainedCreationFailures = new(PathComparer);
    private readonly Action<string>? beforeDirectoryCreate;
    private readonly INewProjectDirectoryCreationObserver? directoryCreationObserver;
    private readonly INewProjectArtifactRollbackObserver? rollbackObserver;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private ExactFileSystemObjectOwnership? ownership;
    private string? allowedLeaseMarkerPath;
    private bool disposed;

    public NewProjectArtifactTracker(IExactFileSystemObjectOwnershipFactory ownershipFactory)
    {
        this.ownershipFactory = ownershipFactory;
    }

    internal NewProjectArtifactTracker(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        Action<string> beforeDirectoryCreate)
        : this(ownershipFactory)
    {
        this.beforeDirectoryCreate = beforeDirectoryCreate;
    }

    internal NewProjectArtifactTracker(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        INewProjectDirectoryCreationObserver directoryCreationObserver)
        : this(ownershipFactory)
    {
        this.directoryCreationObserver = directoryCreationObserver;
    }

    internal NewProjectArtifactTracker(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        INewProjectArtifactRollbackObserver rollbackObserver)
        : this(ownershipFactory)
    {
        this.rollbackObserver = rollbackObserver;
    }

    internal ExactFileSystemObjectOwnership Ownership
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ownership ??= ownershipFactory.Open();
        }
    }

    public void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var missingDirectories = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new IOException($"New project directory path is occupied by a file: {current}");
            }

            missingDirectories.Push(current);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)
                || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"New project directory could not be created safely: {fullPath}");
            }

            current = parent;
        }

        foreach (var missingDirectory in missingDirectories)
        {
            beforeDirectoryCreate?.Invoke(missingDirectory);
            var receipt = Ownership.TryCreateOnlyDirectory(
                Path.GetDirectoryName(missingDirectory)!,
                Path.GetFileName(missingDirectory));
            if (receipt is null)
            {
                throw new IOException(
                    $"New project directory appeared before exclusive creation: {missingDirectory}");
            }

            createdDirectories.Add(receipt);
            try
            {
                directoryCreationObserver?.OnDirectoryCreated(receipt.Route);
            }
            finally
            {
                Ownership.ReleaseCreationFence(receipt);
            }
        }
    }

    public void RecordCreatedFile(FileReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RejectDuplicateTrackedPath(receipt.Route);
        var observation = Ownership.Observe(receipt);
        createdFiles.Add(receipt);
        if (observation != ArtifactObservation.Unchanged)
        {
            throw new NewProjectArtifactEvidenceMismatchException(
                receipt.Route,
                observation == ArtifactObservation.Inconclusive);
        }
    }

    public void CreateFile(
        string path,
        ReadOnlyMemory<byte> contents,
        Action<string, long>? onBytesWritten = null)
    {
        var fullPath = Path.GetFullPath(path);
        RejectDuplicateTrackedPath(fullPath);
        FileReceipt receipt;
        try
        {
            receipt = Ownership.CreateOnlyFile(
                Path.GetDirectoryName(fullPath)!,
                Path.GetFileName(fullPath),
                contents.Span,
                onBytesWritten: onBytesWritten);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 80 or 183)
        {
            throw new NewProjectArtifactAlreadyExistsException(fullPath, exception);
        }
        catch (ExactFileSystemObjectOwnership.FileCreationCleanupException exception)
        {
            retainedCreationFailures.Add(
                exception.Route,
                new RetainedCreationFailure(exception.TargetChanged, exception.RollbackUnproven));
            throw;
        }

        createdFiles.Add(receipt);
    }

    public NewProjectTargetInventoryResult ProveCompleteTargetInventory(
        string targetRoot,
        string allowedLeaseMarkerPath)
    {
        var fullTargetRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetRoot));
        var fullLeaseMarkerPath = AllowLeaseMarker(
            fullTargetRoot,
            allowedLeaseMarkerPath);

        var expectedPaths = new HashSet<string>(PathComparer)
        {
            fullLeaseMarkerPath
        };
        foreach (var file in createdFiles)
        {
            if (IsDescendant(file.Route, fullTargetRoot))
            {
                expectedPaths.Add(file.Route);
            }
        }

        foreach (var directory in createdDirectories)
        {
            if (IsDescendant(directory.Route, fullTargetRoot))
            {
                expectedPaths.Add(directory.Route);
            }
        }

        var targetEnumeration = EnumerateTargetEntries(fullTargetRoot);
        var actualPaths = targetEnumeration.Paths;
        var fileObservations = createdFiles
            .Where(file => IsSameOrDescendant(file.Route, fullTargetRoot))
            .Select(file => new PathObservation(file.Route, Ownership.Observe(file)))
            .ToArray();
        var directoryObservations = createdDirectories
            .Where(directory => IsSameOrDescendant(directory.Route, fullTargetRoot))
            .Select(directory => new PathObservation(
                directory.Route,
                Ownership.Observe(directory)))
            .ToArray();
        var markerObservation = ObserveAllowedLeaseMarker(fullLeaseMarkerPath);
        var observationIncompletePaths = targetEnumeration.ObservationIncompletePaths
            .Concat(fileObservations
                .Where(observation => observation.Observation == ArtifactObservation.Inconclusive)
                .Select(observation => observation.Path))
            .Concat(directoryObservations
                .Where(observation => observation.Observation == ArtifactObservation.Inconclusive)
                .Select(observation => observation.Path))
            .Concat(markerObservation == ArtifactObservation.Inconclusive
                ? [fullLeaseMarkerPath]
                : []);
        var targetChangedPaths = expectedPaths
            .Except(actualPaths, PathComparer)
            .Concat(actualPaths.Except(expectedPaths, PathComparer))
            .Concat(fileObservations
                .Where(observation => observation.Observation != ArtifactObservation.Unchanged)
                .Select(observation => observation.Path))
            .Concat(directoryObservations
                .Where(observation => observation.Observation != ArtifactObservation.Unchanged)
                .Select(observation => observation.Path))
            .Concat(markerObservation == ArtifactObservation.Unchanged
                ? []
                : [fullLeaseMarkerPath])
            .Concat(observationIncompletePaths);
        return new NewProjectTargetInventoryResult(
            SortPaths(targetChangedPaths),
            SortPaths(observationIncompletePaths));
    }

    public string AllowLeaseMarker(
        string targetRoot,
        string leaseMarkerPath)
    {
        var fullTargetRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(targetRoot));
        var fullLeaseMarkerPath = Path.GetFullPath(leaseMarkerPath);
        if (!Path.GetDirectoryName(fullLeaseMarkerPath)!.Equals(
                fullTargetRoot,
                PathComparison))
        {
            throw new ArgumentException(
                "The allowed lease marker must be an immediate child of the target root.",
                nameof(leaseMarkerPath));
        }

        this.allowedLeaseMarkerPath = fullLeaseMarkerPath;
        return fullLeaseMarkerPath;
    }

    public NewProjectRollbackResult Rollback()
        => Rollback(RollbackScope.AllTrackedArtifacts, targetRoot: null);

    public NewProjectRollbackResult RollbackUnderLease(string targetRoot)
        => Rollback(
            RollbackScope.UnderLease,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot)));

    public NewProjectRollbackResult RollbackAfterLeaseRelease(string targetRoot)
        => Rollback(
            RollbackScope.AfterLeaseRelease,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot)));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ownership?.Dispose();
    }

    private NewProjectRollbackResult Rollback(RollbackScope scope, string? targetRoot)
    {
        var targetChangedPaths = new HashSet<string>(PathComparer);
        var cleanupIncompletePaths = new HashSet<string>(PathComparer);
        foreach (var (path, failure) in retainedCreationFailures)
        {
            if (failure.TargetChanged)
            {
                targetChangedPaths.Add(path);
            }

            if (!failure.TargetChanged || failure.RollbackUnproven)
            {
                cleanupIncompletePaths.Add(path);
            }
        }

        foreach (var file in createdFiles.AsEnumerable().Reverse())
        {
            if (removedOwnedPaths.Contains(file.Route))
            {
                continue;
            }

            var observation = Ownership.Observe(file);
            if (observation is ArtifactObservation.Missing or ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(file.Route);
                continue;
            }

            if (observation == ArtifactObservation.Inconclusive
                || !ShouldDeleteFile(scope, file.Route, targetRoot))
            {
                cleanupIncompletePaths.Add(file.Route);
                continue;
            }

            var deletion = Ownership.TryDelete(file, OnRollbackProofComplete);
            if (deletion.Removed)
            {
                removedOwnedPaths.Add(file.Route);
            }
            else if (deletion.Conclusive)
            {
                targetChangedPaths.UnionWith(deletion.RetainedPaths);
            }
            else
            {
                cleanupIncompletePaths.UnionWith(deletion.RetainedPaths);
            }
        }

        foreach (var directory in createdDirectories.AsEnumerable().Reverse())
        {
            if (removedOwnedPaths.Contains(directory.Route))
            {
                continue;
            }

            var observation = Ownership.Observe(directory);
            if (observation is ArtifactObservation.Missing or ArtifactObservation.Changed)
            {
                targetChangedPaths.Add(directory.Route);
                continue;
            }

            if (observation == ArtifactObservation.Inconclusive)
            {
                cleanupIncompletePaths.Add(directory.Route);
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory.Route)
                    .Select(Path.GetFullPath)
                    .ToArray();
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                targetChangedPaths.Add(directory.Route);
                continue;
            }
            catch (Exception exception) when (IsFileSystemObservationFailure(exception))
            {
                cleanupIncompletePaths.Add(directory.Route);
                continue;
            }

            if (entries.Length > 0)
            {
                targetChangedPaths.UnionWith(entries.Where(entry => !IsKnownRetainedEntry(entry)));
                continue;
            }

            if (!ShouldDeleteDirectory(scope, directory.Route, targetRoot))
            {
                if (scope == RollbackScope.AfterLeaseRelease)
                {
                    cleanupIncompletePaths.Add(directory.Route);
                }

                continue;
            }

            var deletion = Ownership.TryDeleteEmpty(directory, OnRollbackProofComplete);
            if (deletion.Removed)
            {
                removedOwnedPaths.Add(directory.Route);
            }
            else if (!deletion.Conclusive)
            {
                cleanupIncompletePaths.UnionWith(deletion.RetainedPaths);
            }
            else
            {
                ClassifyRetainedDirectory(directory, deletion, targetChangedPaths, cleanupIncompletePaths);
            }
        }

        var retainedOwnedPaths = createdFiles
            .Where(file => !removedOwnedPaths.Contains(file.Route))
            .Where(file => Ownership.Observe(file) is ArtifactObservation.Unchanged or ArtifactObservation.Inconclusive)
            .Select(file => file.Route)
            .Concat(createdDirectories
                .Where(directory => !removedOwnedPaths.Contains(directory.Route))
                .Where(directory => Ownership.Observe(directory) is ArtifactObservation.Unchanged or ArtifactObservation.Inconclusive)
                .Select(directory => directory.Route))
            .Concat(cleanupIncompletePaths);
        return new NewProjectRollbackResult(
            SortPaths(targetChangedPaths),
            SortPaths(cleanupIncompletePaths),
            SortPaths(retainedOwnedPaths));
    }

    private void ClassifyRetainedDirectory(
        DirectoryReceipt directory,
        ExactFileSystemObjectOwnership.DeletionResult deletion,
        ISet<string> targetChangedPaths,
        ISet<string> cleanupIncompletePaths)
    {
        var observation = Ownership.Observe(directory);
        if (observation is ArtifactObservation.Missing or ArtifactObservation.Changed)
        {
            targetChangedPaths.Add(directory.Route);
        }
        else if (observation == ArtifactObservation.Inconclusive)
        {
            cleanupIncompletePaths.Add(directory.Route);
        }
        else
        {
            var entries = deletion.RetainedPaths
                .Where(path => !path.Equals(directory.Route, PathComparison))
                .ToArray();
            if (entries.Length == 0)
            {
                cleanupIncompletePaths.Add(directory.Route);
            }
            else
            {
                targetChangedPaths.UnionWith(entries.Where(entry => !IsKnownRetainedEntry(entry)));
            }
        }
    }

    private static bool ShouldDeleteFile(RollbackScope scope, string path, string? targetRoot)
        => scope == RollbackScope.AllTrackedArtifacts
            || scope == RollbackScope.UnderLease && IsSameOrDescendant(path, targetRoot!);

    private static bool ShouldDeleteDirectory(RollbackScope scope, string path, string? targetRoot)
        => scope switch
        {
            RollbackScope.AllTrackedArtifacts => true,
            RollbackScope.UnderLease => IsDescendant(path, targetRoot!),
            RollbackScope.AfterLeaseRelease => path.Equals(targetRoot, PathComparison)
                || IsDescendant(targetRoot!, path),
            _ => false
        };

    private void OnRollbackProofComplete(string path)
        => rollbackObserver?.OnProofComplete(path);

    private bool IsKnownRetainedEntry(string path)
        => allowedLeaseMarkerPath is not null && path.Equals(allowedLeaseMarkerPath, PathComparison)
            || retainedCreationFailures.ContainsKey(path)
            || createdFiles.Any(file => file.Route.Equals(path, PathComparison))
            || createdDirectories.Any(directory => directory.Route.Equals(path, PathComparison));

    private static TargetEnumeration EnumerateTargetEntries(string targetRoot)
    {
        var entries = new HashSet<string>(PathComparer);
        var observationIncompletePaths = new HashSet<string>(PathComparer);
        try
        {
            _ = File.GetAttributes(targetRoot);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TargetEnumeration(entries, observationIncompletePaths);
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            observationIncompletePaths.Add(targetRoot);
            return new TargetEnumeration(entries, observationIncompletePaths);
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(targetRoot);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            string[] directoryEntries;
            try
            {
                directoryEntries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (IsFileSystemObservationFailure(ex))
            {
                observationIncompletePaths.Add(directory);
                continue;
            }

            foreach (var entry in directoryEntries)
            {
                var fullPath = Path.GetFullPath(entry);
                entries.Add(fullPath);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(fullPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }
                catch (Exception ex) when (IsFileSystemObservationFailure(ex))
                {
                    observationIncompletePaths.Add(fullPath);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory)
                    && !attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pendingDirectories.Push(fullPath);
                }
            }
        }

        return new TargetEnumeration(entries, observationIncompletePaths);
    }

    private static bool IsDescendant(string path, string directory)
    {
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryPrefix, PathComparison);
    }

    private static bool IsSameOrDescendant(string path, string directory)
        => path.Equals(directory, PathComparison)
            || IsDescendant(path, directory);

    private static ArtifactObservation ObserveAllowedLeaseMarker(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? ArtifactObservation.Unchanged
                    : ArtifactObservation.Changed;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ArtifactObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemObservationFailure(ex))
        {
            return ArtifactObservation.Inconclusive;
        }
    }

    private static bool IsFileSystemObservationFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException;

    private void RejectDuplicateTrackedPath(string path)
    {
        if (retainedCreationFailures.ContainsKey(path)
            || createdFiles.Any(file => file.Route.Equals(path, PathComparison))
            || createdDirectories.Any(directory => directory.Route.Equals(path, PathComparison)))
        {
            throw new InvalidOperationException(
                $"The project artifact path is already tracked: {path}");
        }
    }

    private static IReadOnlyList<string> SortPaths(IEnumerable<string> paths)
        => paths
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record TargetEnumeration(
        HashSet<string> Paths,
        HashSet<string> ObservationIncompletePaths);

    private sealed record PathObservation(string Path, ArtifactObservation Observation);

    private sealed record RetainedCreationFailure(bool TargetChanged, bool RollbackUnproven);

    private enum RollbackScope
    {
        AllTrackedArtifacts,
        UnderLease,
        AfterLeaseRelease
    }
}

internal sealed record NewProjectTargetInventoryResult(
    IReadOnlyList<string> TargetChangedPaths,
    IReadOnlyList<string> ObservationIncompletePaths)
{
    public bool IsComplete => TargetChangedPaths.Count == 0
        && ObservationIncompletePaths.Count == 0;

    public bool IsConclusive => ObservationIncompletePaths.Count == 0;
}

internal sealed record NewProjectRollbackResult(
    IReadOnlyList<string> TargetChangedPaths,
    IReadOnlyList<string> CleanupIncompletePaths,
    IReadOnlyList<string> RetainedOwnedPaths)
{
    public bool IsComplete => RetainedOwnedPaths.Count == 0
        && CleanupIncompletePaths.Count == 0;
}

internal sealed class NewProjectArtifactAlreadyExistsException : IOException
{
    public NewProjectArtifactAlreadyExistsException(string path, Exception innerException)
        : base($"The project artifact already exists: {path}", innerException)
    {
    }
}

internal sealed class NewProjectArtifactEvidenceMismatchException : IOException
{
    public NewProjectArtifactEvidenceMismatchException(
        string path,
        bool observationIncomplete)
        : base(
            observationIncomplete
                ? $"The created project artifact could not be conclusively matched to its trusted evidence: {path}"
                : $"The created project artifact no longer matches its trusted evidence: {path}")
    {
        Path = path;
        ObservationIncomplete = observationIncomplete;
    }

    public string Path { get; }

    public bool ObservationIncomplete { get; }
}
