using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

/// <summary>
/// Performs the file mutations owned by a recoverable export transaction.
/// </summary>
public interface IExportDestinationFileOperations
{
    /// <summary>Creates a directory and any missing parent directories.</summary>
    void CreateDirectory(string path);

    /// <summary>Copies one file with the requested overwrite policy.</summary>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Deletes one file.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes one directory.</summary>
    void DeleteDirectory(string path, bool recursive);
}

/// <summary>
/// Uses the local filesystem for recoverable export mutations.
/// </summary>
public sealed class ExportDestinationFileOperations : IExportDestinationFileOperations
{
    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}

internal sealed class RecoverableExportDestinationTransaction
{
    private const string RecoveryDirectoryPrefix = ".vba-dev-export-recovery-";
    private readonly IExportDestinationFileOperations fileOperations;

    public RecoverableExportDestinationTransaction(IExportDestinationFileOperations fileOperations)
    {
        this.fileOperations = fileOperations;
    }

    public void Apply(
        string stagingDirectory,
        string destinationDirectory,
        bool removeStaleSources = true)
    {
        var plan = ExportDestinationPlan.Create(
            stagingDirectory,
            destinationDirectory,
            removeStaleSources);
        var recoveryDirectory = Path.Combine(
            plan.DestinationDirectory,
            $"{RecoveryDirectoryPrefix}{Guid.NewGuid():N}");
        var destinationExisted = Directory.Exists(plan.DestinationDirectory);
        var createdDestinationDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutationStarted = false;
        var applyCompleted = false;

        try
        {
            EnsureDestinationDirectory(
                plan.DestinationDirectory,
                plan.DestinationDirectory,
                createdDestinationDirectories);
            fileOperations.CreateDirectory(recoveryDirectory);

            foreach (var relativePath in plan.ExistingRelativePaths)
            {
                var sourcePath = ResolveWithin(plan.DestinationDirectory, relativePath);
                var recoveryPath = ResolveWithin(recoveryDirectory, relativePath);
                EnsureDirectory(Path.GetDirectoryName(recoveryPath)!);
                fileOperations.CopyFile(sourcePath, recoveryPath, overwrite: false);
            }

            mutationStarted = true;
            foreach (var relativePath in plan.ExistingRelativePaths)
            {
                fileOperations.DeleteFile(ResolveWithin(plan.DestinationDirectory, relativePath));
            }

            foreach (var placement in plan.Placements)
            {
                var targetPath = ResolveWithin(plan.DestinationDirectory, placement.TargetRelativePath);
                EnsureDestinationDirectory(
                    Path.GetDirectoryName(targetPath)!,
                    plan.DestinationDirectory,
                    createdDestinationDirectories);
                fileOperations.CopyFile(placement.StagedPath, targetPath, overwrite: true);
            }

            applyCompleted = true;
            fileOperations.DeleteDirectory(recoveryDirectory, recursive: true);
        }
        catch (Exception applyError)
        {
            if (!mutationStarted)
            {
                var cleanupErrors = CleanupBeforeMutationFailure(
                    recoveryDirectory,
                    plan.DestinationDirectory,
                    destinationExisted);
                throw CreateProtectionFailureException(
                    plan.DestinationDirectory,
                    destinationExisted,
                    recoveryDirectory,
                    applyError,
                    cleanupErrors);
            }

            if (applyCompleted)
            {
                throw CreatePostApplyCleanupException(
                    recoveryDirectory,
                    removeStaleSources,
                    applyError);
            }

            var rollbackResult = RollBack(
                plan,
                recoveryDirectory,
                createdDestinationDirectories);
            if (rollbackResult.RestorationErrors.Count > 0)
            {
                throw CreateIncompleteRollbackException(
                    plan.DestinationDirectory,
                    recoveryDirectory,
                    applyError,
                    rollbackResult.RestorationErrors);
            }

            if (rollbackResult.CleanupErrors.Count > 0)
            {
                throw CreateRollbackCleanupException(
                    recoveryDirectory,
                    createdDestinationDirectories,
                    applyError,
                    rollbackResult.CleanupErrors);
            }

            throw new InvalidOperationException(
                $"Export apply failed; the prior destination was restored. {applyError.Message}",
                applyError);
        }
    }

