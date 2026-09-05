using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.FileSystem;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Creates a new Excel workbook-backed VBA project with default source, bin, and publish layout.
/// </summary>
public sealed class NewProjectCommand
{
    private const string CommonModulesRepositoryNotFoundCode =
        "commonModulesRepositoryNotFound";
    private const string CommonModulesRepositoryNotFoundMessage =
        "CommonModules repository was not found; the project was created without shared modules.";
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly IProjectManifestStore manifestStore;
    private readonly IInitialWorkbookCreator initialWorkbookCreator;
    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly VbaProjectReferencePlanner referencePlanner;
    private readonly IProjectManifestMutationLeaseProvider leaseProvider;
    private readonly CommonModulesPackageSnapshotFactory packageSnapshotFactory;
    private readonly NewProjectAncestorSourceSetIsolation ancestorSourceSetIsolation;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

    /// <summary>
    /// Creates the new-project command.
    /// </summary>
    /// <param name="manifestStore">The store used to write the initial project manifest.</param>
    /// <param name="initialWorkbookCreator">The workbook creator used to generate the source template workbook.</param>
    /// <param name="commonModulesManifestReader">The reader used to discover initial CommonModules files.</param>
    public NewProjectCommand(
        IProjectManifestStore manifestStore,
        IInitialWorkbookCreator initialWorkbookCreator,
        CommonModulesManifestReader commonModulesManifestReader,
        VbaProjectReferencePlanner referencePlanner,
        IProjectManifestMutationLeaseProvider leaseProvider)
        : this(
            manifestStore,
            initialWorkbookCreator,
            commonModulesManifestReader,
            referencePlanner,
            leaseProvider,
            new FileSystemPathIdentityResolver())
    {
    }

    internal NewProjectCommand(
        IProjectManifestStore manifestStore,
        IInitialWorkbookCreator initialWorkbookCreator,
        CommonModulesManifestReader commonModulesManifestReader,
        VbaProjectReferencePlanner referencePlanner,
        IProjectManifestMutationLeaseProvider leaseProvider,
        IFileSystemPathIdentityResolver pathIdentityResolver,
        CommonModulesPackageSnapshotFactory? packageSnapshotFactory = null)
    {
        this.manifestStore = manifestStore;
        this.initialWorkbookCreator = initialWorkbookCreator;
        this.commonModulesManifestReader = commonModulesManifestReader;
        this.referencePlanner = referencePlanner;
        this.leaseProvider = leaseProvider;
        this.pathIdentityResolver = pathIdentityResolver;
        this.packageSnapshotFactory = packageSnapshotFactory
            ?? new CommonModulesPackageSnapshotFactory(
                new CommonModulesPackageReader(commonModulesManifestReader));
        ancestorSourceSetIsolation = new NewProjectAncestorSourceSetIsolation(
            manifestStore,
            pathIdentityResolver);
    }

