using VbaDev.App.Build;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Composition;

/// <summary>
/// Wires VbaDev application services to their default infrastructure adapters.
/// </summary>
public static class ToolingCompositionRoot
{
    /// <summary>
    /// Creates the shell-neutral application services for the current working directory.
    /// </summary>
    /// <returns>The composed services consumed by a command-line host.</returns>
    public static ToolingApplicationComposition CreateApplicationComposition()
        => CreateApplicationComposition(
            Directory.GetCurrentDirectory(),
            environmentDiagnosticPort: new ExcelEnvironmentDiagnosticPort(),
            projectMaterializationDiagnosticPort:
                new ExcelProjectMaterializationDiagnosticPort());

    /// <summary>
    /// Creates shell-neutral application services with optional test or host-specific adapter overrides.
    /// </summary>
    /// <param name="workingDirectory">The working directory used by path and project resolution.</param>
    /// <param name="environmentDiagnosticPort">The optional environment diagnostics adapter.</param>
    /// <param name="initialWorkbookCreator">The optional initial workbook creator adapter.</param>
    /// <param name="workbookBuildAutomation">The optional workbook build automation adapter.</param>
    /// <param name="workbookTestRunner">The optional workbook test runner adapter.</param>
    /// <param name="workbookModuleExporter">The optional workbook module exporter adapter.</param>
    /// <param name="vbaProjectReferenceResolver">The optional VBA project reference resolver adapter.</param>
    /// <param name="projectManifestStore">The optional project manifest persistence adapter.</param>
    /// <param name="exportDestinationFileOperations">The optional recoverable export filesystem adapter.</param>
    /// <param name="projectManifestMutationCoordinator">The optional rebased manifest mutation boundary.</param>
    /// <returns>The composed services consumed by a command-line host.</returns>
    public static ToolingApplicationComposition CreateApplicationComposition(
        string workingDirectory,
        IEnvironmentDiagnosticPort? environmentDiagnosticPort = null,
        IInitialWorkbookCreator? initialWorkbookCreator = null,
        IWorkbookBuildAutomation? workbookBuildAutomation = null,
        IWorkbookTestRunner? workbookTestRunner = null,
        IWorkbookModuleExporter? workbookModuleExporter = null,
        IVbaProjectReferenceResolver? vbaProjectReferenceResolver = null,
        IProjectManifestStore? projectManifestStore = null,
        IVbaProjectReferenceAmbiguityProbe? vbaProjectReferenceAmbiguityProbe = null,
        IExportDestinationFileOperations? exportDestinationFileOperations = null,
        IProjectMaterializationDiagnosticPort? projectMaterializationDiagnosticPort = null,
        IProjectManifestMutationCoordinator? projectManifestMutationCoordinator = null)
    {
        var atomicManifestWriter = new ProjectManifestAtomicWriter();
        var manifestStore = projectManifestStore
                            ?? new JsonProjectManifestStore(atomicManifestWriter);
        var manifestEditor = new ProjectManifestEditor(
            manifestStore,
            atomicManifestWriter);
        var mutationCoordinator = projectManifestMutationCoordinator
                                  ?? new ProjectManifestMutationCoordinator(
                                      atomicManifestWriter,
                                      new ProjectManifestMutationLeaseProvider());
        var commonModulesManifestReader = new CommonModulesManifestReader();
        var commonModulesInstallationTransaction = new CommonModulesInstallationTransaction(commonModulesManifestReader, manifestEditor);
        var commonModulesService = new CommonModulesService(commonModulesInstallationTransaction);
        var referenceResolver = vbaProjectReferenceResolver ?? new RegistryVbaProjectReferenceResolver();
        var ambiguityProbe = vbaProjectReferenceAmbiguityProbe
                             ?? (vbaProjectReferenceResolver is null
                                 ? new VbaProjectReferenceAmbiguityProbe(
                                     new ExcelComVbaProjectReferenceProbeAutomation())
                                 : null);
        var referencePlanner = new VbaProjectReferencePlanner(
            referenceResolver,
            ambiguityProbe);
        var referenceService = new VbaProjectReferenceService(
            referencePlanner,
            mutationCoordinator);
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var referenceCompletionService = new VbaProjectReferenceCompletionService(
            projectContextResolver,
            referencePlanner);
        var buildAutomation = workbookBuildAutomation ?? new ExcelComWorkbookBuildAutomation();
        IReadOnlyList<IDoctorProjectDiagnosticProvider> staticProjectDiagnosticProviders =
        [
            new ProjectConfigurationDiagnosticProvider(),
            new CommonModulesDiagnosticProvider(commonModulesManifestReader),
            new CommandDefaultsDiagnosticProvider()
        ];
        var staticProjectCheckCommand = new StaticProjectCheckCommand(
            projectContextResolver,
            staticProjectDiagnosticProviders);
        var doctorPipeline = new DoctorDiagnosticPipeline(
            projectContextResolver,
            staticProjectDiagnosticProviders,
            [new VbaProjectReferenceDiagnosticProvider(referencePlanner)],
            projectMaterializationDiagnosticPort ??
                new DisabledProjectMaterializationDiagnosticPort(),
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        var doctorCommand = new DoctorCommand(
            doctorPipeline,
            new DoctorReportRenderer());
        var newProjectCommand = new NewProjectCommand(
            manifestStore,
            initialWorkbookCreator ?? new ExcelComInitialWorkbookCreator(),
            commonModulesManifestReader);
        var sourcePlanner = new WorkbookSourcePlanner();
        var generationPipeline = CreateWorkbookGenerationPipeline(
            buildAutomation,
            new WorkbookReferenceNormalizer(referencePlanner));
        var workbookOutputCommand = new WorkbookOutputCommand(sourcePlanner, generationPipeline);
        var buildCommand = new BuildCommand(workbookOutputCommand);
        var publishCommand = new PublishCommand(workbookOutputCommand);
        var testCommand = new TestCommand(
            buildCommand,
            workbookTestRunner ?? new ExcelComWorkbookTestRunner(),
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator());
        var exportCommand = new ExportCommand(
            workbookModuleExporter ?? new ExcelComWorkbookModuleExporter(),
            exportDestinationFileOperations ?? new ExportDestinationFileOperations());
        var importCommand = new ImportCommand(buildAutomation);
        return new ToolingApplicationComposition(
            doctorCommand,
            staticProjectCheckCommand,
            newProjectCommand,
            commonModulesService,
            referenceService,
            referenceCompletionService,
            buildCommand,
            publishCommand,
            testCommand,
            exportCommand,
            importCommand,
            projectContextResolver,
            workingDirectory);
    }

    private static WorkbookGenerationPipeline CreateWorkbookGenerationPipeline(
        IWorkbookBuildAutomation buildAutomation,
        WorkbookReferenceNormalizer referenceNormalizer)
        => buildAutomation is IWorkbookGenerationAutomation generationAutomation
            ? new WorkbookGenerationPipeline(generationAutomation, referenceNormalizer)
            : new WorkbookGenerationPipeline(buildAutomation, referenceNormalizer);
}

/// <summary>
/// Contains shell-neutral application services used by an executable command-line host.
/// </summary>
/// <param name="DoctorCommand">The diagnostics command.</param>
/// <param name="StaticProjectCheckCommand">The Excel-free project check command.</param>
/// <param name="NewProjectCommand">The project creation command.</param>
/// <param name="CommonModulesService">The CommonModules service.</param>
/// <param name="ReferenceService">The VBA reference service.</param>
/// <param name="ReferenceCompletionService">The quiet reference-name completion service.</param>
/// <param name="BuildCommand">The workbook build command.</param>
/// <param name="PublishCommand">The workbook publish command.</param>
/// <param name="TestCommand">The workbook test command.</param>
/// <param name="ExportCommand">The workbook export command.</param>
/// <param name="ImportCommand">The workbook import command.</param>
/// <param name="ProjectContextResolver">The project and document context resolver.</param>
/// <param name="WorkingDirectory">The invocation working directory.</param>
public sealed record ToolingApplicationComposition(
    DoctorCommand DoctorCommand,
    StaticProjectCheckCommand StaticProjectCheckCommand,
    NewProjectCommand NewProjectCommand,
    CommonModulesService CommonModulesService,
    VbaProjectReferenceService ReferenceService,
    VbaProjectReferenceCompletionService ReferenceCompletionService,
    BuildCommand BuildCommand,
    PublishCommand PublishCommand,
    TestCommand TestCommand,
    ExportCommand ExportCommand,
    ImportCommand ImportCommand,
    ProjectContextResolver ProjectContextResolver,
    string WorkingDirectory);