    private RollbackResult RollBack(
        ExportDestinationPlan plan,
        string recoveryDirectory,
        IReadOnlySet<string> createdDestinationDirectories)
    {
        var restorationErrors = new List<Exception>();
        var currentRelativePaths = plan.ExistingRelativePaths
            .Concat(plan.Placements.Select(placement => placement.TargetRelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in currentRelativePaths)
        {
            TryRollbackOperation(() =>
            {
                var path = ResolveWithin(plan.DestinationDirectory, relativePath);
                if (File.Exists(path))
                {
                    fileOperations.DeleteFile(path);
                }
            }, restorationErrors);
        }

        foreach (var relativePath in plan.ExistingRelativePaths)
        {
            TryRollbackOperation(() =>
            {
                var recoveryPath = ResolveWithin(recoveryDirectory, relativePath);
                if (!File.Exists(recoveryPath))
                {
                    throw new IOException($"Recovery file is missing: {recoveryPath}");
                }

                var destinationPath = ResolveWithin(plan.DestinationDirectory, relativePath);
                EnsureDirectory(Path.GetDirectoryName(destinationPath)!);
                fileOperations.CopyFile(recoveryPath, destinationPath, overwrite: true);
            }, restorationErrors);
        }

        if (restorationErrors.Count > 0)
        {
            return new RollbackResult(restorationErrors, []);
        }

        var cleanupErrors = new List<Exception>();
        RemoveEmptyCreatedDirectories(createdDestinationDirectories, cleanupErrors);
        if (cleanupErrors.Count == 0)
        {
            TryRollbackOperation(() =>
            {
                if (Directory.Exists(recoveryDirectory))
                {
                    fileOperations.DeleteDirectory(recoveryDirectory, recursive: true);
                }
            }, cleanupErrors);

            if (cleanupErrors.Count == 0)
            {
                RemoveEmptyCreatedDirectories(createdDestinationDirectories, cleanupErrors);
            }
        }

        return new RollbackResult(restorationErrors, cleanupErrors);
    }

    private void RemoveEmptyCreatedDirectories(
        IReadOnlySet<string> createdDestinationDirectories,
        ICollection<Exception> errors)
    {
        foreach (var directoryPath in createdDestinationDirectories
                     .OrderByDescending(path => path.Length)
                     .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            TryRollbackOperation(() =>
            {
                if (Directory.Exists(directoryPath)
                    && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    fileOperations.DeleteDirectory(directoryPath, recursive: false);
                }
            }, errors);
        }
    }

    private IReadOnlyList<Exception> CleanupBeforeMutationFailure(
        string recoveryDirectory,
        string destinationDirectory,
        bool destinationExisted)
    {
        var errors = new List<Exception>();
        TryRollbackOperation(() =>
        {
            if (Directory.Exists(recoveryDirectory))
            {
                fileOperations.DeleteDirectory(recoveryDirectory, recursive: true);
            }
        }, errors);

        TryRollbackOperation(() =>
        {
            if (
                !destinationExisted
                && Directory.Exists(destinationDirectory)
                && !Directory.EnumerateFileSystemEntries(destinationDirectory).Any()
            )
            {
                fileOperations.DeleteDirectory(destinationDirectory, recursive: false);
            }
        }, errors);

        return errors;
    }

    private void EnsureDestinationDirectory(
        string directoryPath,
        string destinationDirectory,
        ISet<string> createdDirectories)
    {
        if (Directory.Exists(directoryPath))
        {
            return;
        }

        var missingDirectories = new Stack<string>();
        var current = directoryPath;
        while (!Directory.Exists(current) && IsPathWithinOrEqual(current, destinationDirectory))
        {
            missingDirectories.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || SamePath(parent, current))
            {
                break;
            }
            current = parent;
        }

        fileOperations.CreateDirectory(directoryPath);
        foreach (var createdDirectory in missingDirectories)
        {
            createdDirectories.Add(createdDirectory);
        }
    }

