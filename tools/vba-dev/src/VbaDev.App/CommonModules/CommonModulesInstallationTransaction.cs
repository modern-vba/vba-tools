using System.Text;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Carries a trusted CommonModules text result and its ordered non-fatal warnings.
/// </summary>
public sealed record CommonModulesTransactionCompletion(
    string Output,
    IReadOnlyList<ProjectManifestMutationWarning> Warnings);

/// <summary>
/// Applies CommonModules source file copies and manifest updates as a recoverable project transaction.
/// </summary>
public sealed class CommonModulesInstallationTransaction
{
    private const string CommonModulesDirectoryName = "common-modules";
    private const string RequiredReferencePlanChangedCode = "commonModulesRequiredReferencePlanChanged";
    private const string RequiredReferencePlanChangedMessage =
        "CommonModules required-reference planning changed while references were being resolved. "
        + "No source or manifest changes were made. Rerun the command.";

    private readonly ProjectManifestEditor manifestEditor;
    private readonly VbaProjectReferencePlanner? referencePlanner;
    private readonly IProjectManifestMutationCoordinator? manifestMutationCoordinator;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;
    private readonly CommonModulesPackageSnapshotFactory packageSnapshotFactory;
    private readonly CommonModulesSourceMutationWriter sourceMutationWriter;

    /// <summary>
    /// Creates a transaction coordinator for CommonModules installation operations.
    /// </summary>
    /// <param name="manifestReader">The manifest reader for the configured CommonModulesRepository.</param>
    /// <param name="manifestStore">The project manifest store used to persist installed entries.</param>
    public CommonModulesInstallationTransaction(
        CommonModulesManifestReader manifestReader,
        IProjectManifestStore manifestStore,
        IProjectManifestAtomicWriter atomicWriter)
        : this(
            manifestReader,
            new ProjectManifestEditor(manifestStore, atomicWriter),
            referencePlanner: null,
            manifestMutationCoordinator: null,
            pathIdentityResolver: null)
    {
    }

    /// <summary>
    /// Creates a transaction coordinator for CommonModules installation operations.
    /// </summary>
    /// <param name="manifestReader">The manifest reader for the configured CommonModulesRepository.</param>
    /// <param name="manifestEditor">The manifest editor used to clone and persist installed entries.</param>
    public CommonModulesInstallationTransaction(
        CommonModulesManifestReader manifestReader,
        ProjectManifestEditor manifestEditor,
        VbaProjectReferencePlanner? referencePlanner = null,
        IProjectManifestMutationCoordinator? manifestMutationCoordinator = null,
        IFileSystemPathIdentityResolver? pathIdentityResolver = null)
        : this(
            manifestReader,
            manifestEditor,
            referencePlanner,
            manifestMutationCoordinator,
            pathIdentityResolver,
            packageSnapshotFactory: null,
            sourceMutationWriter: null)
    {
    }

    internal CommonModulesInstallationTransaction(
        CommonModulesManifestReader manifestReader,
        ProjectManifestEditor manifestEditor,
        VbaProjectReferencePlanner? referencePlanner,
        IProjectManifestMutationCoordinator? manifestMutationCoordinator,
        IFileSystemPathIdentityResolver? pathIdentityResolver,
        CommonModulesPackageSnapshotFactory? packageSnapshotFactory,
        CommonModulesSourceMutationWriter? sourceMutationWriter)
    {
        var packageReader = new CommonModulesPackageReader(manifestReader);
        this.manifestEditor = manifestEditor;
        this.referencePlanner = referencePlanner;
        this.manifestMutationCoordinator = manifestMutationCoordinator;
        this.pathIdentityResolver = pathIdentityResolver ?? new FileSystemPathIdentityResolver();
        this.packageSnapshotFactory = packageSnapshotFactory
            ?? new CommonModulesPackageSnapshotFactory(packageReader);
        this.sourceMutationWriter = sourceMutationWriter
            ?? new CommonModulesSourceMutationWriter();
    }

