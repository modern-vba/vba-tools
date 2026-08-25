using VbaDev.App.Build;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Verifies disposable copies of project templates in dedicated owned Excel processes.
/// </summary>
public sealed class ExcelProjectMaterializationDiagnosticPort
    : IProjectMaterializationDiagnosticPort
{
    private readonly IWorkbookGenerationAutomation workbookAutomation;
    private readonly Func<string, string> stageTemplateWorkbook;
    private readonly Action<string> deleteStagedWorkbook;
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly VbeImportSourceSetFactory importSourceSetFactory;
    private readonly WorkbookMaterializationNamePreflight namePreflight;

    /// <summary>
    /// Creates the production project materialization adapter.
    /// </summary>
    public ExcelProjectMaterializationDiagnosticPort()
        : this(
            new ExcelComWorkbookBuildAutomation(),
            StageTemplateWorkbook,
            DeleteStagedWorkbook,
            new WorkbookSourcePlanner(),
            CreateProductionReferenceNormalizer(),
            new VbeImportSourceSetFactory(),
            new WorkbookMaterializationNamePreflight())
    {
    }

    internal ExcelProjectMaterializationDiagnosticPort(
        IWorkbookGenerationAutomation workbookAutomation,
        Func<string, string> stageTemplateWorkbook,
        Action<string> deleteStagedWorkbook)
        : this(
            workbookAutomation,
            stageTemplateWorkbook,
            deleteStagedWorkbook,
            new WorkbookSourcePlanner(),
            CreateProductionReferenceNormalizer(),
            new VbeImportSourceSetFactory(),
            new WorkbookMaterializationNamePreflight())
    {
    }

    internal ExcelProjectMaterializationDiagnosticPort(
        IWorkbookGenerationAutomation workbookAutomation,
        Func<string, string> stageTemplateWorkbook,
        Action<string> deleteStagedWorkbook,
        WorkbookSourcePlanner sourcePlanner,
        WorkbookReferenceNormalizer referenceNormalizer,
        VbeImportSourceSetFactory importSourceSetFactory,
        WorkbookMaterializationNamePreflight namePreflight)
    {
        this.workbookAutomation = workbookAutomation;
        this.stageTemplateWorkbook = stageTemplateWorkbook;
        this.deleteStagedWorkbook = deleteStagedWorkbook;
        this.sourcePlanner = sourcePlanner;
        this.referenceNormalizer = referenceNormalizer;
        this.importSourceSetFactory = importSourceSetFactory;
        this.namePreflight = namePreflight;
    }

    /// <inheritdoc />
    public async Task<ProjectMaterializationDiagnosticRun> RunAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        foreach (var (documentName, document) in project.Manifest.Documents
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var context = CreateContext(project, documentName, document);
            var profiles = PrepareProfiles(context, cancellationToken);
            var templatePath = project.ResolvePath(document.TemplatePath);
            if (!File.Exists(templatePath))
            {
                foreach (var profile in profiles)
                {
                    profile.Result ??= DiagnosticResult.Skip(
                            profile.CheckId,
                            profile.CheckName,
                            $"The source template does not exist: {templatePath}.");
                    results.Add(profile.Result);
                }

                CleanupDocument(stagedWorkbookPath: null, profiles);
                continue;
            }

            string? stagedWorkbookPath = null;
            try
            {
                var activeProfiles = profiles
                    .Where(profile => profile.SourceSet is not null)
                    .ToArray();
                if (activeProfiles.Length > 0)
                {
                    stagedWorkbookPath = stageTemplateWorkbook(templatePath);
                    await workbookAutomation.RunAsync(
                        stagedWorkbookPath,
                        WorkbookAutomationTimeouts.Default,
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
                            var desiredReferenceNames = document.References
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
                                if (!initialLivePreflight.HasFailures)
                                {
                                    continue;
                                }

                                try
                                {
                                    namePreflight.ThrowIfFailed(
                                        profile.SourcePreflight!,
                                        initialLivePreflight);
                                }
                                catch (InvalidOperationException exception)
                                {
                                    profile.Result = DiagnosticResult.Fail(
                                        profile.CheckId,
                                        profile.CheckName,
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
                                    documentName,
                                    document.References,
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
                                try
                                {
                                    var livePreflight = namePreflight.InspectLivePhase(
                                        profile.SourceSet!.SourceFiles,
                                        finalModules,
                                        finalProjectName,
                                        finalReferences);
                                    namePreflight.ThrowIfFailed(
                                        profile.SourcePreflight!,
                                        livePreflight);
                                    profile.Result = DiagnosticResult.Pass(
                                        profile.CheckId,
                                        profile.CheckName,
                                        "The profile is conflict-free on a disposable template copy.");
                                }
                                catch (InvalidOperationException exception)
                                {
                                    profile.Result = DiagnosticResult.Fail(
                                        profile.CheckId,
                                        profile.CheckName,
                                        exception.Message);
                                }
                            }

                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (WorkbookAutomationTimeoutException exception)
            {
                SetActiveProfileResults(
                    profiles,
                    profile => DiagnosticResult.Unverified(
                        profile.CheckId,
                        profile.CheckName,
                        exception.Message));
            }
            catch (WorkbookAutomationCanceledException exception)
            {
                SetActiveProfileResults(
                    profiles,
                    profile => DiagnosticResult.Unverified(
                        profile.CheckId,
                        profile.CheckName,
                        exception.Message));
                AddProfileResults(results, profiles);
                return new ProjectMaterializationDiagnosticRun(
                    results,
                    Complete: false,
                    Canceled: true);
            }
            catch (WorkbookAutomationCleanupException exception)
            {
                SetActiveProfileResults(
                    profiles,
                    profile => DiagnosticResult.Unverified(
                        profile.CheckId,
                        profile.CheckName,
                        exception.Message));
                AddProfileResults(results, profiles);
                return new ProjectMaterializationDiagnosticRun(
                    results,
                    Complete: false);
            }
            catch (Exception exception)
            {
                SetActiveProfileResults(
                    profiles,
                    profile => DiagnosticResult.Fail(
                        profile.CheckId,
                        profile.CheckName,
                        $"The disposable template could not be materialized: {exception.Message}"));
            }
            finally
            {
                CleanupDocument(stagedWorkbookPath, profiles);
            }

            AddProfileResults(results, profiles);
        }

        return new ProjectMaterializationDiagnosticRun(results);
    }

    private void CleanupDocument(
        string? stagedWorkbookPath,
        IReadOnlyList<ProfileEvaluation> profiles)
    {
        var cleanupErrors = new List<Exception>();
        foreach (var profile in profiles)
        {
            try
            {
                profile.Dispose();
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
        }

        if (stagedWorkbookPath is not null)
        {
            try
            {
                deleteStagedWorkbook(stagedWorkbookPath);
            }
            catch (Exception cleanupError)
            {
                cleanupErrors.Add(cleanupError);
            }
        }

        if (cleanupErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "Workbook materialization temporary resources could not be fully removed.",
                new AggregateException(cleanupErrors));
        }
    }

    private ProfileEvaluation[] PrepareProfiles(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        var profiles = new List<ProfileEvaluation>();
        try
        {
            profiles.Add(PrepareProfile(
                context,
                "build",
                sourcePlanner.ResolveBuildSourceFilesForPreflight,
                cancellationToken));
            profiles.Add(PrepareProfile(
                context,
                "publish",
                sourcePlanner.ResolvePublishSourceFilesForPreflight,
                cancellationToken));
            return profiles.ToArray();
        }
        catch (Exception preparationError)
        {
            var cleanupErrors = new List<Exception>();
            foreach (var profile in profiles)
            {
                try
                {
                    profile.Dispose();
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
            }

            if (cleanupErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Workbook materialization profile preparation failed and its temporary sources could not be fully removed.",
                    new AggregateException([preparationError, .. cleanupErrors]));
            }

            throw;
        }
    }

    private ProfileEvaluation PrepareProfile(
        ResolvedProjectContext context,
        string profileName,
        Func<ResolvedProjectContext, IReadOnlyList<VbaSourceFile>> resolveSources,
        CancellationToken cancellationToken)
    {
        var profile = new ProfileEvaluation(context.DocumentName, profileName);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            profile.SourceSet = importSourceSetFactory.Create(resolveSources(context));
            profile.SourcePreflight = namePreflight.InspectSourcePhase(
                profile.SourceSet.SourceFiles);
            if (profile.SourcePreflight.HasFailures)
            {
                namePreflight.ThrowIfFailed(profile.SourcePreflight);
            }
        }
        catch (OperationCanceledException)
        {
            profile.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            profile.Dispose();
            profile.Result = DiagnosticResult.Fail(
                profile.CheckId,
                profile.CheckName,
                exception.Message);
        }

        return profile;
    }

    private static void SetActiveProfileResults(
        IReadOnlyList<ProfileEvaluation> profiles,
        Func<ProfileEvaluation, DiagnosticResult> createResult)
    {
        foreach (var profile in profiles.Where(profile =>
                     profile.SourceSet is not null && profile.Result is null))
        {
            profile.Result = createResult(profile);
        }
    }

    private static void AddProfileResults(
        List<DiagnosticResult> results,
        IReadOnlyList<ProfileEvaluation> profiles)
    {
        foreach (var profile in profiles)
        {
            results.Add(profile.Result ?? DiagnosticResult.Fail(
                profile.CheckId,
                profile.CheckName,
                "Workbook materialization diagnostics returned incomplete profile evidence."));
        }
    }

    private static ResolvedProjectContext CreateContext(
        ResolvedProject project,
        string documentName,
        VbaDev.Domain.ProjectDocument document)
        => new(
            project.ProjectRoot,
            project.ManifestPath,
            project.Manifest,
            documentName,
            document,
            project.ResolvePath(document.SourcePath),
            project.ResolvePath(document.TemplatePath),
            project.ResolvePath(document.BinPath),
            project.ResolvePath(document.PublishPath),
            project.CommonModulesRepositoryPath);

    private static WorkbookReferenceNormalizer CreateProductionReferenceNormalizer()
        => new(new VbaProjectReferencePlanner(
            new RegistryVbaProjectReferenceResolver(),
            new VbaProjectReferenceAmbiguityProbe(
                new ExcelComVbaProjectReferenceProbeAutomation())));

    private static string StageTemplateWorkbook(string templatePath)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-doctor-{Guid.NewGuid():N}");
        return StageTemplateWorkbook(templatePath, directory);
    }

    internal static string StageTemplateWorkbook(
        string templatePath,
        string directory)
    {
        var stagedWorkbookPath = Path.Combine(
            directory,
            Path.GetFileName(templatePath));
        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(templatePath, stagedWorkbookPath);
            return stagedWorkbookPath;
        }
        catch (Exception stagingError)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception cleanupError)
            {
                throw new InvalidOperationException(
                    $"{stagingError.Message} The failed Doctor staging directory could not be removed: '{directory}'.",
                    new AggregateException(stagingError, cleanupError));
            }

            throw;
        }
    }

    private static void DeleteStagedWorkbook(string stagedWorkbookPath)
    {
        if (File.Exists(stagedWorkbookPath))
        {
            File.Delete(stagedWorkbookPath);
        }

        var directory = Path.GetDirectoryName(stagedWorkbookPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory);
        }
    }

    private sealed class ProfileEvaluation : IDisposable
    {
        public ProfileEvaluation(string documentName, string profileName)
        {
            CheckId = $"project.workbookMaterialization/{documentName}/{profileName}";
            CheckName = $"Workbook materialization ({documentName}/{profileName})";
        }

        public string CheckId { get; }

        public string CheckName { get; }

        public VbeImportSourceSet? SourceSet { get; set; }

        public WorkbookMaterializationNamePreflightReport? SourcePreflight { get; set; }

        public DiagnosticResult? Result { get; set; }

        public void Dispose()
        {
            SourceSet?.Dispose();
            SourceSet = null;
            SourcePreflight = null;
        }
    }
}
