using System.Runtime.ExceptionServices;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaTools.Semantics;

namespace VbaDev.App.Build;

/// <summary>
/// Materializes closed workbook output intents through one owned staging workflow.
/// </summary>
internal sealed class WorkbookMaterializer
{
    private readonly VbaSourceAdmission sourceAdmission;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly IWorkbookGenerationAutomation workbookGenerationAutomation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly IWorkbookOutputTransactionFactory transactionFactory;
    private readonly VbeImportSourceSetFactory importSourceSetFactory;
    private readonly WorkbookAutomationTimeouts baseTimeouts;
    private readonly Func<string, WorkbookStagingArtifact> inspectionWorkbookStager;
    private readonly WorkbookMaterializationNamePreflight namePreflight;
    private readonly WorkbookMaterializationOutputValidator outputValidator = new();
    private readonly IProjectSemanticInputProvider? semanticInputProvider;

    /// <summary>
    /// Creates the workbook materializer.
    /// </summary>
    /// <param name="workbookGenerationAutomation">The workbook automation port used to edit VBA projects.</param>
    /// <param name="referenceNormalizer">The service that reconciles workbook references with manifest references.</param>
    internal WorkbookMaterializer(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
        : this(
            ownershipFactory,
            new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get),
            workbookGenerationAutomation,
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(ownershipFactory),
            new VbeImportSourceSetFactory(ownershipFactory))
    {
    }

    /// <summary>
    /// Creates the materializer with explicit collaborators.
    /// </summary>
    internal WorkbookMaterializer(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        VbaSourceAdmission sourceAdmission,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory)
        : this(
            ownershipFactory,
            sourceAdmission,
            workbookGenerationAutomation,
            referenceNormalizer,
            transactionFactory,
            new VbeImportSourceSetFactory(ownershipFactory))
    {
    }

