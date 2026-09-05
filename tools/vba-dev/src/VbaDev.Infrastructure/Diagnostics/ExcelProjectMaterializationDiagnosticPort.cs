using VbaDev.App.Build;
using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Diagnostics;

/// <summary>
/// Projects WorkbookMaterializer inspection results into Doctor diagnostics.
/// </summary>
public sealed class ExcelProjectMaterializationDiagnosticPort
    : IProjectMaterializationDiagnosticPort
{
    private readonly WorkbookMaterializer materializer;

    /// <summary>
    /// Creates the production project materialization adapter.
    /// </summary>
    public ExcelProjectMaterializationDiagnosticPort()
        : this(CreateProductionMaterializer())
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
            CreateProductionReferenceNormalizer(),
            new VbeImportSourceSetFactory(),
            new WorkbookMaterializationNamePreflight())
    {
    }

    internal ExcelProjectMaterializationDiagnosticPort(
        IWorkbookGenerationAutomation workbookAutomation,
        Func<string, string> stageTemplateWorkbook,
        Action<string> deleteStagedWorkbook,
        WorkbookReferenceNormalizer referenceNormalizer,
        VbeImportSourceSetFactory importSourceSetFactory,
        WorkbookMaterializationNamePreflight namePreflight)
        : this(new WorkbookMaterializer(
            new WorkbookSourcePlanner(),
            workbookAutomation,
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(),
            importSourceSetFactory,
            inspectionWorkbookStager: stageTemplateWorkbook,
            inspectionWorkbookDeleter: deleteStagedWorkbook,
            namePreflight: namePreflight))
    {
    }

    internal ExcelProjectMaterializationDiagnosticPort(WorkbookMaterializer materializer)
    {
        this.materializer = materializer;
    }

    /// <inheritdoc />
    public Task<ProjectMaterializationDiagnosticRun> RunAsync(
        ResolvedProject project,
        DoctorProjectSourceInspection sources,
        CancellationToken cancellationToken)
        => RunWithSourcesAsync(project, sources, cancellationToken);

    private async Task<ProjectMaterializationDiagnosticRun> RunWithSourcesAsync(
        ResolvedProject project,
        DoctorProjectSourceInspection sources,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();
        foreach (var (documentName, document) in project.Manifest.Documents
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var inspection = await materializer.InspectAsync(
                    new ProjectInspectionIntent(
                        CreateContext(project, documentName, document),
                        sources.GetDocument(documentName)),
                    cancellationToken)
                .ConfigureAwait(false);
            results.AddRange(inspection.Profiles.Select(profile =>
                ToDiagnosticResult(documentName, profile)));
            if (!inspection.Complete)
            {
                return new ProjectMaterializationDiagnosticRun(
                    results,
                    Complete: false,
                    inspection.Canceled);
            }
        }

        return new ProjectMaterializationDiagnosticRun(results);
    }

    private static DiagnosticResult ToDiagnosticResult(
        string documentName,
        ProjectInspectionProfileResult profile)
    {
        var profileName = profile.Profile switch
        {
            ProjectInspectionProfile.Build => "build",
            ProjectInspectionProfile.Publish => "publish",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Profile, null)
        };
        var checkId = $"project.workbookMaterialization/{documentName}/{profileName}";
        var checkName = $"Workbook materialization ({documentName}/{profileName})";
        return profile.Status switch
        {
            ProjectInspectionStatus.Pass => DiagnosticResult.Pass(
                checkId,
                checkName,
                profile.Message),
            ProjectInspectionStatus.Fail => DiagnosticResult.Fail(
                checkId,
                checkName,
                profile.Message),
            ProjectInspectionStatus.Unverified => DiagnosticResult.Unverified(
                checkId,
                checkName,
                profile.Message),
            ProjectInspectionStatus.Skip => DiagnosticResult.Skip(
                checkId,
                checkName,
                profile.Message),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Status, null)
        };
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

    private static WorkbookMaterializer CreateProductionMaterializer()
        => new(
            new WorkbookSourcePlanner(),
            new ExcelComWorkbookGenerationAutomation(),
            CreateProductionReferenceNormalizer(),
            new WorkbookOutputTransactionFactory());

    private static WorkbookReferenceNormalizer CreateProductionReferenceNormalizer()
        => new(new VbaProjectReferencePlanner(
            new RegistryVbaProjectReferenceResolver(),
            new VbaProjectReferenceAmbiguityProbe(
                new ExcelComVbaProjectReferenceProbeAutomation())));
}
