using System.Text;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.CommonModules;

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

    private readonly CommonModulesPackageReader packageReader;
    private readonly ProjectManifestEditor manifestEditor;
    private readonly VbaProjectReferencePlanner? referencePlanner;
    private readonly IProjectManifestMutationCoordinator? manifestMutationCoordinator;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

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
    {
        packageReader = new CommonModulesPackageReader(manifestReader);
        this.manifestEditor = manifestEditor;
        this.referencePlanner = referencePlanner;
        this.manifestMutationCoordinator = manifestMutationCoordinator;
        this.pathIdentityResolver = pathIdentityResolver ?? new FileSystemPathIdentityResolver();
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
            .GetResult();

    /// <summary>
    /// Adds requested CommonModules after resolving every missing required VBA reference.
    /// </summary>
    public async Task<string> AddAsync(
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

        var repositoryPath = GetRepositoryPath(context);
        var entries = packageReader.Load(repositoryPath).Entries;
        var selectionPlan = CommonModulesDependencyResolver.ResolveRequestedPlan(
            entries,
            normalizedRequestedModules);
        ValidateSelectedEntryIdentities(selectionPlan.Entries);
        var invocationDocument = ProjectManifestEditor.GetDocument(
            context.Manifest,
            context.DocumentName);
        var referenceEvidence = await ResolveRequiredReferenceEvidenceAsync(
                context.DocumentName,
                invocationDocument,
                selectionPlan.RequiredReferences,
                context.TemplateDocumentPath,
                cancellationToken)
            .ConfigureAwait(false);

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
            if (plan.Manifest is not null)
            {
                SaveManifest(context.ProjectRoot, plan.Manifest);
            }

            return plan.Result;
        }

        ProjectManifest? recoveryManifest = null;
        try
        {
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
                        recoveryManifest = plan.Manifest;
                        return plan;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return outcome.Result;
        }
        catch (Exception ex) when (recoveryManifest is not null
                                   && ex is IOException
                                       or UnauthorizedAccessException
                                       or ProjectManifestException)
        {
            throw CreateManifestRecoveryException(
                context.ProjectRoot,
                recoveryManifest,
                ex);
        }
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

    private ProjectManifestMutationPlan<string> RebaseAdd(
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

        var repositoryPath = GetRepositoryPath(snapshot.ProjectRoot, snapshot.Manifest.CommonModulesRepository);
        var entries = packageReader.Load(repositoryPath).Entries;
        var selectionPlan = CommonModulesDependencyResolver.ResolveRequestedPlan(
            entries,
            normalizedRequestedModules);
        var orderedEntries = selectionPlan.Entries;
        ValidateSelectedEntryIdentities(orderedEntries);
        var requestedNames = normalizedRequestedModules
            .Select(GetCommonModuleName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var document = ProjectManifestEditor.GetDocument(plannedManifest, documentName);
        var referencesChanged = AppendRequiredReferencesFromEvidence(
            snapshot.ProjectRoot,
            documentName,
            document,
            selectionPlan.RequiredReferences,
            referenceEvidence);
        var installedByName = document.CommonModules.ToDictionary(
            module => module.Name,
            StringComparer.OrdinalIgnoreCase);
        ValidateInstalledSourceIdentities(orderedEntries, installedByName);
        var entriesToCopy = orderedEntries
            .Where(entry => !installedByName.ContainsKey(entry.Name))
            .ToArray();
        var documentSourceSetPath = ResolveManifestPath(snapshot.ProjectRoot, document.SourcePath);
        var copyPlan = PlanCopyEntries(
            repositoryPath,
            documentSourceSetPath,
            entriesToCopy,
            "Copied",
            force,
            documentName: null);
        var changed = ApplyInstalledEntries(document, orderedEntries, requestedNames, installedByName)
            || referencesChanged;
        ValidatePlannedManifest(plannedManifest);
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCopyPlan(copyPlan);

        var copied = BuildCopyOutput(copyPlan);
        var result = copied.Length == 0
            ? "No CommonModules changes." + Environment.NewLine
            : copied;
        return changed
            ? ProjectManifestMutationPlan<string>.Commit(plannedManifest, result)
            : ProjectManifestMutationPlan<string>.NoOp(result);
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
            .GetResult();

    /// <summary>
    /// Refreshes installed CommonModules after resolving all document requirements.
    /// </summary>
    public async Task<string> UpdateAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
    {
        var repositoryPath = GetRepositoryPath(project);
        var entries = packageReader.Load(repositoryPath).Entries;
        var referenceEvidenceByDocument = new Dictionary<string, CommonModulesReferenceResolutionEvidence>(
            StringComparer.OrdinalIgnoreCase);
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

        if (manifestMutationCoordinator is null)
        {
            var plan = RebaseUpdate(
                new ProjectManifestMutationSnapshot(
                    project.ProjectRoot,
                    project.ManifestPath,
                    project.Manifest),
                referenceEvidenceByDocument,
                cancellationToken);
            if (plan.Manifest is not null)
            {
                SaveManifest(project.ProjectRoot, plan.Manifest);
            }

            return plan.Result;
        }

        ProjectManifest? recoveryManifest = null;
        try
        {
            var outcome = await manifestMutationCoordinator.ExecuteAsync(
                    project.ProjectRoot,
                    ProjectManifestMutationCommand.CommonModuleUpdate,
                    snapshot =>
                    {
                        var plan = RebaseUpdate(
                            snapshot,
                            referenceEvidenceByDocument,
                            cancellationToken);
                        recoveryManifest = plan.Manifest;
                        return plan;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return outcome.Result;
        }
        catch (Exception ex) when (recoveryManifest is not null
                                   && ex is IOException
                                       or UnauthorizedAccessException
                                       or ProjectManifestException)
        {
            throw CreateManifestRecoveryException(
                project.ProjectRoot,
                recoveryManifest,
                ex);
        }
    }

    private CommonModulesTransactionException CreateManifestRecoveryException(
        string projectRoot,
        ProjectManifest recoveryManifest,
        Exception manifestSaveException)
        => new(manifestEditor.CreateRecoveryAfterFailedSave(
            projectRoot,
            recoveryManifest,
            manifestSaveException));

    private ProjectManifestMutationPlan<string> RebaseUpdate(
        ProjectManifestMutationSnapshot snapshot,
        IReadOnlyDictionary<string, CommonModulesReferenceResolutionEvidence> referenceEvidenceByDocument,
        CancellationToken cancellationToken)
    {
        var repositoryPath = GetRepositoryPath(snapshot.ProjectRoot, snapshot.Manifest.CommonModulesRepository);
        var entries = packageReader.Load(repositoryPath).Entries;
        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var copyPlans = new List<CommonModuleCopyPlan>();
        var manifestChanged = false;
        var updatePlans = new List<(
            string DocumentName,
            ProjectDocument Document,
            CommonModulesUpdatePlan Plan)>();

        foreach (var (documentName, document) in plannedManifest.Documents.OrderBy(
                     item => item.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            var updatePlan = CreateUpdatePlan(entries, document);
            if (updatePlan is null)
            {
                continue;
            }

            updatePlans.Add((documentName, document, updatePlan));
        }

        if (updatePlans.Count != referenceEvidenceByDocument.Count
            || updatePlans.Any(updatePlan =>
                !referenceEvidenceByDocument.ContainsKey(updatePlan.DocumentName)))
        {
            throw RequiredReferencePlanChanged();
        }

        foreach (var (documentName, document, updatePlan) in updatePlans)
        {
            ValidateSelectedEntryIdentities(updatePlan.Entries);
            var referenceEvidence = referenceEvidenceByDocument[documentName];
            manifestChanged |= AppendRequiredReferencesFromEvidence(
                snapshot.ProjectRoot,
                documentName,
                document,
                updatePlan.RequiredReferences,
                referenceEvidence);
            var installedByName = document.CommonModules.ToDictionary(
                module => module.Name,
                StringComparer.OrdinalIgnoreCase);
            ValidateInstalledSourceIdentities(updatePlan.Entries, installedByName);
            var documentSourceSetPath = ResolveManifestPath(snapshot.ProjectRoot, document.SourcePath);
            copyPlans.AddRange(PlanCopyEntries(
                repositoryPath,
                documentSourceSetPath,
                updatePlan.Entries,
                "Updated",
                overwrite: true,
                documentName));
            if (ApplyInstalledEntries(
                    document,
                    updatePlan.Entries,
                    updatePlan.RequestedNames,
                    installedByName))
            {
                manifestChanged = true;
            }
        }

        ValidatePlannedManifest(plannedManifest);
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCopyPlan(copyPlans);
        var output = BuildCopyOutput(copyPlans);
        var result = output.Length == 0
            ? "No installed CommonModules entries were found." + Environment.NewLine
            : output;
        return manifestChanged
            ? ProjectManifestMutationPlan<string>.Commit(plannedManifest, result)
            : ProjectManifestMutationPlan<string>.NoOp(result);
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

        var requestedModuleNames = document.CommonModules
            .Where(module => module.Requested)
            .Select(module => module.Name)
            .ToArray();
        var dependencyClosureEntries = requestedModuleNames.Length == 0
            ? []
            : CommonModulesDependencyResolver.ResolveRequestedEntries(entries, requestedModuleNames);
        var installedEntries = installedModuleNames
            .Select(module => CommonModulesDependencyResolver.ResolveEntry(entries, module))
            .ToArray();
        var orderedEntries = CommonModulesDependencyResolver.MergeEntries(
            dependencyClosureEntries,
            installedEntries);
        var selectionPlan = CommonModulesDependencyResolver.CreateSelectionPlan(orderedEntries);
        return new CommonModulesUpdatePlan(
            selectionPlan.Entries,
            selectionPlan.RequiredReferences,
            requestedModuleNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<CommonModuleCopyPlan> PlanCopyEntries(
        string repositoryPath,
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
            var sourcePath = Path.Combine(repositoryPath, entry.ModuleFile);
            if (!File.Exists(sourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {sourcePath}");
            }

            var targetPath = ResolveTargetPath(documentSourceSetPath, entry.InstalledModuleFile, overwrite);
            var canonicalTargetPath = Path.GetFullPath(targetPath);
            if (plannedTargets.TryGetValue(canonicalTargetPath, out var conflictingEntry))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules entries '{conflictingEntry.ModuleFile}' and '{entry.ModuleFile}' resolve to the same target source file: {targetPath}");
            }

            plannedTargets.Add(canonicalTargetPath, entry);
            var sidecarDeletePaths = DocumentSourceSetLayout.IsFormFile(entry.ModuleFile)
                ? DocumentSourceSetLayout.FindFormSidecars(documentSourceSetPath, entry.ModuleFile)
                : [];
            var sourceSidecarPath = DocumentSourceSetLayout.IsFormFile(entry.ModuleFile)
                ? DocumentSourceSetLayout.ResolveExistingSidecarPath(sourcePath)
                : null;
            var targetSidecarPath = sourceSidecarPath is null
                ? null
                : Path.ChangeExtension(targetPath, ".frx");
            var relativeTargetPath = NormalizeDisplayPath(Path.GetRelativePath(documentSourceSetPath, targetPath));
            var outputPath = documentName is null ? relativeTargetPath : $"{documentName}/{relativeTargetPath}";
            plans.Add(new CommonModuleCopyPlan(
                SourcePath: sourcePath,
                TargetPath: targetPath,
                SourceSidecarPath: sourceSidecarPath,
                TargetSidecarPath: targetSidecarPath,
                SidecarDeletePaths: sidecarDeletePaths,
                Verb: verb,
                OutputPath: outputPath));
        }

        return plans;
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

    private static void ExecuteCopyPlan(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        try
        {
            foreach (var plan in copyPlan)
            {
                foreach (var sidecarPath in plan.SidecarDeletePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(sidecarPath))
                    {
                        File.Delete(sidecarPath);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetPath)!);
                File.Copy(plan.SourcePath, plan.TargetPath, overwrite: true);
                if (plan.SourceSidecarPath is not null && plan.TargetSidecarPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetSidecarPath)!);
                    File.Copy(plan.SourceSidecarPath, plan.TargetSidecarPath, overwrite: true);
                }
            }
        }
        catch (IOException ex)
        {
            throw new CommonModulesTransactionException(FileOperationFailureMessage(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CommonModulesTransactionException(FileOperationFailureMessage(ex));
        }
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

    private static string FileOperationFailureMessage(Exception ex)
        => $"CommonModules file operation failed before manifest save; manifest was not saved and source files may have been partially updated. {ex.Message}";

    private static string BuildCopyOutput(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        var output = new StringBuilder();
        foreach (var plan in copyPlan)
        {
            output.AppendLine($"{plan.Verb} {plan.OutputPath}");
        }

        return output.ToString();
    }

    private static bool ApplyInstalledEntries(
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
                var refreshed = installed with
                {
                    Name = entry.Name,
                    ModuleFile = entry.InstalledModuleFile,
                    Requested = installed.Requested || requested,
                    TestOnly = entry.TestOnly
                };
                if (refreshed != installed)
                {
                    var index = document.CommonModules.FindIndex(module => module.Name.Equals(installed.Name, StringComparison.OrdinalIgnoreCase));
                    document.CommonModules[index] = refreshed;
                    installedByName[name] = refreshed;
                    changed = true;
                }

                continue;
            }

            var installedEntry = new InstalledCommonModule(
                entry.Name,
                entry.InstalledModuleFile,
                requested,
                entry.TestOnly);
            document.CommonModules.Add(installedEntry);
            installedByName.Add(name, installedEntry);
            changed = true;
        }

        return changed;
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
        IReadOnlySet<string> RequestedNames);

    private sealed record CommonModuleCopyPlan(
        string SourcePath,
        string TargetPath,
        string? SourceSidecarPath,
        string? TargetSidecarPath,
        IReadOnlyList<string> SidecarDeletePaths,
        string Verb,
        string OutputPath);
}