    private void EnsureDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            fileOperations.CreateDirectory(directoryPath);
        }
    }

    private static InvalidOperationException CreateIncompleteRollbackException(
        string destinationDirectory,
        string recoveryDirectory,
        Exception applyError,
        IReadOnlyList<Exception> rollbackErrors)
        => new(
            $"Export apply failed and rollback was incomplete. Recovery area retained at '{Path.GetFullPath(recoveryDirectory)}'. "
            + $"Manually remove affected VBA source and sidecar files from '{Path.GetFullPath(destinationDirectory)}', "
            + "copy the recovery area contents back while preserving their relative paths, then remove the recovery area. "
            + $"Apply error: {applyError.Message} Rollback error: {string.Join(" | ", rollbackErrors.Select(error => error.Message))}",
            new AggregateException([applyError, .. rollbackErrors]));

    private static InvalidOperationException CreateProtectionFailureException(
        string destinationDirectory,
        bool destinationExisted,
        string recoveryDirectory,
        Exception protectionError,
        IReadOnlyList<Exception> cleanupErrors)
    {
        var recoveryRetained = Directory.Exists(recoveryDirectory);
        var retainedProtection = recoveryRetained
            ? $"Incomplete protection data was retained at '{Path.GetFullPath(recoveryDirectory)}'. "
              + "Do not use this incomplete protection data to restore or replace the unchanged destination source state; "
              + "remove it after investigating the protection and cleanup errors. "
            : "No recovery area was retained. ";
        var retainedDestination = !destinationExisted
                                  && !recoveryRetained
                                  && Directory.Exists(destinationDirectory)
            ? $"Empty destination directory remains at '{Path.GetFullPath(destinationDirectory)}'. "
            : string.Empty;
        var cleanupState = cleanupErrors.Count == 0
            ? "Protection data cleanup completed."
            : "The protection operation and its cleanup both failed.";
        var innerError = cleanupErrors.Count == 0
            ? protectionError
            : new AggregateException([protectionError, .. cleanupErrors]);

        return new InvalidOperationException(
            "Export destination protection failed before mutation; no destination source or sidecar file was changed. "
            + retainedProtection
            + retainedDestination
            + cleanupState,
            innerError);
    }

    private static InvalidOperationException CreateRollbackCleanupException(
        string recoveryDirectory,
        IReadOnlySet<string> createdDestinationDirectories,
        Exception applyError,
        IReadOnlyList<Exception> cleanupErrors)
    {
        var retainedPaths = createdDestinationDirectories
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Concat(Directory.Exists(recoveryDirectory)
                ? [Path.GetFullPath(recoveryDirectory)]
                : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retainedState = retainedPaths.Length == 0
            ? "No recovery area or export-created directory was retained."
            : $"Retained rollback cleanup paths: {string.Join(", ", retainedPaths.Select(path => $"'{path}'"))}. "
              + "The prior source state is already restored; inspect these paths and remove only export-owned remnants.";

        return new InvalidOperationException(
            "Export apply failed; the prior destination source state was restored, but cleanup was incomplete. "
            + retainedState,
            new AggregateException([applyError, .. cleanupErrors]));
    }

    private static InvalidOperationException CreatePostApplyCleanupException(
        string recoveryDirectory,
        bool removeStaleSources,
        Exception cleanupError)
    {
        var retainedState = Directory.Exists(recoveryDirectory)
            ? $"Partial recovery cleanup data was retained at '{Path.GetFullPath(recoveryDirectory)}'. "
              + "Do not use it to restore or replace the applied snapshot; inspect and remove it after resolving the cleanup failure."
            : "No recovery cleanup path was retained.";

        return new InvalidOperationException(
            "Export apply completed, but recovery cleanup was incomplete. "
            + (removeStaleSources
                ? "The new exact source snapshot remains applied. "
                : "The requested source overlay remains applied. ")
            + retainedState,
            cleanupError);
    }

    private static void TryRollbackOperation(Action operation, ICollection<Exception> errors)
    {
        try
        {
            operation();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private sealed record RollbackResult(
        IReadOnlyList<Exception> RestorationErrors,
        IReadOnlyList<Exception> CleanupErrors);

    private static string ResolveWithin(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsPathWithinOrEqual(resolved, root) || SamePath(resolved, root))
        {
            throw new InvalidOperationException($"Export path escapes its owned directory: {relativePath}");
        }

        return resolved;
    }

    private static bool IsPathWithinOrEqual(string path, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(directoryPath), Path.GetFullPath(path));
        return relativePath == "."
            || (
                relativePath.Length > 0
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath)
            );
    }

    private static bool SamePath(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record ExportPlacement(string StagedPath, string TargetRelativePath);

    private sealed record ExportDestinationPlan(
        string StagingDirectory,
        string DestinationDirectory,
        IReadOnlyList<string> ExistingRelativePaths,
        IReadOnlyList<ExportPlacement> Placements)
    {
        public static ExportDestinationPlan Create(
            string stagingDirectory,
            string destinationDirectory,
            bool removeStaleSources)
        {
            var staging = Path.GetFullPath(stagingDirectory);
            var destination = Path.GetFullPath(destinationDirectory);
            if (removeStaleSources)
            {
                RejectReparsePoints(destination);
            }
            else
            {
                RejectReparsePoint(destination);
            }
            RejectRetainedRecoveryAreas(destination);

            var stagedSources = DocumentSourceSetLayout.EnumerateVbaSourceFiles(staging);
            DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(staging, stagedSources);
            ValidateStagedSidecars(staging);

            var existingLayout = removeStaleSources
                ? DocumentSourceSetLayout.CaptureExistingSourceLayout(destination)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var placements = new List<ExportPlacement>();
            foreach (var stagedSource in stagedSources
                         .OrderBy(source => Path.GetFileName(source.SourcePath), StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(stagedSource.SourcePath);
                var relativePath = existingLayout.TryGetValue(fileName, out var existingRelativePath)
                    ? existingRelativePath
                    : fileName;
                placements.Add(new ExportPlacement(stagedSource.SourcePath, relativePath));

                if (stagedSource.BinaryPath is not null)
                {
                    placements.Add(new ExportPlacement(
                        stagedSource.BinaryPath,
                        Path.ChangeExtension(relativePath, ".frx")));
                }
            }

            ValidatePlacements(destination, placements);
            var orderedPlacements = placements
                .OrderBy(placement => placement.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var existingRelativePaths = !Directory.Exists(destination)
                ? []
                : removeStaleSources
                    ? Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                        .Where(DocumentSourceSetLayout.IsVbaSourceOrSidecar)
                        .Select(path => Path.GetRelativePath(destination, path))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : orderedPlacements
                        .Select(placement => placement.TargetRelativePath)
                        .Where(relativePath => File.Exists(ResolveWithin(destination, relativePath)))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

            return new ExportDestinationPlan(
                staging,
                destination,
                existingRelativePaths,
                orderedPlacements);
        }

        private static void RejectRetainedRecoveryAreas(string destinationDirectory)
        {
            if (!Directory.Exists(destinationDirectory))
            {
                return;
            }

            var retainedRecovery = Directory
                .EnumerateDirectories(
                    destinationDirectory,
                    $"{RecoveryDirectoryPrefix}*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (retainedRecovery is not null)
            {
                throw new InvalidOperationException(
                    "Export destination contains retained export recovery or protection data that requires inspection before retrying: "
                    + Path.GetFullPath(retainedRecovery));
            }
        }

        private static void RejectReparsePoints(string destinationDirectory)
        {
            RejectReparsePoint(destinationDirectory);
            if (!Directory.Exists(destinationDirectory))
            {
                return;
            }

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(destinationDirectory);
            while (pendingDirectories.TryPop(out var directoryPath))
            {
                var directoryAttributes = File.GetAttributes(directoryPath);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        $"Export destination contains a reparse point: {Path.GetFullPath(directoryPath)}");
                }

                foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                             directoryPath,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Export destination contains a reparse point: {Path.GetFullPath(entryPath)}");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entryPath);
                    }
                }
            }
        }

        private static void RejectReparsePoint(string path)
        {
            if (!Path.Exists(path))
            {
                return;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Export destination contains a reparse point: {Path.GetFullPath(path)}");
            }
        }

        private static void ValidateStagedSidecars(string stagingDirectory)
        {
            foreach (var sidecarPath in DocumentSourceSetLayout.EnumerateFormSidecarPaths(stagingDirectory))
            {
                if (!DocumentSourceSetLayout.HasSameDirectoryForm(sidecarPath))
                {
                    throw new InvalidOperationException(
                        $"Staged export contains a form sidecar without a same-directory form: {sidecarPath}");
                }
            }
        }

        private static void ValidatePlacements(
            string destinationDirectory,
            IReadOnlyList<ExportPlacement> placements)
        {
            var duplicateTarget = placements
                .GroupBy(placement => placement.TargetRelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Skip(1).Any());
            if (duplicateTarget is not null)
            {
                throw new InvalidOperationException(
                    $"Export placement plan contains duplicate target path '{duplicateTarget.Key}'.");
            }

            foreach (var placement in placements)
            {
                var targetPath = ResolveWithin(destinationDirectory, placement.TargetRelativePath);
                RejectReparsePoint(targetPath);
                if (Directory.Exists(targetPath))
                {
                    throw new InvalidOperationException(
                        $"Export placement target is an existing directory: {targetPath}");
                }

                var parent = Path.GetDirectoryName(targetPath);
                while (parent is not null && IsPathWithinOrEqual(parent, destinationDirectory))
                {
                    if (File.Exists(parent))
                    {
                        throw new InvalidOperationException(
                            $"Export placement parent is an existing file: {parent}");
                    }
                    if (SamePath(parent, destinationDirectory))
                    {
                        break;
                    }
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }
    }
}