    /// <summary>
    /// Adds requested CommonModules entries to one document source set and records them in the manifest.
    /// </summary>
    /// <param name="context">The resolved project and document context to update.</param>
    /// <param name="requestedModules">The requested module names or file names.</param>
    /// <param name="force">Whether existing target source files may be overwritten.</param>
    /// <returns>A human-readable summary of copied files.</returns>
    public string Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules, bool force)
        => AddAsync(context, requestedModules, force, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Output;

    /// <summary>
    /// Adds requested CommonModules after resolving every missing required VBA reference.
    /// </summary>
    public async Task<CommonModulesTransactionCompletion> AddAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> requestedModules,
        bool force,
        CancellationToken cancellationToken)
    {
        var normalizedRequestedModules = requestedModules
            .Select(VbaIdentifier.TrimWhitespace)
            .Where(module => module.Length > 0)
            .ToArray();
        if (normalizedRequestedModules.Length == 0)
        {
            throw new CommonModulesManifestException("common-module add requires at least one CommonModules module name.");
        }

        var invocationDocument = ProjectManifestEditor.GetDocument(
            context.Manifest,
            context.DocumentName);
        var repositoryBackedRequests = GetRepositoryBackedAddRequests(
            invocationDocument,
            normalizedRequestedModules);
        IReadOnlyList<string> requiredReferences = [];
        if (repositoryBackedRequests.Count > 0)
        {
            var repositoryPath = GetRepositoryPath(context);
            var selectionPlan = CaptureProvisionalSelectionPlan(
                repositoryPath,
                repositoryBackedRequests,
                cancellationToken);
            ValidateSelectedEntryIdentities(selectionPlan.Entries);
            requiredReferences = selectionPlan.RequiredReferences;
        }

        var referenceEvidence = await ResolveRequiredReferenceEvidenceAsync(
                context.DocumentName,
                invocationDocument,
                requiredReferences,
                context.TemplateDocumentPath,
                cancellationToken)
            .ConfigureAwait(false);

        CommonModulesPackageSnapshot? transactionSnapshot = null;
        try
        {
            if (manifestMutationCoordinator is null)
            {
                var plan = RebaseAdd(
                    new ProjectManifestMutationSnapshot(
                        context.ProjectRoot,
                        context.ManifestPath,
                        context.Manifest),
                    context.DocumentName,
                    normalizedRequestedModules,
                    force,
                    referenceEvidence,
                    cancellationToken);
                transactionSnapshot = plan.Result.PackageSnapshot;
                if (plan.Manifest is not null)
                {
                    SaveManifest(context.ProjectRoot, plan.Manifest);
                }

                var cleanup = CleanupSnapshot(transactionSnapshot);
                return CreateCompletion(plan.Result, [], cleanup);
            }

            var outcome = await manifestMutationCoordinator.ExecuteAsync(
                    context.ProjectRoot,
                    ProjectManifestMutationCommand.CommonModuleAdd,
                    snapshot =>
                    {
                        var plan = RebaseAdd(
                            snapshot,
                            context.DocumentName,
                            normalizedRequestedModules,
                            force,
                            referenceEvidence,
                            cancellationToken);
                        transactionSnapshot = plan.Result.PackageSnapshot;
                        return plan;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var outcomeCleanup = CleanupSnapshot(transactionSnapshot);
            return CreateCompletion(outcome.Result, outcome.Warnings, outcomeCleanup);
        }
        catch (Exception ex)
        {
            var cleanup = CleanupSnapshot(transactionSnapshot);
            var contextualFailure = AddSnapshotFailureContext(ex, cleanup);
            if (ReferenceEquals(contextualFailure, ex))
            {
                throw;
            }

            throw contextualFailure;
        }
    }

    private CommonModulesSelectionPlan CaptureProvisionalSelectionPlan(
        string repositoryPath,
        IReadOnlyList<string> requestedModules,
        CancellationToken cancellationToken)
    {
        CommonModulesPackageSnapshot? snapshot = null;
        try
        {
            snapshot = packageSnapshotFactory.Capture(repositoryPath, cancellationToken);
            var plan = snapshot.ResolveRequestedPlan(requestedModules);
            var cleanup = snapshot.Cleanup();
            if (!cleanup.Deleted)
            {
                throw SnapshotCleanupFailure(cleanup.RetainedPath!);
            }

            return plan;
        }
        catch (Exception ex)
        {
            var cleanup = CleanupSnapshot(snapshot);
            throw AddSnapshotFailureContext(ex, cleanup);
        }
    }

    private IReadOnlyList<CommonModuleManifestEntry> CaptureProvisionalEntries(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        CommonModulesPackageSnapshot? snapshot = null;
        try
        {
            snapshot = packageSnapshotFactory.Capture(repositoryPath, cancellationToken);
            var entries = snapshot.Entries;
            var cleanup = snapshot.Cleanup();
            if (!cleanup.Deleted)
            {
                throw SnapshotCleanupFailure(cleanup.RetainedPath!);
            }

            return entries;
        }
        catch (Exception ex)
        {
            var cleanup = CleanupSnapshot(snapshot);
            throw AddSnapshotFailureContext(ex, cleanup);
        }
    }

    private static IReadOnlyList<string> GetRepositoryBackedAddRequests(
        ProjectDocument document,
        IReadOnlyList<string> normalizedRequestedModules)
    {
        var installedNames = document.CommonModules
            .Select(module => module.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalizedRequestedModules
            .Where(module => !installedNames.Contains(GetCommonModuleName(module)))
            .ToArray();
    }

    private async Task<CommonModulesReferenceResolutionEvidence> ResolveRequiredReferenceEvidenceAsync(
        string documentName,
        ProjectDocument invocationDocument,
        IReadOnlyList<string> requiredReferences,
        string templateDocumentPath,
        CancellationToken cancellationToken)
    {
        var selectedNames = invocationDocument.References
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNames = requiredReferences
            .Where(reference => !selectedNames.Contains(reference))
            .ToArray();
        if (missingNames.Length == 0)
        {
            return new CommonModulesReferenceResolutionEvidence(
                documentName,
                TemplateIdentity: null,
                missingNames,
                new Dictionary<string, ResolvedVbaProjectReference>(StringComparer.OrdinalIgnoreCase));
        }

        if (referencePlanner is null)
        {
            throw new CommonModulesManifestException(
                "CommonModules required-reference resolution is not configured.");
        }

        var templateIdentity = pathIdentityResolver.Resolve(templateDocumentPath);
        try
        {
            var resolutionBatch = await referencePlanner.ResolveReferencesAsync(
                    templateDocumentPath,
                    missingNames,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var resolvedReferences = referencePlanner.SelectManifestInputReferences(
                resolutionBatch,
                missingNames);
            return new CommonModulesReferenceResolutionEvidence(
                documentName,
                templateIdentity,
                missingNames,
                missingNames
                    .Zip(resolvedReferences)
                    .ToDictionary(
                        pair => pair.First,
                        pair => pair.Second,
                        StringComparer.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException ex)
        {
            throw new CommonModulesManifestException(ex.Message);
        }
    }

    private ProjectManifestMutationPlan<CommonModulesRebaseResult> RebaseAdd(
        ProjectManifestMutationSnapshot snapshot,
        string documentName,
        IReadOnlyList<string> normalizedRequestedModules,
        bool force,
        CommonModulesReferenceResolutionEvidence referenceEvidence,
        CancellationToken cancellationToken)
    {
        if (!snapshot.Manifest.Documents.Keys.Any(name =>
                name.Equals(documentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw RequiredReferencePlanChanged();
        }

        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var document = ProjectManifestEditor.GetDocument(plannedManifest, documentName);
        var installedByName = document.CommonModules.ToDictionary(
            module => module.Name,
            StringComparer.OrdinalIgnoreCase);
        var repositoryBackedRequests = GetRepositoryBackedAddRequests(
            document,
            normalizedRequestedModules);
        CommonModulesPackageSnapshot? packageSnapshot = null;
        CommonModulesPackageSnapshotCleanupResult? cleanup = null;
        try
        {
            CommonModulesSelectionPlan selectionPlan;
            if (repositoryBackedRequests.Count == 0)
            {
                selectionPlan = new CommonModulesSelectionPlan([], []);
            }
            else
            {
                var repositoryPath = GetRepositoryPath(
                    snapshot.ProjectRoot,
                    snapshot.Manifest.CommonModulesRepository);
                packageSnapshot = packageSnapshotFactory.Capture(
                    repositoryPath,
                    cancellationToken);
                selectionPlan = packageSnapshot.ResolveRequestedPlan(repositoryBackedRequests);
            }

            var orderedEntries = selectionPlan.Entries;
            ValidateSelectedEntryIdentities(orderedEntries);
            var requestedNames = normalizedRequestedModules
                .Select(GetCommonModuleName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var referencesChanged = AppendRequiredReferencesFromEvidence(
                snapshot.ProjectRoot,
                documentName,
                document,
                selectionPlan.RequiredReferences,
                referenceEvidence);
            ValidateInstalledSourceIdentities(orderedEntries, installedByName);
            var entriesToCopy = orderedEntries
                .Where(entry => !installedByName.ContainsKey(entry.Name))
                .ToArray();
            var documentSourceSetPath = ResolveManifestPath(
                snapshot.ProjectRoot,
                document.SourcePath);
            var copyPlan = packageSnapshot is null
                ? []
                : PlanCopyEntries(
                    packageSnapshot,
                    documentSourceSetPath,
                    entriesToCopy,
                    "Copied",
                    force,
                    documentName: null);
            var changed = ApplyAddEntries(
                    document,
                    orderedEntries,
                    requestedNames,
                    installedByName)
                || referencesChanged;
            ValidatePlannedManifest(plannedManifest);
            var sourceMutation = ExecuteCopyPlan(copyPlan, cancellationToken);

            var copied = BuildCopyOutput(copyPlan);
            var output = copied.Length == 0
                ? "No CommonModules changes." + Environment.NewLine
                : copied;
            var affectedNames = requestedNames
                .Concat(orderedEntries.Select(entry => entry.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new CommonModulesRebaseResult(
                output,
                packageSnapshot,
                document.CommonModules.Count(module =>
                    module.Orphaned && affectedNames.Contains(module.Name)),
                document.CommonModules.Any(module =>
                    module.Orphaned && affectedNames.Contains(module.Name)) ? 1 : 0);
            var recovery = CreateCommitFailureRecovery(
                snapshot.ProjectRoot,
                snapshot.ManifestPath,
                plannedManifest,
                changed || sourceMutation.SourceMutationCommitted,
                sourceMutation.SourceMutationCommitted,
                GetManualVerificationPaths(copyPlan));
            return changed
                ? ProjectManifestMutationPlan<CommonModulesRebaseResult>.Commit(
                    plannedManifest,
                    result,
                    sourceMutation.SourceMutationCommitted,
                    recovery)
                : ProjectManifestMutationPlan<CommonModulesRebaseResult>.NoOp(
                    result,
                    sourceMutation.SourceMutationCommitted,
                    recovery);
        }
        catch (CommonModulesSourceMutationException ex)
        {
            var sourceFailure = CreateSourceMutationFailure(
                snapshot.ProjectRoot,
                plannedManifest,
                ex);
            cleanup ??= CleanupSnapshot(packageSnapshot);
            throw AddSnapshotFailureContext(sourceFailure, cleanup);
        }
        catch (Exception ex)
        {
            cleanup ??= CleanupSnapshot(packageSnapshot);
            throw AddSnapshotFailureContext(ex, cleanup);
        }
    }

    private bool AppendRequiredReferencesFromEvidence(
        string projectRoot,
        string documentName,
        ProjectDocument document,
        IReadOnlyList<string> requiredReferences,
        CommonModulesReferenceResolutionEvidence? evidence)
    {
        var missingNames = requiredReferences
            .Where(requiredReference => !document.References.Any(reference =>
                reference.Name.Equals(requiredReference, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (evidence is null
            || !evidence.DocumentName.Equals(documentName, StringComparison.OrdinalIgnoreCase)
            || !missingNames.SequenceEqual(
                evidence.MissingNames,
                StringComparer.OrdinalIgnoreCase))
        {
            throw RequiredReferencePlanChanged();
        }

        if (missingNames.Length == 0)
        {
            return false;
        }

        if (evidence.TemplateIdentity is null
            || missingNames.Any(name => !evidence.ResolvedByRequiredName.ContainsKey(name)))
        {
            throw RequiredReferencePlanChanged();
        }

        FileSystemPathIdentity latestTemplateIdentity;
        try
        {
            var latestTemplatePath = ResolveManifestPath(projectRoot, document.TemplatePath);
            latestTemplateIdentity = pathIdentityResolver.Resolve(latestTemplatePath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            throw RequiredReferencePlanChanged();
        }

        if (!FileSystemPathIdentityRelations.Same(
                evidence.TemplateIdentity,
                latestTemplateIdentity))
        {
            throw RequiredReferencePlanChanged();
        }

        foreach (var missingName in missingNames)
        {
            var resolvedReference = evidence.ResolvedByRequiredName[missingName];
            document.References.Add(new VbaProjectReference(
                resolvedReference.Name,
                requested: false));
        }

        return true;
    }

    private static ProjectManifestMutationException RequiredReferencePlanChanged()
        => new(RequiredReferencePlanChangedCode, RequiredReferencePlanChangedMessage);

    /// <summary>
    /// Refreshes all installed CommonModules source files in a project from the configured repository.
    /// </summary>
    /// <param name="project">The resolved project whose installed CommonModules entries should be updated.</param>
    /// <returns>A human-readable summary of updated files.</returns>
    public string Update(ResolvedProject project)
        => UpdateAsync(project, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Output;

    /// <summary>
    /// Refreshes installed CommonModules after resolving all document requirements.
    /// </summary>
    public async Task<CommonModulesTransactionCompletion> UpdateAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
    {
        var referenceEvidenceByDocument = new Dictionary<string, CommonModulesReferenceResolutionEvidence>(
            StringComparer.OrdinalIgnoreCase);
        if (project.Manifest.Documents.Values.Any(document => document.CommonModules.Count > 0))
        {
            var repositoryPath = GetRepositoryPath(project);
            var entries = CaptureProvisionalEntries(repositoryPath, cancellationToken);
            foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(
                         item => item.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                var updatePlan = CreateUpdatePlan(entries, document);
                if (updatePlan is null)
                {
                    continue;
                }

                ValidateSelectedEntryIdentities(updatePlan.Entries);
                referenceEvidenceByDocument.Add(
                    documentName,
                    await ResolveRequiredReferenceEvidenceAsync(
                        documentName,
                        document,
                        updatePlan.RequiredReferences,
                        project.ResolvePath(document.TemplatePath),
                        cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        CommonModulesPackageSnapshot? transactionSnapshot = null;
        try
        {
            if (manifestMutationCoordinator is null)
            {
                var plan = RebaseUpdate(
                    new ProjectManifestMutationSnapshot(
                        project.ProjectRoot,
                        project.ManifestPath,
                        project.Manifest),
                    referenceEvidenceByDocument,
                    cancellationToken);
                transactionSnapshot = plan.Result.PackageSnapshot;
                if (plan.Manifest is not null)
                {
                    SaveManifest(project.ProjectRoot, plan.Manifest);
                }

                var cleanup = CleanupSnapshot(transactionSnapshot);
                return CreateCompletion(plan.Result, [], cleanup);
            }

            var outcome = await manifestMutationCoordinator.ExecuteAsync(
                    project.ProjectRoot,
                    ProjectManifestMutationCommand.CommonModuleUpdate,
                    snapshot =>
                    {
                        var plan = RebaseUpdate(
                            snapshot,
                            referenceEvidenceByDocument,
                            cancellationToken);
                        transactionSnapshot = plan.Result.PackageSnapshot;
                        return plan;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var outcomeCleanup = CleanupSnapshot(transactionSnapshot);
            return CreateCompletion(outcome.Result, outcome.Warnings, outcomeCleanup);
        }
        catch (Exception ex)
        {
            var cleanup = CleanupSnapshot(transactionSnapshot);
            var contextualFailure = AddSnapshotFailureContext(ex, cleanup);
            if (ReferenceEquals(contextualFailure, ex))
            {
                throw;
            }

            throw contextualFailure;
        }
    }

    private Func<Exception, Exception>? CreateCommitFailureRecovery(
        string projectRoot,
        string manifestPath,
        ProjectManifest recoveryManifest,
        bool recoveryRequired,
        bool sourceMutationCommitted,
        IReadOnlyList<string> sourceVerificationPaths)
        => !recoveryRequired
            ? null
            : failure => CreateManifestRecoveryException(
                projectRoot,
                manifestPath,
                recoveryManifest,
                failure,
                sourceMutationCommitted,
                sourceVerificationPaths);

    private CommonModulesTransactionException CreateManifestRecoveryException(
        string projectRoot,
        string manifestPath,
        ProjectManifest recoveryManifest,
        Exception manifestSaveException,
        bool sourceMutationCommitted,
        IReadOnlyList<string> sourceVerificationPaths)
    {
        var recovery = manifestEditor.CreateRecoveryAfterFailedSave(
            projectRoot,
            recoveryManifest,
            manifestSaveException);
        if (!sourceMutationCommitted)
        {
            return new CommonModulesTransactionException(recovery);
        }

        var verificationPaths = sourceVerificationPaths
            .Append(Path.GetFullPath(manifestPath))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var renderedPaths = string.Join(
            ", ",
            verificationPaths.Select(path => $"\"{path}\""));
        return new CommonModulesTransactionException(
            $"{manifestSaveException.Message}{Environment.NewLine}"
            + "CommonModules source files may already reflect the complete copy plan, while the "
            + "project manifest was not committed. The current manifest was preserved. "
            + $"Manually verify: {renderedPaths}.{Environment.NewLine}"
            + $"Project manifest recovery: {recovery}{Environment.NewLine}"
            + "Recovery requires a manual merge and was not applied automatically.");
    }

    private CommonModulesTransactionException CreateSourceMutationFailure(
        string projectRoot,
        ProjectManifest recoveryManifest,
        CommonModulesSourceMutationException sourceFailure)
    {
        var message = sourceFailure.Message;
        if (sourceFailure.SourceMutationCommitted)
        {
            var recovery = manifestEditor.CreateRecoveryAfterFailedSave(
                projectRoot,
                recoveryManifest,
                sourceFailure);
            message += Environment.NewLine + "Project manifest recovery: " + recovery;
        }

        return new CommonModulesTransactionException(message);
    }

    private static CommonModulesPackageSnapshotCleanupResult? CleanupSnapshot(
        CommonModulesPackageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        try
        {
            return snapshot.Cleanup();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException)
        {
            return new CommonModulesPackageSnapshotCleanupResult(
                Deleted: false,
                RetainedPath: Path.GetFullPath(snapshot.StagingPath));
        }
    }

    private static Exception AddSnapshotFailureContext(
        Exception failure,
        CommonModulesPackageSnapshotCleanupResult? cleanup)
        => cleanup is { Deleted: false, RetainedPath: not null }
            ? new CommonModulesTransactionException(
                AddRetainedSnapshotContext(failure.Message, cleanup))
            : failure;

    private static string AddRetainedSnapshotContext(
        string message,
        CommonModulesPackageSnapshotCleanupResult? cleanup)
        => cleanup is { Deleted: false, RetainedPath: not null }
            ? message + Environment.NewLine
                + $"CommonModules snapshot workspace was retained: \"{Path.GetFullPath(cleanup.RetainedPath)}\"."
            : message;

    private static CommonModulesTransactionException SnapshotCleanupFailure(string retainedPath)
        => new(
            $"The CommonModules package snapshot workspace could not be removed: "
            + $"\"{Path.GetFullPath(retainedPath)}\".");

    private static CommonModulesTransactionCompletion CreateCompletion(
        CommonModulesRebaseResult result,
        IReadOnlyList<ProjectManifestMutationWarning> coordinatorWarnings,
        CommonModulesPackageSnapshotCleanupResult? snapshotCleanup)
    {
        var warnings = new List<ProjectManifestMutationWarning>();
        if (result.OrphanedModuleCount > 0)
        {
            var moduleWord = result.OrphanedModuleCount == 1 ? "CommonModule" : "CommonModules";
            var documentWord = result.OrphanedDocumentCount == 1 ? "document" : "documents";
            warnings.Add(new ProjectManifestMutationWarning(
                "orphanedCommonModulesRetained",
                $"Retained {result.OrphanedModuleCount} orphaned {moduleWord} across "
                + $"{result.OrphanedDocumentCount} {documentWord}; no source was removed."));
        }

        warnings.AddRange(coordinatorWarnings);
        if (snapshotCleanup is { Deleted: false, RetainedPath: not null })
        {
            warnings.Add(new ProjectManifestMutationWarning(
                "commonModulesSnapshotCleanupFailed",
                "The CommonModules mutation completed, but its non-authoritative snapshot workspace "
                + $"could not be removed: \"{Path.GetFullPath(snapshotCleanup.RetainedPath)}\"."));
        }

        var ordered = warnings
            .GroupBy(warning => warning.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(warning => WarningRank(warning.Code))
            .ThenBy(warning => warning.Code, StringComparer.Ordinal)
            .ToArray();
        return new CommonModulesTransactionCompletion(result.Output, ordered);
    }

    private static int WarningRank(string code)
        => code switch
        {
            "orphanedCommonModulesRetained" => 0,
            "cancellationDeferred" => 1,
            "commonModulesSnapshotCleanupFailed" => 2,
            "leaseMarkerCleanupFailed" => 3,
            _ => 4
        };

    private ProjectManifestMutationPlan<CommonModulesRebaseResult> RebaseUpdate(
        ProjectManifestMutationSnapshot snapshot,
        IReadOnlyDictionary<string, CommonModulesReferenceResolutionEvidence> referenceEvidenceByDocument,
        CancellationToken cancellationToken)
    {
        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var targetDocuments = plannedManifest.Documents
            .Where(item => item.Value.CommonModules.Count > 0)
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (targetDocuments.Length != referenceEvidenceByDocument.Count
            || targetDocuments.Any(item => !referenceEvidenceByDocument.ContainsKey(item.Key)))
        {
            throw RequiredReferencePlanChanged();
        }

        CommonModulesPackageSnapshot? packageSnapshot = null;
        CommonModulesPackageSnapshotCleanupResult? cleanup = null;
        try
        {
            IReadOnlyList<CommonModuleManifestEntry> entries = [];
            if (targetDocuments.Length > 0)
            {
                var repositoryPath = GetRepositoryPath(
                    snapshot.ProjectRoot,
                    snapshot.Manifest.CommonModulesRepository);
                packageSnapshot = packageSnapshotFactory.Capture(
                    repositoryPath,
                    cancellationToken);
                entries = packageSnapshot.Entries;
            }

            var copyPlans = new List<CommonModuleCopyPlan>();
            var manifestChanged = false;
            foreach (var (documentName, document) in targetDocuments)
            {
                var updatePlan = CreateUpdatePlan(entries, document)!;
                ValidateSelectedEntryIdentities(updatePlan.Entries);
                manifestChanged |= AppendRequiredReferencesFromEvidence(
                    snapshot.ProjectRoot,
                    documentName,
                    document,
                    updatePlan.RequiredReferences,
                    referenceEvidenceByDocument[documentName]);
                var installedByName = document.CommonModules.ToDictionary(
                    module => module.Name,
                    StringComparer.OrdinalIgnoreCase);
                ValidateInstalledSourceIdentities(updatePlan.Entries, installedByName);
                var documentSourceSetPath = ResolveManifestPath(
                    snapshot.ProjectRoot,
                    document.SourcePath);
                copyPlans.AddRange(PlanCopyEntries(
                    packageSnapshot!,
                    documentSourceSetPath,
                    updatePlan.Entries,
                    "Updated",
                    overwrite: true,
                    documentName));
                manifestChanged |= ApplyUpdateEntries(
                    document,
                    updatePlan,
                    installedByName);
            }

            ValidatePlannedManifest(plannedManifest);
            var sourceMutation = ExecuteCopyPlan(copyPlans, cancellationToken);
            var output = BuildCopyOutput(copyPlans);
            var result = new CommonModulesRebaseResult(
                output.Length == 0
                    ? targetDocuments.Length == 0
                        ? "No installed CommonModules entries were found." + Environment.NewLine
                        : "No CommonModules changes." + Environment.NewLine
                    : output,
                packageSnapshot,
                targetDocuments.Sum(item => item.Value.CommonModules.Count(module => module.Orphaned)),
                targetDocuments.Count(item => item.Value.CommonModules.Any(module => module.Orphaned)));
            var recovery = CreateCommitFailureRecovery(
                snapshot.ProjectRoot,
                snapshot.ManifestPath,
                plannedManifest,
                manifestChanged || sourceMutation.SourceMutationCommitted,
                sourceMutation.SourceMutationCommitted,
                GetManualVerificationPaths(copyPlans));
            return manifestChanged
                ? ProjectManifestMutationPlan<CommonModulesRebaseResult>.Commit(
                    plannedManifest,
                    result,
                    sourceMutation.SourceMutationCommitted,
                    recovery)
                : ProjectManifestMutationPlan<CommonModulesRebaseResult>.NoOp(
                    result,
                    sourceMutation.SourceMutationCommitted,
                    recovery);
        }
        catch (CommonModulesSourceMutationException ex)
        {
            var sourceFailure = CreateSourceMutationFailure(
                snapshot.ProjectRoot,
                plannedManifest,
                ex);
            cleanup ??= CleanupSnapshot(packageSnapshot);
            throw AddSnapshotFailureContext(sourceFailure, cleanup);
        }
        catch (Exception ex)
        {
            cleanup ??= CleanupSnapshot(packageSnapshot);
            throw AddSnapshotFailureContext(ex, cleanup);
        }
    }

    private static CommonModulesUpdatePlan? CreateUpdatePlan(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        ProjectDocument document)
    {
        var installedModuleNames = document.CommonModules
            .Select(module => module.Name)
            .ToArray();
        if (installedModuleNames.Length == 0)
        {
            return null;
        }

        var entriesByName = entries.ToDictionary(
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase);
        var requestedModuleNames = document.CommonModules
            .Where(module => module.Requested)
            .Select(module => module.Name)
            .ToArray();
        var availableRequestedModuleNames = requestedModuleNames
            .Where(entriesByName.ContainsKey)
            .ToArray();
        var dependencyClosureEntries = availableRequestedModuleNames.Length == 0
            ? []
            : CommonModulesDependencyResolver.ResolveRequestedEntries(
                entries,
                availableRequestedModuleNames);
        var installedEntries = installedModuleNames
            .Where(entriesByName.ContainsKey)
            .Select(module => entriesByName[module])
            .ToArray();
        var orderedEntries = CommonModulesDependencyResolver.MergeEntries(
            dependencyClosureEntries,
            installedEntries);
        var selectionPlan = CommonModulesDependencyResolver.CreateSelectionPlan(orderedEntries);
        return new CommonModulesUpdatePlan(
            selectionPlan.Entries,
            selectionPlan.RequiredReferences,
            installedModuleNames
                .Where(module => !entriesByName.ContainsKey(module))
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CommonModuleCopyPlan> PlanCopyEntries(
        CommonModulesPackageSnapshot packageSnapshot,
        string documentSourceSetPath,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string verb,
        bool overwrite,
        string? documentName = null)
    {
        var plans = new List<CommonModuleCopyPlan>();
        var plannedTargets = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var observedTargetPath = ResolveTargetPath(
                documentSourceSetPath,
                entry.InstalledModuleFile,
                overwrite);
            var targetPath = Path.Combine(
                Path.GetDirectoryName(observedTargetPath)!,
                entry.InstalledModuleFile);
            var canonicalTargetPath = Path.GetFullPath(targetPath);
            if (plannedTargets.TryGetValue(canonicalTargetPath, out var conflictingEntry))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules entries '{conflictingEntry.ModuleFile}' and '{entry.ModuleFile}' resolve to the same target source file: {targetPath}");
            }

            plannedTargets.Add(canonicalTargetPath, entry);
            var mutations = new List<CommonModulesSourceFileMutation>();
            AddWriteMutation(
                mutations,
                observedTargetPath,
                targetPath,
                packageSnapshot.ReadFileBytes(entry.ModuleFile));
            if (DocumentSourceSetLayout.IsFormFile(entry.ModuleFile))
            {
                PlanFormSidecarMutations(
                    packageSnapshot,
                    documentSourceSetPath,
                    entry,
                    observedTargetPath,
                    targetPath,
                    mutations);
            }

            if (mutations.Count == 0)
            {
                continue;
            }

            var relativeTargetPath = NormalizeDisplayPath(Path.GetRelativePath(documentSourceSetPath, targetPath));
            var outputPath = documentName is null ? relativeTargetPath : $"{documentName}/{relativeTargetPath}";
            plans.Add(new CommonModuleCopyPlan(
                Mutations: mutations,
                Verb: verb,
                OutputPath: outputPath));
        }

        return plans;
    }

    private static void PlanFormSidecarMutations(
        CommonModulesPackageSnapshot packageSnapshot,
        string documentSourceSetPath,
        CommonModuleManifestEntry entry,
        string observedFormPath,
        string canonicalFormPath,
        ICollection<CommonModulesSourceFileMutation> mutations)
    {
        var existingSidecars = DocumentSourceSetLayout.FindFormSidecars(
            documentSourceSetPath,
            entry.ModuleFile);
        var observedTargetSidecar = DocumentSourceSetLayout.ResolveExistingSidecarPath(
            observedFormPath);
        foreach (var existingSidecar in existingSidecars)
        {
            if (observedTargetSidecar is not null
                && Path.GetFullPath(existingSidecar).Equals(
                    Path.GetFullPath(observedTargetSidecar),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddDeleteMutation(mutations, existingSidecar);
        }

        var packageSidecarName = Path.ChangeExtension(entry.ModuleFile, ".frx");
        if (packageSnapshot.TryReadFileBytes(packageSidecarName, out var desiredSidecarBytes))
        {
            var canonicalTargetSidecar = Path.ChangeExtension(canonicalFormPath, ".frx");
            AddWriteMutation(
                mutations,
                observedTargetSidecar ?? canonicalTargetSidecar,
                canonicalTargetSidecar,
                desiredSidecarBytes);
        }
        else if (observedTargetSidecar is not null)
        {
            AddDeleteMutation(mutations, observedTargetSidecar);
        }
        else
        {
            var canonicalTargetSidecar = Path.ChangeExtension(canonicalFormPath, ".frx");
            mutations.Add(new CommonModulesSourceFileMutation(
                canonicalTargetSidecar,
                canonicalTargetSidecar,
                CommonModulesExpectedFile.Absent,
                DesiredBytes: null,
                VerificationOnly: true));
        }
    }

    private static void AddWriteMutation(
        ICollection<CommonModulesSourceFileMutation> mutations,
        string observedPath,
        string targetPath,
        byte[] desiredBytes)
    {
        if (!File.Exists(observedPath))
        {
            mutations.Add(new CommonModulesSourceFileMutation(
                observedPath,
                targetPath,
                CommonModulesExpectedFile.Absent,
                desiredBytes));
            return;
        }

        var observedBytes = ReadTargetBytes(observedPath);
        if (observedBytes.AsSpan().SequenceEqual(desiredBytes)
            && Path.GetFullPath(observedPath).Equals(
                Path.GetFullPath(targetPath),
                StringComparison.Ordinal))
        {
            mutations.Add(new CommonModulesSourceFileMutation(
                observedPath,
                targetPath,
                CommonModulesExpectedFile.Present(observedBytes),
                desiredBytes,
                VerificationOnly: true));
            return;
        }

        mutations.Add(new CommonModulesSourceFileMutation(
            observedPath,
            targetPath,
            CommonModulesExpectedFile.Present(observedBytes),
            desiredBytes));
    }

    private static void AddDeleteMutation(
        ICollection<CommonModulesSourceFileMutation> mutations,
        string path)
    {
        mutations.Add(new CommonModulesSourceFileMutation(
            path,
            path,
            CommonModulesExpectedFile.Present(ReadTargetBytes(path)),
            DesiredBytes: null));
    }

    private static byte[] ReadTargetBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommonModulesManifestException(
                $"CommonModules target source file could not be read before mutation: {path}. {ex.Message}");
        }
    }

    private static string ResolveTargetPath(
        string documentSourceSetPath,
        string moduleFile,
        bool overwrite)
    {
        var matches = DocumentSourceSetLayout.FindSourceMatches(documentSourceSetPath, moduleFile);
        if (!overwrite && matches.Count > 0)
        {
            throw new CommonModulesManifestException($"CommonModules target source file already exists: {matches[0]}");
        }

        if (overwrite && matches.Count > 1)
        {
            throw new CommonModulesManifestException(
                $"CommonModules target source file has multiple matches for '{moduleFile}': {string.Join(", ", matches)}");
        }

        return matches.Count == 1
            ? matches[0]
            : Path.Combine(documentSourceSetPath, CommonModulesDirectoryName, Path.GetFileName(moduleFile));
    }

    private CommonModulesSourceMutationResult ExecuteCopyPlan(
        IReadOnlyList<CommonModuleCopyPlan> copyPlan,
        CancellationToken cancellationToken)
        => sourceMutationWriter.Execute(
            copyPlan.SelectMany(plan => plan.Mutations).ToArray(),
            cancellationToken);

    private static IReadOnlyList<string> GetManualVerificationPaths(
        IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in copyPlan.SelectMany(plan => plan.Mutations))
        {
            foreach (var path in new[] { mutation.ObservedPath, mutation.TargetPath })
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    paths.Add(fullPath);
                }
            }
        }

        return paths;
    }

    private void SaveManifest(string projectRoot, ProjectManifest manifest)
    {
        try
        {
            manifestEditor.SaveWithRecovery(projectRoot, manifest);
        }
        catch (ProjectManifestEditException ex)
        {
            throw new CommonModulesTransactionException(ex.Message);
        }
    }

    private static string BuildCopyOutput(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        var output = new StringBuilder();
        foreach (var plan in copyPlan)
        {
            if (plan.Mutations.All(mutation => mutation.VerificationOnly))
            {
                continue;
            }

            output.AppendLine($"{plan.Verb} {plan.OutputPath}");
        }

        return output.ToString();
    }

    private static bool ApplyAddEntries(
        ProjectDocument document,
        IReadOnlyList<CommonModuleManifestEntry> orderedEntries,
        IReadOnlySet<string> requestedNames,
        IDictionary<string, InstalledCommonModule> installedByName)
    {
        var changed = false;
        foreach (var entry in orderedEntries)
        {
            var name = entry.Name;
            var requested = requestedNames.Contains(name);
            if (installedByName.TryGetValue(name, out var installed))
            {
                if (!requested || installed.Requested)
                {
                    continue;
                }

                var promoted = installed with { Requested = true };
                ReplaceInstalledEntry(document, installed, promoted);
                installedByName[name] = promoted;
                changed = true;
                continue;
            }

            var installedEntry = new InstalledCommonModule(
                entry.Name,
                entry.InstalledModuleFile,
                requested,
                entry.TestOnly,
                Orphaned: false);
            document.CommonModules.Add(installedEntry);
            installedByName.Add(name, installedEntry);
            changed = true;
        }

        foreach (var requestedName in requestedNames)
        {
            if (!installedByName.TryGetValue(requestedName, out var installed)
                || installed.Requested)
            {
                continue;
            }

            var promoted = installed with { Requested = true };
            ReplaceInstalledEntry(document, installed, promoted);
            installedByName[requestedName] = promoted;
            changed = true;
        }

        return changed;
    }

    private static bool ApplyUpdateEntries(
        ProjectDocument document,
        CommonModulesUpdatePlan updatePlan,
        IDictionary<string, InstalledCommonModule> installedByName)
    {
        var changed = false;
        foreach (var entry in updatePlan.Entries)
        {
            if (installedByName.TryGetValue(entry.Name, out var installed))
            {
                var refreshed = installed with
                {
                    Name = entry.Name,
                    ModuleFile = entry.InstalledModuleFile,
                    TestOnly = entry.TestOnly,
                    Orphaned = false
                };
                if (refreshed != installed)
                {
                    ReplaceInstalledEntry(document, installed, refreshed);
                    installedByName[entry.Name] = refreshed;
                    changed = true;
                }

                continue;
            }

            var dependency = new InstalledCommonModule(
                entry.Name,
                entry.InstalledModuleFile,
                Requested: false,
                entry.TestOnly,
                Orphaned: false);
            document.CommonModules.Add(dependency);
            installedByName.Add(entry.Name, dependency);
            changed = true;
        }

        foreach (var orphanedName in updatePlan.OrphanedNames)
        {
            var installed = installedByName[orphanedName];
            if (installed.Orphaned)
            {
                continue;
            }

            var orphaned = installed with { Orphaned = true };
            ReplaceInstalledEntry(document, installed, orphaned);
            installedByName[orphanedName] = orphaned;
            changed = true;
        }

        return changed;
    }

    private static void ReplaceInstalledEntry(
        ProjectDocument document,
        InstalledCommonModule prior,
        InstalledCommonModule replacement)
    {
        var index = document.CommonModules.FindIndex(module => module.Name.Equals(
            prior.Name,
            StringComparison.OrdinalIgnoreCase));
        document.CommonModules[index] = replacement;
    }

    private static void ValidateSelectedEntryIdentities(IReadOnlyList<CommonModuleManifestEntry> entries)
    {
        var byName = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var byModuleFile = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
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

            byName.Add(entry.Name, entry);
            byModuleFile.Add(entry.InstalledModuleFile, entry);
        }
    }

    private static void ValidatePlannedManifest(ProjectManifest manifest)
    {
        try
        {
            ProjectManifestValidator.Validate(manifest, ProjectManifest.ManifestFileName);
        }
        catch (VbaProjectManifestException ex)
        {
            throw new CommonModulesManifestException(ex.Message);
        }
    }

    private static void ValidateInstalledSourceIdentities(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyDictionary<string, InstalledCommonModule> installedByName)
    {
        foreach (var entry in entries)
        {
            if (!installedByName.TryGetValue(entry.Name, out var installed)
                || installed.ModuleFile.Equals(entry.InstalledModuleFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            throw new CommonModulesManifestException(
                $"Installed CommonModules source identity changed for '{installed.Name}': " +
                $"'{installed.ModuleFile}' -> '{entry.InstalledModuleFile}'. Rename inference is not supported.");
        }
    }

    private static string GetCommonModuleName(string moduleFile)
        => Path.GetFileNameWithoutExtension(moduleFile);

    private static string NormalizeDisplayPath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string GetRepositoryPath(ResolvedProjectContext context)
        => GetRepositoryPath(context.CommonModulesRepositoryPath);

    private static string GetRepositoryPath(ResolvedProject project)
        => GetRepositoryPath(project.CommonModulesRepositoryPath);

    private static string GetRepositoryPath(
        string projectRoot,
        string? commonModulesRepositoryPath)
        => GetRepositoryPath(commonModulesRepositoryPath is null
            ? null
            : ResolveManifestPath(projectRoot, commonModulesRepositoryPath));

    private static string GetRepositoryPath(string? commonModulesRepositoryPath)
    {
        if (commonModulesRepositoryPath is null)
        {
            throw new CommonModulesManifestException("CommonModulesRepository is not configured in vba-project.json.");
        }

        if (!Directory.Exists(commonModulesRepositoryPath))
        {
            throw new CommonModulesManifestException($"CommonModulesRepository was not found: {commonModulesRepositoryPath}");
        }

        return commonModulesRepositoryPath;
    }

    private static string ResolveManifestPath(string projectRoot, string path)
    {
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(projectRoot, normalizedPath));
    }

    private sealed record CommonModulesReferenceResolutionEvidence(
        string DocumentName,
        FileSystemPathIdentity? TemplateIdentity,
        IReadOnlyList<string> MissingNames,
        IReadOnlyDictionary<string, ResolvedVbaProjectReference> ResolvedByRequiredName);

    private sealed record CommonModulesUpdatePlan(
        IReadOnlyList<CommonModuleManifestEntry> Entries,
        IReadOnlyList<string> RequiredReferences,
        IReadOnlySet<string> OrphanedNames);

    private sealed record CommonModulesRebaseResult(
        string Output,
        CommonModulesPackageSnapshot? PackageSnapshot,
        int OrphanedModuleCount,
        int OrphanedDocumentCount);

    private sealed record CommonModuleCopyPlan(
        IReadOnlyList<CommonModulesSourceFileMutation> Mutations,
        string Verb,
        string OutputPath);
}