    internal WorkbookMaterializer(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory,
        VbeImportSourceSetFactory importSourceSetFactory)
        : this(
            ownershipFactory,
            new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get),
            workbookGenerationAutomation,
            referenceNormalizer,
            transactionFactory,
            importSourceSetFactory)
    {
    }

    internal WorkbookMaterializer(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        VbaSourceAdmission sourceAdmission,
        IWorkbookGenerationAutomation workbookGenerationAutomation,
        WorkbookReferenceNormalizer referenceNormalizer,
        IWorkbookOutputTransactionFactory transactionFactory,
        VbeImportSourceSetFactory importSourceSetFactory,
        WorkbookAutomationTimeouts? baseTimeouts = null,
        Func<string, WorkbookStagingArtifact>? inspectionWorkbookStager = null,
        WorkbookMaterializationNamePreflight? namePreflight = null,
        IProjectSemanticInputProvider? semanticInputProvider = null)
    {
        this.ownershipFactory = ownershipFactory;
        this.sourceAdmission = sourceAdmission;
        this.workbookGenerationAutomation = workbookGenerationAutomation;
        this.referenceNormalizer = referenceNormalizer;
        this.transactionFactory = transactionFactory;
        this.importSourceSetFactory = importSourceSetFactory;
        this.baseTimeouts = baseTimeouts ?? WorkbookAutomationTimeouts.Default;
        this.inspectionWorkbookStager = inspectionWorkbookStager ?? (template => StageInspectionWorkbook(ownershipFactory, template));
        this.namePreflight = namePreflight ?? new WorkbookMaterializationNamePreflight();
        this.semanticInputProvider = semanticInputProvider;
    }

    internal async Task<WorkbookMaterializationResult> MaterializeAsync(
        WorkbookMaterializationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var plan = intent is WorkbookMaterializationIntent.ProjectBuild build
            ? await CreateAnalyzedBuildPlanAsync(build.Context, cancellationToken).ConfigureAwait(false)
            : CreatePlan(intent, cancellationToken);
        return await MaterializeCoreAsync(
            plan.DocumentName,
            plan.TemplateWorkbookPath,
            plan.TargetWorkbookPath,
            plan.DesiredReferences,
            plan.SourceInput,
            plan.Timeouts,
            plan.NormalizeReferences,
            plan.GuardExistingTarget,
            plan.CapturedTemplate,
            plan.SemanticInputs,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkbookMaterializationPlan> CreateAnalyzedBuildPlanAsync(
        ResolvedProjectContext context, CancellationToken cancellationToken)
    {
        CapturedWorkbookTemplate? template = null;
        VbaProjectSemanticInputs? inputs = null;
        var admission = await sourceAdmission.AdmitAnalyzedProjectBuildAsync(
            context.DocumentSourceSetPath, context.Document.CommonModules,
            async (sources, token) =>
            {
                var provider = semanticInputProvider
                    ?? throw new InvalidOperationException("A required project semantic input provider was not configured for ordinary Build.");
                template = CapturedWorkbookTemplate.Capture(context.TemplateDocumentPath, token);
                inputs = await provider.AcquireAsync(context, template, sources, token).ConfigureAwait(false);
                return inputs;
            }, cancellationToken).ConfigureAwait(false);
        return CreateProjectPlan(context, context.BinDocumentPath, ResolveTimeouts(context),
            new AdmittedWorkbookGenerationSourceInput(admission)) with
        {
            CapturedTemplate = template,
            SemanticInputs = inputs
        };
    }

    internal async Task<ProjectInspectionResult> InspectAsync(
        ProjectInspectionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var context = intent.Context;
        using var buildProfile = PrepareInspectionProfile(
            ProjectInspectionProfile.Build,
            () => intent.SourceCapture.AdmitProjectBuild(
                context.Document.CommonModules,
                cancellationToken),
            cancellationToken);
        using var publishProfile = PrepareInspectionProfile(
            ProjectInspectionProfile.Publish,
            () => intent.SourceCapture.AdmitProjectPublish(
                context.Document.CommonModules,
                cancellationToken),
            cancellationToken);
        var profiles = new[] { buildProfile, publishProfile };
        var activeProfiles = profiles
            .Where(profile => profile.SourceSet is not null)
            .ToArray();
        if (activeProfiles.Length == 0)
        {
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(profiles.Select(GetInspectionResult).ToArray()),
                stagedWorkbook: null);
        }

        WorkbookStagingArtifact? stagedWorkbook = null;
        try
        {
            if (!File.Exists(context.TemplateDocumentPath))
            {
                SetPendingInspectionResults(
                    profiles,
                    profile => new ProjectInspectionProfileResult(
                        profile.Profile,
                        ProjectInspectionStatus.Skip,
                        $"The source template does not exist: {context.TemplateDocumentPath}."));
                return CompleteInspection(
                    profiles,
                    new ProjectInspectionResult(profiles.Select(GetInspectionResult).ToArray()),
                    stagedWorkbook);
            }

            stagedWorkbook = inspectionWorkbookStager(context.TemplateDocumentPath);
            await workbookGenerationAutomation.RunAsync(
                stagedWorkbook.Path,
                ResolveTimeouts(context),
                async (session, operationCancellationToken) =>
                {
                    var projectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var modules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var retainedModules = modules
                        .Where(module => !module.Kind.IsImportable())
                        .ToArray();
                    var activeReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var desiredReferenceNames = context.Document.References
                        .Select(reference => reference.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var referencesKnownToRemain = activeReferences
                        .Where(reference =>
                            !reference.IsRemovable ||
                            desiredReferenceNames.Contains(reference.Name))
                        .ToArray();

                    foreach (var profile in activeProfiles)
                    {
                        var initialLivePreflight = namePreflight.InspectLivePhase(
                            profile.SourceSet!.SourceFiles,
                            retainedModules,
                            projectName,
                            referencesKnownToRemain);
                        try
                        {
                            namePreflight.ThrowIfFailed(
                                profile.SourcePreflight!,
                                initialLivePreflight);
                        }
                        catch (InvalidOperationException exception)
                        {
                            profile.Result = new ProjectInspectionProfileResult(
                                profile.Profile,
                                ProjectInspectionStatus.Fail,
                                exception.Message);
                        }
                    }

                    var profilesRequiringFinalInspection = activeProfiles
                        .Where(profile => profile.Result is null)
                        .ToArray();
                    if (profilesRequiringFinalInspection.Length == 0)
                    {
                        return true;
                    }

                    foreach (var module in modules.Where(module => module.Kind.IsImportable()))
                    {
                        await session
                            .RemoveModuleAsync(module.Name, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    await referenceNormalizer.NormalizeAsync(
                            session,
                            context.DocumentName,
                            context.Document.References,
                            operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalProjectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalModules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    foreach (var profile in profilesRequiringFinalInspection)
                    {
                        var finalLivePreflight = namePreflight.InspectLivePhase(
                            profile.SourceSet!.SourceFiles,
                            finalModules,
                            finalProjectName,
                            finalReferences);
                        try
                        {
                            namePreflight.ThrowIfFailed(
                                profile.SourcePreflight!,
                                finalLivePreflight);
                        }
                        catch (InvalidOperationException exception)
                        {
                            profile.Result = new ProjectInspectionProfileResult(
                                profile.Profile,
                                ProjectInspectionStatus.Fail,
                                exception.Message);
                        }
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(profiles.Select(GetInspectionResult).ToArray()),
                stagedWorkbook);
        }
        catch (WorkbookAutomationTimeoutException exception)
        {
            SetPendingInspectionResults(
                profiles,
                profile => new ProjectInspectionProfileResult(
                    profile.Profile,
                    ProjectInspectionStatus.Unverified,
                    exception.Message));
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(profiles.Select(GetInspectionResult).ToArray()),
                stagedWorkbook, exception);
        }
        catch (WorkbookAutomationCanceledException exception)
        {
            SetPendingInspectionResults(
                profiles,
                profile => new ProjectInspectionProfileResult(
                    profile.Profile,
                    ProjectInspectionStatus.Unverified,
                    exception.Message));
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(
                    profiles.Select(GetInspectionResult).ToArray(),
                    Complete: false,
                    Canceled: true),
                stagedWorkbook, exception);
        }
        catch (WorkbookAutomationCleanupException exception)
        {
            SetPendingInspectionResults(
                profiles,
                profile => new ProjectInspectionProfileResult(
                    profile.Profile,
                    ProjectInspectionStatus.Unverified,
                    exception.Message));
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(
                    profiles.Select(GetInspectionResult).ToArray(),
                    Complete: false),
                stagedWorkbook, exception);
        }
        catch (WorkbookStagingPreparationException exception)
        {
            SetPendingInspectionResults(
                profiles,
                profile => new ProjectInspectionProfileResult(
                    profile.Profile,
                    ProjectInspectionStatus.Fail,
                    $"The disposable template could not be materialized: {exception.Message}"));
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(
                    profiles.Select(GetInspectionResult).ToArray(),
                    Complete: false),
                stagedWorkbook, exception);
        }
        catch (Exception exception)
        {
            SetPendingInspectionResults(
                profiles,
                profile => new ProjectInspectionProfileResult(
                    profile.Profile,
                    ProjectInspectionStatus.Fail,
                    $"The disposable template could not be materialized: {exception.Message}"));
            return CompleteInspection(
                profiles,
                new ProjectInspectionResult(profiles.Select(GetInspectionResult).ToArray()),
                stagedWorkbook, exception);
        }
    }

    private WorkbookMaterializationPlan CreatePlan(
        WorkbookMaterializationIntent intent,
        CancellationToken cancellationToken)
        => intent switch
        {
            WorkbookMaterializationIntent.Publish publish => CreateProjectPlan(
                publish.Context,
                publish.Context.PublishDocumentPath,
                ResolveTimeouts(publish.Context),
                new AdmittedWorkbookGenerationSourceInput(sourceAdmission.AdmitProjectPublish(
                    publish.Context.DocumentSourceSetPath,
                    publish.Context.Document.CommonModules,
                    cancellationToken))),
            WorkbookMaterializationIntent.SourceSnapshotBuild snapshot => CreateProjectPlan(
                snapshot.Context,
                snapshot.TargetWorkbookPath,
                ResolveTimeouts(snapshot.Context),
                snapshot.SourceCapture),
            WorkbookMaterializationIntent.ExplicitImport import => new WorkbookMaterializationPlan(
                Path.GetFileNameWithoutExtension(import.TargetWorkbookPath),
                import.TargetWorkbookPath,
                import.TargetWorkbookPath,
                [],
                new AdmittedWorkbookGenerationSourceInput(import.Admission),
                baseTimeouts,
                NormalizeReferences: false,
                GuardExistingTarget: true),
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, null)
        };

    private WorkbookMaterializationPlan CreateProjectPlan(
        ResolvedProjectContext context,
        string targetWorkbookPath,
        WorkbookAutomationTimeouts timeouts,
        IAdmittedWorkbookGenerationSourceInput sourceInput)
        => new(
            context.DocumentName,
            context.TemplateDocumentPath,
            targetWorkbookPath,
            context.Document.References,
            sourceInput,
            timeouts,
            NormalizeReferences: true,
            GuardExistingTarget: false);

    private WorkbookAutomationTimeouts ResolveTimeouts(ResolvedProjectContext context)
    {
        var configuredTimeouts = context.Manifest.CommandDefaults?.ExcelAutomation;
        return baseTimeouts with
        {
            WorkbookOpen = configuredTimeouts?.WorkbookOpenTimeoutSeconds is int openSeconds
                ? TimeSpan.FromSeconds(openSeconds)
                : baseTimeouts.WorkbookOpen,
            WorkbookSave = configuredTimeouts?.WorkbookSaveTimeoutSeconds is int saveSeconds
                ? TimeSpan.FromSeconds(saveSeconds)
                : baseTimeouts.WorkbookSave
        };
    }

    private InspectionProfile PrepareInspectionProfile(
        ProjectInspectionProfile profile,
        Func<AdmittedVbaSourceSet> admitSources,
        CancellationToken cancellationToken)
    {
        var result = new InspectionProfile(profile);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.SourceSet = importSourceSetFactory.Create(admitSources());
            result.SourcePreflight = namePreflight.InspectSourcePhase(
                result.SourceSet.SourceFiles);
            namePreflight.ThrowIfFailed(result.SourcePreflight);
        }
        catch (OperationCanceledException)
        {
            result.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                result.Dispose();
            }
            catch (Exception cleanupError)
            {
                result.CleanupEvidence = cleanupError.Message;
            }

            result.Result = new ProjectInspectionProfileResult(
                profile,
                ProjectInspectionStatus.Fail,
                exception.Message);
        }

        return result;
    }

    private static ProjectInspectionProfileResult GetInspectionResult(
        InspectionProfile profile)
        => profile.Result ?? new ProjectInspectionProfileResult(
            profile.Profile,
            ProjectInspectionStatus.Pass,
            "The profile is conflict-free on a disposable template copy.");

    private static void SetPendingInspectionResults(
        IReadOnlyList<InspectionProfile> profiles,
        Func<InspectionProfile, ProjectInspectionProfileResult> createResult)
    {
        foreach (var profile in profiles.Where(profile =>
                     profile.SourceSet is not null && profile.Result is null))
        {
            profile.Result = createResult(profile);
        }
    }

    private ProjectInspectionResult CompleteInspection(
        IReadOnlyList<InspectionProfile> profiles,
        ProjectInspectionResult result,
        WorkbookStagingArtifact? stagedWorkbook,
        Exception? operationError = null)
    {
        var processReleaseProven = true;
        if (operationError is not null)
        {
            var facts = WorkbookAutomationTerminalFacts.Analyze(operationError);
            processReleaseProven = facts.ProcessReleaseProven;
        }
        var sharedCleanupEvidence = new List<string>();
        if (stagedWorkbook is not null)
        {
            if (!processReleaseProven)
            {
                stagedWorkbook.ReleaseWithoutCleanup();
                sharedCleanupEvidence.Add(
                    $"Disposable workbook retained because owned Excel process release could not be proved: {stagedWorkbook.Path}");
            }
            else
            {
                var cleanup = stagedWorkbook.Cleanup();
                if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
                    sharedCleanupEvidence.Add("Disposable workbook cleanup could not be confirmed: " +
                        WorkbookStagingArtifact.DescribeCleanup(cleanup));
            }
        }

        var profileCleanupEvidence = new Dictionary<InspectionProfile, IReadOnlyList<string>>();
        foreach (var profile in profiles)
        {
            var evidence = new List<string>();
            if (profile.CleanupEvidence is not null)
            {
                evidence.Add(profile.CleanupEvidence);
            }

            try
            {
                if (!processReleaseProven && profile.SourceSet is { } sourceSet)
                {
                    sourceSet.RetainWithoutCleanup();
                    evidence.Add($"VBE source mirror retained because owned Excel process release could not be proved: {sourceSet.StagingPath}");
                }
                else
                {
                    profile.Dispose();
                }
            }
            catch (Exception cleanupError)
            {
                evidence.Add(cleanupError.Message);
            }

            if (evidence.Count > 0)
            {
                profileCleanupEvidence.Add(profile, evidence);
            }
        }

        if (sharedCleanupEvidence.Count == 0 && profileCleanupEvidence.Count == 0)
        {
            return result;
        }

        foreach (var profile in profiles)
        {
            var cleanupEvidence = sharedCleanupEvidence
                .Concat(profileCleanupEvidence.GetValueOrDefault(profile) ?? [])
                .ToArray();
            if (cleanupEvidence.Length == 0)
            {
                continue;
            }

            ApplyInspectionCleanupEvidence(profile, cleanupEvidence);
        }

        return new ProjectInspectionResult(
            profiles.Select(GetInspectionResult).ToArray(),
            Complete: false,
            result.Canceled);
    }

    private static void ApplyInspectionCleanupEvidence(
        InspectionProfile profile,
        IReadOnlyList<string> cleanupEvidence)
    {
        var cleanupMessage = string.Join(" ", cleanupEvidence);
        if (profile.Result is null)
        {
            profile.Result = new ProjectInspectionProfileResult(
                profile.Profile,
                ProjectInspectionStatus.Unverified,
                cleanupMessage);
        }
        else
        {
            profile.Result = profile.Result with
            {
                Status = profile.Result.Status == ProjectInspectionStatus.Fail
                    ? ProjectInspectionStatus.Fail
                    : ProjectInspectionStatus.Unverified,
                Message = $"{profile.Result.Message} {cleanupMessage}"
            };
        }
    }

    internal static WorkbookStagingArtifact StageInspectionWorkbook(
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        string templateWorkbookPath,
        string? directory = null)
        => WorkbookStagingArtifact.CreateCopy(ownershipFactory, templateWorkbookPath,
            directory ?? Path.Combine(Path.GetTempPath(), $"vba-dev-doctor-{Guid.NewGuid():N}"),
            Path.GetFileName(templateWorkbookPath), createDirectory: true);

    private static void VerifyAnalyzedProjectIdentity(VbaProjectSemanticInputs? inputs, string actualName)
    {
        if (inputs?.ProjectNamespaces?.ContainingProjectName is { } expectedName
            && !expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase))
        {
            throw new BuildCommandException($"The generated VBA project identity differs from the analyzed template: expected '{expectedName}', found '{actualName}'. Restore or re-export the source template before rebuilding.");
        }
    }

    private static void VerifyAnalyzedReferences(VbaProjectSemanticInputs? inputs, IReadOnlyList<WorkbookReference> references)
    {
        if (inputs is null) return;
        foreach (var (name, expected) in inputs.ReferenceCatalogIdentities)
        {
            var actual = references.Where(reference => VbaReferenceName.Comparer.Equals(reference.Name, name)).ToArray();
            var expectedNamespace = inputs.ProjectNamespaces?.References.FirstOrDefault(reference =>
                VbaReferenceName.Comparer.Equals(reference.ReferenceName, name))?.Name;
            if (actual.Length == 1 && Guid.TryParse(actual[0].Guid, out var actualGuid)
                && Guid.TryParse(expected.Guid, out var expectedGuid) && actualGuid == expectedGuid
                && actual[0].Major == expected.MajorVersion && actual[0].Minor == expected.MinorVersion
                && (expectedNamespace is null || expectedNamespace.Equals(actual[0].NamespaceName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var found = actual.Length == 1
                ? $"{actual[0].Guid ?? "unavailable GUID"} {actual[0].Major}.{actual[0].Minor}, namespace '{actual[0].NamespaceName}'"
                : $"{actual.Length} matching references";
            throw new BuildCommandException($"Generated reference '{name}' differs from the analyzed catalog: expected {expected.Guid} {expected.MajorVersion}.{expected.MinorVersion}, namespace '{expectedNamespace}'; found {found}. Repair the source-template references or registered TypeLib before rebuilding.");
        }
    }

    private async Task<WorkbookMaterializationResult> MaterializeCoreAsync(
        string documentName,
        string templateWorkbookPath,
        string targetWorkbookPath,
        IReadOnlyList<VbaProjectReference> desiredReferences,
        IAdmittedWorkbookGenerationSourceInput sourceInput,
        WorkbookAutomationTimeouts timeouts,
        bool normalizeReferences,
        bool guardExistingTarget,
        CapturedWorkbookTemplate? capturedTemplate,
        VbaProjectSemanticInputs? semanticInputs,
        CancellationToken cancellationToken)
    {
        var sourceAdmission = sourceInput.Admission;
        var preparedSource = CreateImportSourceSetAndReleaseInput(
            guardExistingTarget || capturedTemplate is not null ? null : templateWorkbookPath,
            sourceInput,
            cancellationToken);
        VbeImportSourceSet? importSourceSet = preparedSource.SourceSet;
        var sourcePreflight = preparedSource.Preflight;
        IWorkbookOutputTransaction? transaction = null;
        FileStream? targetGuard = null;
        try
        {
            if (guardExistingTarget)
            {
                targetGuard = new FileStream(
                    targetWorkbookPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }

            transaction = capturedTemplate is null
                ? transactionFactory.Create(templateWorkbookPath, targetWorkbookPath)
                : transactionFactory.Create(capturedTemplate, targetWorkbookPath);
            var sessionResult = await workbookGenerationAutomation.RunAsync(
                transaction.StagingWorkbookPath,
                timeouts,
                async (session, operationCancellationToken) =>
                {
                    var projectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    VerifyAnalyzedProjectIdentity(semanticInputs, projectName);
                    var modules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var retainedModules = modules
                        .Where(component => !component.Kind.IsImportable())
                        .ToArray();
                    var activeReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var desiredReferenceNames = desiredReferences
                        .Select(reference => reference.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var referencesKnownToRemain = normalizeReferences
                        ? activeReferences
                            .Where(reference =>
                                !reference.IsRemovable ||
                                desiredReferenceNames.Contains(reference.Name))
                            .ToArray()
                        : activeReferences;
                    var initialLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        retainedModules,
                        projectName,
                        referencesKnownToRemain);
                    if (initialLivePreflight.HasFailures)
                    {
                        namePreflight.ThrowIfFailed(sourcePreflight, initialLivePreflight);
                    }
                    foreach (var component in modules.Where(component => component.Kind.IsImportable()))
                    {
                        await session
                            .RemoveModuleAsync(component.Name, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    var result = normalizeReferences
                        ? await referenceNormalizer.NormalizeAsync(
                                session,
                                documentName,
                                desiredReferences,
                                operationCancellationToken,
                                semanticInputs)
                            .ConfigureAwait(false)
                        : [];
                    var finalProjectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    VerifyAnalyzedProjectIdentity(semanticInputs, finalProjectName);
                    var finalModules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var finalReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    VerifyAnalyzedReferences(semanticInputs, finalReferences);
                    var finalLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        finalModules,
                        finalProjectName,
                        finalReferences);
                    namePreflight.ThrowIfFailed(sourcePreflight, finalLivePreflight);

                    foreach (var sourceFile in importSourceSet.SourceFiles)
                    {
                        await session
                            .ImportModuleAsync(sourceFile, operationCancellationToken)
                            .ConfigureAwait(false);
                    }

                    var verificationReport = await session
                        .VerifyAsync(operationCancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new WorkbookVerificationReportMissingException();
                    var committedProjectName = await session
                        .GetProjectNameAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    VerifyAnalyzedProjectIdentity(semanticInputs, committedProjectName);
                    var committedModules = await session
                        .GetModulesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    var importedComponentNames = importSourceSet.SourceFiles
                        .Select(sourceFile => sourceFile.ImportVerification.ComponentName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var committedRetainedModules = committedModules
                        .Where(component => !importedComponentNames.Contains(component.Name))
                        .ToArray();
                    var committedReferences = await session
                        .GetReferencesAsync(operationCancellationToken)
                        .ConfigureAwait(false);
                    VerifyAnalyzedReferences(semanticInputs, committedReferences);
                    var committedLivePreflight = namePreflight.InspectLivePhase(
                        importSourceSet.SourceFiles,
                        committedRetainedModules,
                        committedProjectName,
                        committedReferences);
                    namePreflight.ThrowIfFailed(sourcePreflight, committedLivePreflight);
                    await session.SaveAsync(operationCancellationToken).ConfigureAwait(false);
                    transaction.CaptureSavedWorkbook();
                    return new WorkbookGenerationSessionResult(
                        result,
                        verificationReport,
                        importSourceSet.SourceFiles.Count);
                },
                cancellationToken).ConfigureAwait(false);

            transaction.CompleteSavedCapture();
            outputValidator.Validate(transaction.StagingWorkbookPath);

            var completedImportSourceSet = importSourceSet;
            importSourceSet = null;
            completedImportSourceSet.Dispose();

            ThrowIfCanceled(
                cancellationToken,
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.OutputCommit,
                    Path.GetFileName(targetWorkbookPath)));
            targetGuard?.Dispose();
            targetGuard = null;
            try
            {
                transaction.Commit();
            }
            catch (IOException ex)
            {
                throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
            }

            var completedTransaction = transaction;
            transaction = null;
            completedTransaction.Dispose();

            // Commit is the success boundary. A later cancellation cannot turn replaced output into cancellation.
            return new WorkbookMaterializationResult(
                Path.GetFullPath(targetWorkbookPath),
                sessionResult.ImportedSourceCount,
                sessionResult.OutputWarnings,
                sessionResult.VerificationReport,
                sourceAdmission);
        }
        catch (Exception operationError)
        {
            var failure = operationError;
            var terminalFacts = WorkbookAutomationTerminalFacts.Analyze(operationError);
            if (terminalFacts.ProcessReleaseProven)
            {
                if (transaction is not null)
                {
                    try { transaction.CompleteSavedCapture(); }
                    catch (Exception captureError) { failure = CombineFailures(failure, captureError); }
                }
                failure = DisposeAfterFailure(importSourceSet, failure);
                failure = DisposeTransactionAfterFailure(transaction, failure);
            }
            else
            {
                var retainedPaths = new[] { importSourceSet?.StagingPath, transaction?.StagingWorkbookPath }
                    .Where(path => path is not null).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
                importSourceSet?.RetainWithoutCleanup();
                transaction?.RetainWithoutCleanup();
                failure = new BuildCommandException(
                    $"{failure.Message} Excel-dependent scratch was retained because owned process release could not be proved: {string.Join(", ", retainedPaths)}",
                    failure);
            }
            failure = DisposeAfterFailure(targetGuard, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static Exception DisposeAfterFailure(
        IDisposable? resource,
        Exception failure)
    {
        if (resource is null)
        {
            return failure;
        }

        try
        {
            resource.Dispose();
            return failure;
        }
        catch (Exception cleanupError)
        {
            return CombineFailures(failure, cleanupError);
        }
    }

    private static Exception DisposeTransactionAfterFailure(
        IWorkbookOutputTransaction? transaction,
        Exception failure)
    {
        if (transaction is null)
        {
            return failure;
        }

        try
        {
            transaction.Dispose();
            return failure;
        }
        catch (Exception cleanupError)
        {
            return new BuildCommandException(
                $"{failure.Message} {cleanupError.Message}",
                new AggregateException(failure, cleanupError));
        }
    }

    private PreparedImportSource CreateImportSourceSetAndReleaseInput(
        string? templateWorkbookPath,
        IAdmittedWorkbookGenerationSourceInput sourceInput,
        CancellationToken cancellationToken)
    {
        VbeImportSourceSet? importSourceSet = null;
        WorkbookMaterializationNamePreflightReport? sourcePreflight = null;
        Exception? failure = null;
        try
        {
            ThrowIfCanceled(
                cancellationToken,
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup));
            if (templateWorkbookPath is not null && !File.Exists(templateWorkbookPath))
            {
                throw new BuildCommandException($"Template workbook was not found: {templateWorkbookPath}");
            }
            importSourceSet = importSourceSetFactory.Create(sourceInput.Admission);
            sourcePreflight = namePreflight.InspectSourcePhase(importSourceSet.SourceFiles);
            if (sourcePreflight.HasFailures)
            {
                namePreflight.ThrowIfFailed(sourcePreflight);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            sourceInput.Dispose();
        }
        catch (Exception ex)
        {
            failure = CombineFailures(failure, ex);
        }

        if (failure is not null)
        {
            if (importSourceSet is not null)
            {
                try
                {
                    importSourceSet.Dispose();
                }
                catch (Exception ex)
                {
                    failure = CombineFailures(failure, ex);
                }
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new PreparedImportSource(importSourceSet!, sourcePreflight!);
    }

    private static Exception CombineFailures(Exception? operationError, Exception cleanupError)
        => operationError is null
            ? cleanupError
            : new InvalidOperationException(
                $"{operationError.Message} {cleanupError.Message}",
                new AggregateException(operationError, cleanupError));

    private sealed record PreparedImportSource(
        VbeImportSourceSet SourceSet,
        WorkbookMaterializationNamePreflightReport Preflight);

    private sealed record WorkbookGenerationSessionResult(
        IReadOnlyList<string> OutputWarnings,
        VbeImportVerificationReport VerificationReport,
        int ImportedSourceCount);

    private sealed class InspectionProfile(ProjectInspectionProfile profile) : IDisposable
    {
        internal ProjectInspectionProfile Profile { get; } = profile;

        internal VbeImportSourceSet? SourceSet { get; set; }

        internal WorkbookMaterializationNamePreflightReport? SourcePreflight { get; set; }

        internal ProjectInspectionProfileResult? Result { get; set; }

        internal string? CleanupEvidence { get; set; }

        public void Dispose()
        {
            var sourceSet = SourceSet;
            SourceSet = null;
            SourcePreflight = null;
            sourceSet?.Dispose();
        }
    }

    private sealed record WorkbookMaterializationPlan(
        string DocumentName,
        string TemplateWorkbookPath,
        string TargetWorkbookPath,
        IReadOnlyList<VbaProjectReference> DesiredReferences,
        IAdmittedWorkbookGenerationSourceInput SourceInput,
        WorkbookAutomationTimeouts Timeouts,
        bool NormalizeReferences,
        bool GuardExistingTarget,
        CapturedWorkbookTemplate? CapturedTemplate = null,
        VbaProjectSemanticInputs? SemanticInputs = null);

    private static void ThrowIfCanceled(
        CancellationToken cancellationToken,
        WorkbookAutomationStage stage)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new WorkbookAutomationCanceledException(stage, cancellationToken);
        }
    }
}