    /// <summary>
    /// Creates the project directory, source template workbook, initial CommonModules files, and project manifest.
    /// </summary>
    /// <param name="request">The new-project command input.</param>
    /// <returns>The command result describing created project state or validation errors.</returns>
    public CommandResult Run(NewProjectCommandRequest request)
        => RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Creates one project while honoring cooperative cancellation before the initial manifest commit.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        NewProjectCommandRequest request,
        CancellationToken cancellationToken)
    {
        NewProjectPathPlan pathPlan;
        try
        {
            pathPlan = ResolvePathPlan(request);
        }
        catch (Exception ex) when (ex is ProjectManifestException
            or ArgumentException
            or IOException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return CommandResult.UsageError(ex.Message);
        }

        var projectRoot = pathPlan.RequestedProjectRoot;
        var projectName = pathPlan.ProjectName;
        var documentName = pathPlan.DocumentName;
        var warnings = new List<NewProjectWarning>();
        using var artifacts = new NewProjectArtifactTracker();
        IProjectManifestMutationLease? lease = null;
        CommonModulesPackageSnapshot? packageSnapshot = null;
        FileSystemPathIdentity? commonModulesRepositoryRouteIdentity = null;
        ProjectManifest? committedManifest = null;
        string? operationProjectRoot = null;
        string? leaseMarkerPath = null;
        string? workbookPath = null;
        Exception? failure = null;
        var manifestCommitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialProjectIdentity =
                ancestorSourceSetIsolation.ValidateInitial(projectRoot);
            artifacts.EnsureDirectory(initialProjectIdentity.OperationPath);
            cancellationToken.ThrowIfCancellationRequested();
            lease = await leaseProvider.AcquireAsync(
                    projectRoot,
                ProjectManifestMutationCommand.NewExcel,
                cancellationToken)
                .ConfigureAwait(false);
            operationProjectRoot = lease.ProjectIdentity.OperationPath;
            leaseMarkerPath = lease.ManifestPath + ".vba-dev.lock";
            artifacts.AllowLeaseMarker(
                operationProjectRoot,
                leaseMarkerPath);
            lease.ProveOwnershipContinuity();
            ValidateLeaseIdentityContinuity(
                projectRoot,
                initialProjectIdentity,
                lease.ProjectIdentity);
            ValidateMaterializationPaths(
                operationProjectRoot,
                documentName);
            ancestorSourceSetIsolation.ValidateFinal(
                projectRoot,
                initialProjectIdentity);
            EnsureCompleteTargetInventory(
                artifacts,
                operationProjectRoot,
                leaseMarkerPath);

            var commonModulesRepository = DiscoverCommonModulesRepository(
                operationProjectRoot);
            if (commonModulesRepository is null)
            {
                warnings.Add(new NewProjectWarning(
                    CommonModulesRepositoryNotFoundCode,
                    CommonModulesRepositoryNotFoundMessage));
            }
            else
            {
                commonModulesRepositoryRouteIdentity =
                    EstablishDurableCommonModulesRepositoryRoute(
                        projectRoot,
                        commonModulesRepository);
                packageSnapshot = packageSnapshotFactory.Capture(
                    commonModulesRepository,
                    cancellationToken);
            }

            var sourceSetPath = Path.Combine(
                operationProjectRoot,
                "src",
                documentName);
            var binPath = Path.Combine(operationProjectRoot, "bin");
            var publishPath = Path.Combine(operationProjectRoot, "publish");
            var commonModulesPlan = CreateInitialCommonModulesPlan(
                packageSnapshot,
                sourceSetPath);
            cancellationToken.ThrowIfCancellationRequested();

            artifacts.EnsureDirectory(sourceSetPath);
            artifacts.EnsureDirectory(binPath);
            artifacts.EnsureDirectory(publishPath);

            workbookPath = Path.Combine(sourceSetPath, $"{documentName}.xlsm");
            if (initialWorkbookCreator is not IReceiptInitialWorkbookCreator receiptCreator)
            {
                throw new InvalidOperationException(
                    "The initial workbook creator cannot issue an invocation-owned project artifact receipt.");
            }

            var initialWorkbook = await receiptCreator
                .CreateInitialWorkbookAsync(workbookPath, artifacts.Ownership, cancellationToken)
                .ConfigureAwait(false);
            var workbookReceipt = initialWorkbook.OwnedArtifactReceipt
                ?? throw new NewProjectTargetChangedException(
                    [workbookPath],
                    new InvalidOperationException("The initial workbook creator returned no ownership receipt."));
            if (!Path.GetFullPath(workbookReceipt.Route)
                    .Equals(Path.GetFullPath(workbookPath), PathComparison))
            {
                throw new NewProjectTargetChangedException(
                    [workbookPath],
                    new InvalidOperationException(
                        "The initial workbook creator returned an ownership receipt for a different path."));
            }

            artifacts.RecordCreatedFile(workbookReceipt);
            var references = await CreateReferenceEntriesAsync(
                    workbookPath,
                    initialWorkbook.ReferenceNames,
                    commonModulesPlan.RequiredReferences,
                    cancellationToken)
                .ConfigureAwait(false);
            CopyInitialCommonModules(commonModulesPlan, artifacts);

            var manifest = ProjectManifest.CreateDefault(
                projectName,
                documentName,
                operationProjectRoot,
                commonModulesRepository,
                commonModulesPlan.InstalledModules,
                references);
            var manifestStage = NewProjectInitialManifestStager.Stage(
                lease.ManifestPath,
                manifest,
                artifacts);
            ancestorSourceSetIsolation.ValidateFinal(
                projectRoot,
                lease.ProjectIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCompleteTargetInventory(
                artifacts,
                operationProjectRoot,
                leaseMarkerPath);
            lease.ProveOwnershipContinuity();
            if (commonModulesRepositoryRouteIdentity is not null)
            {
                ProveDurableCommonModulesRepositoryRoute(
                    projectRoot,
                    commonModulesRepositoryRouteIdentity);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                manifestStage.CommitCreateOnly();
            }
            catch (IOException ex) when (
                ObservePathEntry(lease.ManifestPath) == PathEntryObservation.Present)
            {
                throw new NewProjectTargetChangedException(
                    [lease.ManifestPath],
                    ex);
            }

            manifestCommitted = true;
            committedManifest = manifest;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (manifestCommitted)
        {
            return await CompleteCommittedCreationAsync(
                    request.Format,
                    projectRoot,
                    projectName,
                    documentName,
                    committedManifest!,
                    lease!,
                    packageSnapshot,
                    warnings)
                .ConfigureAwait(false);
        }

        return await CompleteFailedCreationAsync(
                projectRoot,
                operationProjectRoot,
                workbookPath,
                leaseMarkerPath,
                failure ?? new InvalidOperationException(
                    "Project creation did not reach a terminal state."),
                artifacts,
                lease,
                packageSnapshot)
            .ConfigureAwait(false);
    }

    private static void CopyInitialCommonModules(
        NewProjectCommonModulesPlan plan,
        NewProjectArtifactTracker artifacts)
    {
        foreach (var artifact in plan.Artifacts)
        {
            artifacts.EnsureDirectory(Path.GetDirectoryName(artifact.TargetPath)!);
            artifacts.CreateFile(artifact.TargetPath, artifact.Contents);
        }
    }

    private static NewProjectCommonModulesPlan CreateInitialCommonModulesPlan(
        CommonModulesPackageSnapshot? snapshot,
        string sourceSetPath)
    {
        if (snapshot is null)
        {
            return new NewProjectCommonModulesPlan([], [], []);
        }

        var requestedModuleFiles = snapshot.Entries
            .Where(entry => entry.HasCategory("runtime-baseline")
                || entry.HasCategory("test-foundation"))
            .Select(entry => entry.ModuleFile)
            .ToArray();
        var requested = requestedModuleFiles.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var selection = snapshot.ResolveRequestedPlan(requestedModuleFiles);
        ValidateSelectedEntryIdentities(selection.Entries, sourceSetPath);
        var commonModulesDirectory = Path.Combine(
            sourceSetPath,
            "common-modules");
        var copyArtifacts = new List<NewProjectCopyArtifact>();
        foreach (var entry in selection.Entries)
        {
            copyArtifacts.Add(new NewProjectCopyArtifact(
                Path.Combine(commonModulesDirectory, entry.InstalledModuleFile),
                snapshot.ReadFileBytes(entry.ModuleFile)));
            if (!entry.ModuleFile.EndsWith(".frm", StringComparison.Ordinal))
            {
                continue;
            }

            var sidecarName = Path.ChangeExtension(entry.ModuleFile, ".frx");
            if (snapshot.TryReadFileBytes(sidecarName, out var sidecarBytes))
            {
                copyArtifacts.Add(new NewProjectCopyArtifact(
                    Path.Combine(commonModulesDirectory, sidecarName),
                    sidecarBytes));
            }
        }

        var installedModules = selection.Entries
            .Select(entry => new InstalledCommonModule(
                entry.Name,
                entry.InstalledModuleFile,
                requested.Contains(entry.ModuleFile),
                entry.TestOnly))
            .ToArray();
        return new NewProjectCommonModulesPlan(
            copyArtifacts,
            installedModules,
            selection.RequiredReferences);
    }

    private static void ValidateSelectedEntryIdentities(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string sourceSetPath)
    {
        var byName = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var byModuleFile = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var byTargetPath = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (byName.TryGetValue(entry.Name, out var matchingName))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules selection contains duplicate CommonModules name '{entry.Name}': " +
                    $"'{matchingName.ModuleFile}' and '{entry.ModuleFile}'.");
            }

            if (byModuleFile.TryGetValue(entry.InstalledModuleFile, out var matchingModuleFile))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules selection contains duplicate flat moduleFile '{entry.InstalledModuleFile}': " +
                    $"'{matchingModuleFile.ModuleFile}' and '{entry.ModuleFile}'.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(sourceSetPath, "common-modules", entry.InstalledModuleFile));
            if (byTargetPath.TryGetValue(targetPath, out var matchingTarget))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules entries '{matchingTarget.ModuleFile}' and '{entry.ModuleFile}' " +
                    $"resolve to the same target source file: {targetPath}");
            }

            byName.Add(entry.Name, entry);
            byModuleFile.Add(entry.InstalledModuleFile, entry);
            byTargetPath.Add(targetPath, entry);
        }
    }

    private async Task<VbaProjectReference[]> CreateReferenceEntriesAsync(
        string workbookPath,
        IReadOnlyList<string> baselineReferenceNames,
        IReadOnlyList<string> requiredReferenceNames,
        CancellationToken cancellationToken)
    {
        var references = new List<VbaProjectReference>();
        var selectedNames = new HashSet<string>(VbaProjectReferenceName.Comparer);
        foreach (var rawName in baselineReferenceNames)
        {
            var referenceName = rawName.Trim();
            if (referenceName.Length == 0
                || VbaProjectReferenceName.IsStandardLibrary(referenceName)
                || !selectedNames.Add(referenceName))
            {
                continue;
            }

            references.Add(new VbaProjectReference(
                referenceName,
                requested: true));
        }

        var missingRequiredNames = new List<string>();
        foreach (var rawName in requiredReferenceNames)
        {
            var referenceName = rawName.Trim();
            if (referenceName.Length == 0
                || VbaProjectReferenceName.IsStandardLibrary(referenceName)
                || !selectedNames.Add(referenceName))
            {
                continue;
            }

            missingRequiredNames.Add(referenceName);
        }

        if (missingRequiredNames.Count == 0)
        {
            return references.ToArray();
        }

        var resolution = await referencePlanner.ResolveReferencesAsync(
                workbookPath,
                missingRequiredNames,
                cancellationToken)
            .ConfigureAwait(false);
        var resolvedReferences = referencePlanner.SelectManifestInputReferences(
            resolution,
            missingRequiredNames);
        references.AddRange(resolvedReferences.Select(reference =>
            new VbaProjectReference(reference.Name, requested: false)));
        return references.ToArray();
    }

    private static void ValidateLeaseIdentityContinuity(
        string requestedProjectRoot,
        FileSystemPathIdentity initialIdentity,
        FileSystemPathIdentity leasedIdentity)
    {
        var sameCanonicalPath = Path.TrimEndingDirectorySeparator(
                initialIdentity.CanonicalPath)
            .Equals(
                Path.TrimEndingDirectorySeparator(leasedIdentity.CanonicalPath),
                StringComparison.OrdinalIgnoreCase);
        var sameExistingObject = initialIdentity.ObjectIdentity is null
            || leasedIdentity.ObjectIdentity is not null
                && initialIdentity.ObjectIdentity == leasedIdentity.ObjectIdentity;
        var existingIdentityEstablished = !OperatingSystem.IsWindows()
            || leasedIdentity.ObjectIdentity is not null;
        if (sameCanonicalPath && sameExistingObject && existingIdentityEstablished)
        {
            return;
        }

        throw new NewProjectTargetChangedException(
            [Path.GetFullPath(requestedProjectRoot)]);
    }

    private static void EnsureCompleteTargetInventory(
        NewProjectArtifactTracker artifacts,
        string targetRoot,
        string leaseMarkerPath)
    {
        NewProjectTargetInventoryResult inventory;
        try
        {
            inventory = artifacts.ProveCompleteTargetInventory(
                targetRoot,
                leaseMarkerPath);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new NewProjectTargetChangedException([targetRoot], ex);
        }

        if (!inventory.IsComplete)
        {
            throw new NewProjectTargetChangedException(
                inventory.TargetChangedPaths);
        }
    }

    private static async Task<CommandResult> CompleteCommittedCreationAsync(
        string format,
        string requestedProjectRoot,
        string projectName,
        string documentName,
        ProjectManifest manifest,
        IProjectManifestMutationLease lease,
        CommonModulesPackageSnapshot? packageSnapshot,
        List<NewProjectWarning> warnings)
    {
        var cleanupFailures = new List<Exception>();
        var retainedPaths = new HashSet<string>(PathComparer);
        var retainedSetConclusive = true;
        if (packageSnapshot is not null)
        {
            try
            {
                var cleanup = packageSnapshot.Cleanup();
                if (!cleanup.Deleted)
                {
                    retainedPaths.Add(cleanup.RetainedPath!);
                    warnings.Add(new NewProjectWarning(
                        "commonModulesSnapshotCleanupFailed",
                        "The project was created, but its non-authoritative CommonModules "
                        + $"snapshot workspace could not be removed: \"{cleanup.RetainedPath}\"."));
                }
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "The project manifest was committed, but CommonModules snapshot "
                    + "cleanup could not be proved.",
                    ex));
                retainedPaths.Add(Path.GetFullPath(packageSnapshot.StagingPath));
                retainedSetConclusive = false;
            }
        }

        try
        {
            var release = await lease.ReleaseAsync().ConfigureAwait(false);
            foreach (var warning in release.Warnings)
            {
                warnings.Add(warning.Code.Equals(
                        "leaseMarkerCleanupFailed",
                        StringComparison.Ordinal)
                    ? new NewProjectWarning(
                        "leaseMarkerCleanupFailed",
                        "The project was created and its project lease was released, "
                        + "but the lease marker could not be removed: "
                        + $"\"{lease.ManifestPath}.vba-dev.lock\".")
                    : new NewProjectWarning(warning.Code, warning.Message));
            }
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(new InvalidOperationException(
                "The project manifest was committed, but its project lease release "
                + "and marker cleanup could not be proved.",
                ex));
            retainedPaths.Add(lease.ManifestPath + ".vba-dev.lock");
            retainedSetConclusive = false;
        }

        if (cleanupFailures.Count > 0)
        {
            return RenderFailure(
                requestedProjectRoot,
                lease.ProjectIdentity.OperationPath,
                cleanupFailures[0],
                [],
                retainedPaths,
                cleanupFailures.Skip(1).ToArray(),
                retainedSetConclusive);
        }

        var requestedManifestPath = Path.Combine(
            requestedProjectRoot,
            ProjectManifest.ManifestFileName);
        return RenderSuccess(
            format,
            requestedProjectRoot,
            requestedManifestPath,
            projectName,
            documentName,
            manifest,
            warnings);
    }

    private static async Task<CommandResult> CompleteFailedCreationAsync(
        string requestedProjectRoot,
        string? operationProjectRoot,
        string? workbookPath,
        string? leaseMarkerPath,
        Exception failure,
        NewProjectArtifactTracker artifacts,
        IProjectManifestMutationLease? lease,
        CommonModulesPackageSnapshot? packageSnapshot)
    {
        var targetChangedPaths = new HashSet<string>(PathComparer);
        var cleanupIncompletePaths = new HashSet<string>(PathComparer);
        var supplementalFailures = new List<Exception>();
        var retainedSetConclusive = true;
        var failureTree = EnumerateExceptionTree(failure).ToArray();
        var changedWorkbookPaths = failureTree
            .OfType<InitialWorkbookArtifactRetainedException>()
            .Where(exception => exception.TargetChanged)
            .Select(exception => exception.WorkbookPath)
            .ToHashSet(PathComparer);
        var cleanupOnlyWorkbookPaths = failureTree
            .OfType<InitialWorkbookArtifactRetainedException>()
            .Where(exception => !exception.TargetChanged)
            .Select(exception => exception.WorkbookPath)
            .Where(path => !changedWorkbookPaths.Contains(path))
            .ToHashSet(PathComparer);
        targetChangedPaths.UnionWith(changedWorkbookPaths);
        cleanupIncompletePaths.UnionWith(cleanupOnlyWorkbookPaths);
        foreach (var partialCreation in failureTree
                     .OfType<ExactFileSystemObjectOwnership.FileCreationCleanupException>())
        {
            if (partialCreation.TargetChanged)
            {
                targetChangedPaths.Add(partialCreation.Route);
                cleanupOnlyWorkbookPaths.Remove(partialCreation.Route);
            }

            if (!partialCreation.TargetChanged || partialCreation.RollbackUnproven)
            {
                cleanupIncompletePaths.Add(partialCreation.Route);
            }

            retainedSetConclusive &= !partialCreation.RollbackUnproven;
        }

        retainedSetConclusive &= !failureTree
            .OfType<ExactFileSystemObjectOwnership.RollbackException>().Any();
        foreach (var retainedSnapshot in failureTree
                     .OfType<CommonModulesPackageSnapshotRetainedException>())
        {
            var cleanup = retainedSnapshot.CleanupResult;
            if (cleanup.RetainedPath is not null)
            {
                cleanupIncompletePaths.Add(cleanup.RetainedPath);
            }

            cleanupIncompletePaths.UnionWith(cleanup.RetainedEntryPaths);
            cleanupIncompletePaths.UnionWith(cleanup.ObservationIncompletePaths);
            retainedSetConclusive &= cleanup.IsConclusive;
        }

        if (failure is NewProjectTargetChangedException targetChanged)
        {
            targetChangedPaths.UnionWith(targetChanged.Paths);
        }

        if (failureTree.OfType<ProjectManifestMutationException>().Any(
                exception => exception.Code.Equals(
                    "manifestMutationLeaseChanged",
                    StringComparison.Ordinal))
            && leaseMarkerPath is not null)
        {
            targetChangedPaths.Add(leaseMarkerPath);
        }

        if (failureTree.OfType<WorkbookAutomationCleanupException>().Any())
        {
            retainedSetConclusive = false;
            if (workbookPath is not null
                && ObservePathEntry(workbookPath) == PathEntryObservation.Present)
            {
                cleanupIncompletePaths.Add(workbookPath);
            }
        }

        if (packageSnapshot is not null)
        {
            try
            {
                var cleanup = packageSnapshot.Cleanup();
                if (!cleanup.Deleted && cleanup.RetainedPath is not null)
                {
                    cleanupIncompletePaths.Add(cleanup.RetainedPath);
                }
            }
            catch (Exception ex)
            {
                supplementalFailures.Add(ex);
                cleanupIncompletePaths.Add(packageSnapshot.StagingPath);
                retainedSetConclusive = false;
            }
        }

        NewProjectRollbackResult? rollback = null;
        try
        {
            rollback = lease is not null && operationProjectRoot is not null
                ? artifacts.RollbackUnderLease(operationProjectRoot)
                : artifacts.Rollback();
            targetChangedPaths.UnionWith(rollback.TargetChangedPaths);
        }
        catch (Exception ex)
        {
            supplementalFailures.Add(ex);
            retainedSetConclusive = false;
        }

        var releaseProved = lease is null;
        if (lease is not null)
        {
            try
            {
                var release = await lease.ReleaseAsync().ConfigureAwait(false);
                releaseProved = true;
                if (release.Warnings.Count > 0 && leaseMarkerPath is not null)
                {
                    cleanupIncompletePaths.Add(leaseMarkerPath);
                }
            }
            catch (Exception ex)
            {
                supplementalFailures.Add(ex);
                retainedSetConclusive = false;
                if (leaseMarkerPath is not null)
                {
                    cleanupIncompletePaths.Add(leaseMarkerPath);
                }
            }
        }

        if (releaseProved
            && lease is not null
            && operationProjectRoot is not null)
        {
            try
            {
                rollback = artifacts.RollbackAfterLeaseRelease(
                    operationProjectRoot);
                targetChangedPaths.UnionWith(rollback.TargetChangedPaths);
            }
            catch (Exception ex)
            {
                supplementalFailures.Add(ex);
                retainedSetConclusive = false;
            }
        }

        if (rollback is not null)
        {
            cleanupIncompletePaths.UnionWith(rollback.CleanupIncompletePaths);
            foreach (var cleanupOnlyWorkbookPath in cleanupOnlyWorkbookPaths)
            {
                targetChangedPaths.Remove(cleanupOnlyWorkbookPath);
            }

            foreach (var retainedOwnedPath in rollback.RetainedOwnedPaths)
            {
                var retainedForForeignContent = targetChangedPaths.Any(
                    changedPath => IsSameOrDescendant(
                        changedPath,
                        retainedOwnedPath));
                if (!retainedForForeignContent || !releaseProved)
                {
                    cleanupIncompletePaths.Add(retainedOwnedPath);
                }
            }
        }

        if (failure is OperationCanceledException
            && targetChangedPaths.Count == 0
            && cleanupIncompletePaths.Count == 0
            && supplementalFailures.Count == 0
            && releaseProved
            && retainedSetConclusive)
        {
            return CommandResult.Cancelled("Project creation was cancelled.");
        }

        return RenderFailure(
            requestedProjectRoot,
            operationProjectRoot,
            failure,
            targetChangedPaths,
            cleanupIncompletePaths,
            supplementalFailures,
            retainedSetConclusive);
    }

    private static CommandResult RenderFailure(
        string requestedProjectRoot,
        string? operationProjectRoot,
        Exception failure,
        IReadOnlyCollection<string> targetChangedPaths,
        IReadOnlyCollection<string> cleanupIncompletePaths,
        IReadOnlyList<Exception> supplementalFailures,
        bool retainedSetConclusive)
    {
        var error = new StringBuilder();
        if (failure is not NewProjectTargetChangedException)
        {
            error.AppendLine(FormatException(failure));
        }

        foreach (var supplementalFailure in supplementalFailures)
        {
            error.AppendLine(FormatException(supplementalFailure));
        }

        if (targetChangedPaths.Count > 0)
        {
            error.AppendLine(
                "newProjectTargetChanged: The project target changed during creation; "
                + (retainedSetConclusive
                    ? "foreign or changed content was preserved."
                    : "preservation of every foreign or changed path could not be proved."));
            foreach (var path in SortPaths(targetChangedPaths))
            {
                error.AppendLine($"  {path}");
            }
        }

        if (cleanupIncompletePaths.Count > 0 || !retainedSetConclusive)
        {
            error.AppendLine(
                "newProjectCleanupIncomplete: Project creation cleanup was incomplete.");
            error.AppendLine(
                $"Target: {operationProjectRoot ?? Path.GetFullPath(requestedProjectRoot)}");
            foreach (var path in SortPaths(cleanupIncompletePaths))
            {
                error.AppendLine($"  {path}");
            }

            if (!retainedSetConclusive)
            {
                error.AppendLine(
                    "Additional retained paths could not be determined conclusively. "
                    + "Inspect the listed paths and the entire project target before retrying.");
            }

            error.AppendLine(
                "Inspect the retained paths before retrying. Move or remove only "
                + "content you have independently verified is safe; vba-dev will "
                + "not change it automatically.");
        }

        return new CommandResult(
            1,
            string.Empty,
            error.ToString());
    }

    private static string FormatException(Exception exception)
        => exception switch
        {
            OperationCanceledException => "Project creation was cancelled.",
            ProjectManifestMutationException mutation =>
                $"{mutation.Code}: {mutation.Message}",
            _ => exception.Message
        };

    private static IEnumerable<Exception> EnumerateExceptionTree(
        Exception exception)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        return Enumerate(exception, visited);

        static IEnumerable<Exception> Enumerate(
            Exception current,
            HashSet<Exception> visited)
        {
            if (!visited.Add(current))
            {
                yield break;
            }

            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    foreach (var descendant in Enumerate(inner, visited))
                    {
                        yield return descendant;
                    }
                }

                yield break;
            }

            if (current.InnerException is not null)
            {
                foreach (var descendant in Enumerate(
                    current.InnerException,
                    visited))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static PathEntryObservation ObservePathEntry(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return PathEntryObservation.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return PathEntryObservation.Missing;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return PathEntryObservation.Inconclusive;
        }
    }

    private static bool IsFileSystemFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            or System.Security.SecurityException;

    private static bool IsSameOrDescendant(string candidate, string directory)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directory));
        if (fullCandidate.Equals(fullDirectory, PathComparison))
        {
            return true;
        }

        var prefix = fullDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, PathComparison);
    }

    private static IReadOnlyList<string> SortPaths(IEnumerable<string> paths)
        => paths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static NewProjectPathPlan ResolvePathPlan(
        NewProjectCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartDirectory);

        string? projectName = null;
        if (request.HasProjectName)
        {
            projectName = request.ProjectName ?? string.Empty;
            ValidateProjectName(projectName);
        }

        string projectRoot;
        if (request.HasOutputDirectory)
        {
            var outputDirectory = request.OutputDirectory ?? string.Empty;
            if (outputDirectory.Length == 0)
            {
                throw new ProjectManifestException(
                    "projectOutputEmpty: Project output path cannot be empty.");
            }

            if (!IsSupportedOutputPathSyntax(outputDirectory))
            {
                throw new ProjectManifestException(
                    "projectOutputNotWindowsFilesystemPath: Project output must be an invocation-relative, drive-qualified, or UNC Windows filesystem path.");
            }

            projectRoot = Path.GetFullPath(
                outputDirectory,
                request.StartDirectory);
        }
        else if (projectName is not null)
        {
            projectRoot = Path.GetFullPath(Path.Combine(
                request.StartDirectory,
                projectName));
        }
        else
        {
            projectRoot = Path.GetFullPath(request.StartDirectory);
        }

        projectName ??= Path.GetFileName(
            Path.TrimEndingDirectorySeparator(projectRoot));
        ValidateProjectName(projectName);
        var documentName = request.DocumentName ?? projectName;
        if (!documentName.Equals(projectName, StringComparison.Ordinal))
        {
            ValidateProjectName(documentName);
        }

        ValidateMaterializationPaths(projectRoot, documentName);
        return new NewProjectPathPlan(
            projectRoot,
            projectName,
            documentName);
    }

    private static void ValidateMaterializationPaths(
        string projectRoot,
        string documentName)
    {
        ValidateExcelPath(Path.Combine(
            projectRoot,
            "src",
            documentName,
            $"{documentName}.xlsm"));
        ValidateExcelPath(Path.Combine(
            projectRoot,
            "bin",
            $"{documentName}.xlsm"));
        ValidateExcelPath(Path.Combine(
            projectRoot,
            "publish",
            $"{documentName}.xlsm"));
    }

    private static void ValidateProjectName(string projectName)
    {
        var validation = ProjectNameLexicalContract.Validate(projectName);
        if (!validation.IsValid)
        {
            throw new ProjectManifestException(
                $"{validation.Reason}: {ProjectNameValidationMessage(validation.Reason!)}");
        }
    }

    private static string ProjectNameValidationMessage(string reason)
        => reason switch
        {
            ProjectCreationPathValidationReasons.ProjectNameEmpty =>
                "Enter a project name.",
            ProjectCreationPathValidationReasons.ProjectNameIllFormedUnicode =>
                "Project name contains an invalid Unicode sequence.",
            ProjectCreationPathValidationReasons.ProjectNameDotSegment =>
                "Project name cannot be \".\" or \"..\".",
            ProjectCreationPathValidationReasons.ProjectNameContainsPathSeparator =>
                "Project name cannot contain \"/\" or \"\\\".",
            ProjectCreationPathValidationReasons.ProjectNameContainsWindowsInvalidCharacter =>
                "Project name contains a character that Windows does not allow in a file or folder name.",
            ProjectCreationPathValidationReasons.ProjectNameContainsUnicodeControlCharacter =>
                "Project name cannot contain control characters.",
            ProjectCreationPathValidationReasons.ProjectNameHasLeadingOrTrailingWhitespace =>
                "Project name cannot start or end with whitespace.",
            ProjectCreationPathValidationReasons.ProjectNameEndsWithDot =>
                "Project name cannot end with a dot.",
            ProjectCreationPathValidationReasons.ProjectNameUsesReservedDeviceName =>
                "Project name cannot use a reserved Windows device name, even with an extension.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };

    private static void ValidateExcelPath(string path)
    {
        var validation = ExcelWorkbookPathContract.Validate(path);
        if (validation.IsValid)
        {
            return;
        }

        var message = validation.Reason switch
        {
            ProjectCreationPathValidationReasons.ExcelPathContainsUnsupportedCharacter =>
                $"Excel workbook path contains \"[\" or \"]\", which Excel does not reliably support: \"{path}\".",
            ProjectCreationPathValidationReasons.ExcelPathTooLong =>
                $"Excel workbook path exceeds the 218-character limit ({path.Length} UTF-16 code units): \"{path}\".",
            _ => throw new ArgumentOutOfRangeException(
                nameof(validation),
                validation.Reason,
                null)
        };
        throw new ProjectManifestException($"{validation.Reason}: {message}");
    }

    private static bool IsSupportedOutputPathSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || path.StartsWith("\\??\\", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/')
        {
            return true;
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var components = path[2..].Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries);
            return components.Length >= 2
                && components[0] is not "." and not "?"
                && !components[0].Contains(':', StringComparison.Ordinal)
                && !components[1].Contains(':', StringComparison.Ordinal);
        }

        if (path[0] is '\\' or '/'
            || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static CommandResult RenderSuccess(
        string format,
        string projectRoot,
        string manifestPath,
        string projectName,
        string documentName,
        ProjectManifest manifest,
        IReadOnlyList<NewProjectWarning> warnings)
    {
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var receipt = new NewProjectReceipt(
                "1.0",
                "project",
                projectRoot,
                documentName,
                "new",
                "excel",
                Complete: true,
                warnings,
                manifestPath,
                manifest);
            return CommandResult.Success(
                JsonSerializer.Serialize(receipt, ReceiptJsonOptions)
                + Environment.NewLine);
        }

        var document = manifest.Documents[documentName];
        var output = new StringBuilder()
            .AppendLine($"Created Excel VBA project \"{projectName}\".")
            .AppendLine($"Project: {projectRoot}")
            .AppendLine($"Manifest: {manifestPath}")
            .AppendLine($"Document: {documentName}")
            .AppendLine($"Source set: {document.SourcePath}")
            .AppendLine($"Source template: {document.TemplatePath}")
            .AppendLine($"Build target: {document.BinPath}")
            .AppendLine($"Publish target: {document.PublishPath}");
        AppendCommonModules(output, document.CommonModules);
        AppendReferences(output, document.References);
        AppendSummary(output, document.CommonModules, document.References);
        var standardError = string.Concat(warnings.Select(warning =>
            $"[WARN] {warning.Code}: {warning.Message}{Environment.NewLine}"));
        return new CommandResult(0, output.ToString(), standardError);
    }

    private static void AppendCommonModules(
        StringBuilder output,
        IReadOnlyList<InstalledCommonModule> commonModules)
    {
        output.AppendLine("CommonModules:");
        if (commonModules.Count == 0)
        {
            output.AppendLine("  (none)");
            return;
        }

        foreach (var module in commonModules)
        {
            output.AppendLine(
                $"  - {(module.Requested ? "requested" : "dependency")}: "
                + $"{module.Name} ({module.ModuleFile})");
        }
    }

    private static void AppendReferences(
        StringBuilder output,
        IReadOnlyList<VbaProjectReference> references)
    {
        output.AppendLine("References:");
        if (references.Count == 0)
        {
            output.AppendLine("  (none)");
            return;
        }

        foreach (var reference in references)
        {
            output.AppendLine(
                $"  - {(reference.Requested ? "requested" : "CommonModules")}: "
                + reference.Name);
        }
    }

    private static void AppendSummary(
        StringBuilder output,
        IReadOnlyList<InstalledCommonModule> commonModules,
        IReadOnlyList<VbaProjectReference> references)
    {
        var requestedModules = commonModules.Count(module => module.Requested);
        var dependencyModules = commonModules.Count - requestedModules;
        var requestedReferences = references.Count(reference => reference.Requested);
        var commonModulesReferences = references.Count - requestedReferences;
        output.AppendLine("Summary:")
            .AppendLine(
                $"  CommonModules: {FormatCount(commonModules.Count, "CommonModule", "CommonModules")} "
                + $"({requestedModules} requested, "
                + $"{FormatCount(dependencyModules, "dependency", "dependencies")})")
            .AppendLine(
                $"  References: {FormatCount(references.Count, "reference", "references")} "
                + $"({requestedReferences} requested, "
                + $"{commonModulesReferences} from CommonModules)");
    }

    private static string FormatCount(
        int count,
        string singular,
        string plural)
        => $"{count} {(count == 1 ? singular : plural)}";

    private sealed record NewProjectWarning(string Code, string Message);

    private enum PathEntryObservation
    {
        Missing,
        Present,
        Inconclusive
    }

    private sealed record NewProjectPathPlan(
        string RequestedProjectRoot,
        string ProjectName,
        string DocumentName);

    private sealed record NewProjectCopyArtifact(
        string TargetPath,
        ReadOnlyMemory<byte> Contents);

    private sealed record NewProjectCommonModulesPlan(
        IReadOnlyList<NewProjectCopyArtifact> Artifacts,
        IReadOnlyList<InstalledCommonModule> InstalledModules,
        IReadOnlyList<string> RequiredReferences);

    private sealed record NewProjectReceipt(
        string SchemaVersion,
        string Scope,
        string Project,
        string Document,
        string Operation,
        string Template,
        bool Complete,
        IReadOnlyList<NewProjectWarning> Warnings,
        string ManifestPath,
        ProjectManifest Manifest);

    private static string? DiscoverCommonModulesRepository(string projectRoot)
    {
        try
        {
            var parent = Directory.GetParent(projectRoot);
            if (parent is null)
            {
                return null;
            }

            var matches = parent.EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false
                    })
                .Where(entry => entry.Name.Equals(
                    "common_modules_repo",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
            {
                return null;
            }

            if (matches.Length != 1
                || !matches[0].Name.Equals(
                    "common_modules_repo",
                    StringComparison.Ordinal)
                || matches[0] is not DirectoryInfo repository
                || repository.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CommonModulesManifestException(
                    "The canonical CommonModules repository sibling could not be "
                    + $"identified safely below: {parent.FullName}");
            }

            return repository.FullName;
        }
        catch (CommonModulesManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            throw new CommonModulesManifestException(
                $"The canonical CommonModules repository sibling could not be observed safely for: {projectRoot}");
        }
    }

    private FileSystemPathIdentity EstablishDurableCommonModulesRepositoryRoute(
        string requestedProjectRoot,
        string discoveredRepositoryPath)
    {
        var durablePath = GetDurableCommonModulesRepositoryPath(
            requestedProjectRoot);
        try
        {
            var discoveredIdentity = pathIdentityResolver.Resolve(
                discoveredRepositoryPath);
            var durableIdentity = pathIdentityResolver.Resolve(durablePath);
            if (!HasContinuousExistingDirectoryIdentity(
                    discoveredIdentity,
                    durableIdentity)
                || !Directory.Exists(durableIdentity.OperationPath))
            {
                throw new CommonModulesManifestException(
                    "The requested project route cannot persist a durable CommonModules "
                    + $"repository route to the canonical sibling: {durablePath}");
            }

            return durableIdentity;
        }
        catch (CommonModulesManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            throw new CommonModulesManifestException(
                "The requested project route cannot establish a durable CommonModules "
                + $"repository route to the canonical sibling: {durablePath}");
        }
    }

    private void ProveDurableCommonModulesRepositoryRoute(
        string requestedProjectRoot,
        FileSystemPathIdentity expectedIdentity)
    {
        var durablePath = GetDurableCommonModulesRepositoryPath(
            requestedProjectRoot);
        try
        {
            var currentIdentity = pathIdentityResolver.Resolve(durablePath);
            if (!HasContinuousExistingDirectoryIdentity(
                    expectedIdentity,
                    currentIdentity)
                || !Directory.Exists(currentIdentity.OperationPath))
            {
                throw new CommonModulesManifestException(
                    "The durable CommonModules repository route changed before "
                    + $"the initial manifest commit: {durablePath}");
            }
        }
        catch (CommonModulesManifestException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or InvalidOperationException
            or System.Security.SecurityException)
        {
            throw new CommonModulesManifestException(
                "The durable CommonModules repository route could not be proved "
                + $"before the initial manifest commit: {durablePath}");
        }
    }

    private static string GetDurableCommonModulesRepositoryPath(
        string requestedProjectRoot)
        => Path.GetFullPath(Path.Combine(
            requestedProjectRoot,
            "..",
            "common_modules_repo"));

    private static bool HasContinuousExistingDirectoryIdentity(
        FileSystemPathIdentity expected,
        FileSystemPathIdentity current)
    {
        if (!Path.TrimEndingDirectorySeparator(expected.CanonicalPath).Equals(
                Path.TrimEndingDirectorySeparator(current.CanonicalPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expected.ObjectIdentity is not null
            || current.ObjectIdentity is not null)
        {
            return expected.ObjectIdentity is not null
                && current.ObjectIdentity is not null
                && expected.ObjectIdentity == current.ObjectIdentity;
        }

        return !OperatingSystem.IsWindows();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class NewProjectTargetChangedException : Exception
    {
        public NewProjectTargetChangedException(
            IReadOnlyList<string> paths,
            Exception? innerException = null)
            : base(
                "The project target changed during creation.",
                innerException)
        {
            Paths = SortPaths(paths);
        }

        public IReadOnlyList<string> Paths { get; }
    }
}
