using VbaDev.Infrastructure.FileSystem;
using VbaDev.App.Build;
using VbaDev.App.DebugPreparation;
using VbaDev.App.CommonModules;
using VbaDev.App.Diagnostics;
using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.HostEvents;
using VbaDev.App.Import;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using VbaDev.Infrastructure.Workbooks;
using VbaDev.Infrastructure.References;
using VbaTools.TypeLibRegistry;

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
            environmentDiagnosticPort: new ExcelEnvironmentDiagnosticPort());

    /// <summary>
    /// Creates shell-neutral application services with optional test or host-specific adapter overrides.
    /// </summary>
    /// <param name="workingDirectory">The working directory used by path and project resolution.</param>
    /// <param name="environmentDiagnosticPort">The optional environment diagnostics adapter.</param>
    /// <param name="initialWorkbookCreator">The optional initial workbook creator adapter.</param>
    /// <param name="workbookGenerationAutomation">The optional workbook generation automation adapter.</param>
    /// <param name="sourceWorkbookAutomation">The optional source-workbook automation adapter.</param>
    /// <param name="workbookTestRunner">The optional workbook test runner adapter.</param>
    /// <param name="workbookModuleExporter">The optional workbook module exporter adapter.</param>
    /// <param name="projectWorkbookModuleExporter">The optional project-source workbook exporter adapter.</param>
    /// <param name="vbaProjectReferenceResolver">The optional VBA project reference resolver adapter.</param>
    /// <param name="projectManifestStore">The optional project manifest persistence adapter.</param>
    /// <param name="exportDestinationFileOperations">The optional recoverable export filesystem adapter.</param>
    /// <param name="projectManifestMutationCoordinator">The optional rebased manifest mutation boundary.</param>
    /// <param name="projectManifestMutationLeaseProvider">The optional shared project mutation lease provider.</param>
    /// <param name="persistSourceAnalysisFailureEvidence">Whether to retain local failure evidence; tests can disable persistence.</param>
    /// <param name="sourceWorkbookRecoveryBudget">The optional finite overall budget for borrowed-workbook recovery.</param>
    /// <param name="sourceWorkbookRecoveryOwnershipFactory">The optional ownership adapter for source Build recovery staging.</param>
    /// <param name="debugSourceWorkbookAutomationFactory">The optional exact borrowed-workbook debug preparation adapter factory.</param>
    /// <returns>The composed services consumed by a command-line host.</returns>
    public static ToolingApplicationComposition CreateApplicationComposition(
        string workingDirectory,
        IEnvironmentDiagnosticPort? environmentDiagnosticPort = null,
        IInitialWorkbookCreator? initialWorkbookCreator = null,
        IWorkbookGenerationAutomation? workbookGenerationAutomation = null,
        IWorkbookTestRunner? workbookTestRunner = null,
        IWorkbookModuleExporter? workbookModuleExporter = null,
        IVbaProjectReferenceResolver? vbaProjectReferenceResolver = null,
        IProjectManifestStore? projectManifestStore = null,
        IVbaProjectReferenceAmbiguityProbe? vbaProjectReferenceAmbiguityProbe = null,
        IExportDestinationFileOperations? exportDestinationFileOperations = null,
        IProjectMaterializationDiagnosticPort? projectMaterializationDiagnosticPort = null,
        IProjectManifestMutationCoordinator? projectManifestMutationCoordinator = null,
        IProjectManifestMutationLeaseProvider? projectManifestMutationLeaseProvider = null,
        IHostEventCatalogAutomation? hostEventCatalogAutomation = null,
        IProjectSemanticInputProvider? projectSemanticInputProvider = null,
        ITypeLibRegistryCatalogReader? typeLibRegistryCatalogReader = null,
        ITypeLibCatalogMetadataReader? typeLibCatalogMetadataReader = null,
        IOfficeClickToRunTypeLibEvidenceReader? officeClickToRunTypeLibEvidenceReader = null,
        IWorkbookProjectIdentityProbe? workbookProjectIdentityProbe = null,
        bool persistSourceAnalysisFailureEvidence = true,
        ISourceWorkbookAutomation? sourceWorkbookAutomation = null,
        IWorkbookModuleExporter? projectWorkbookModuleExporter = null,
        TimeSpan? sourceWorkbookRecoveryBudget = null,
        IExactFileSystemObjectOwnershipFactory? sourceWorkbookRecoveryOwnershipFactory = null,
        Func<int, long, ISourceWorkbookAutomation>? debugSourceWorkbookAutomationFactory = null,
        ISourceWorkbookAutomation? testSourceWorkbookAutomation = null)
    {
        var ownershipFactory = new WindowsExactFileSystemObjectOwnershipFactory();
        var pathIdentityResolver = new FileSystemPathIdentityResolver();
        var atomicManifestWriter = new ProjectManifestAtomicWriter();
        var manifestStore = projectManifestStore
                            ?? new JsonProjectManifestStore(atomicManifestWriter);
        var manifestEditor = new ProjectManifestEditor(atomicManifestWriter);
        var mutationLeaseProvider = projectManifestMutationLeaseProvider
                                    ?? new ProjectManifestMutationLeaseProvider();
        var mutationCoordinator = projectManifestMutationCoordinator
                                  ?? new ProjectManifestMutationCoordinator(
                                      atomicManifestWriter,
                                      mutationLeaseProvider);
        var registrySnapshot = new TypeLibRegistryCatalogSnapshot(
            typeLibRegistryCatalogReader ?? new RegistryTypeLibRegistryCatalogReader());
        var referenceResolver = vbaProjectReferenceResolver ?? new RegistryVbaProjectReferenceResolver(registrySnapshot);
        var ambiguityProbe = vbaProjectReferenceAmbiguityProbe
                             ?? (vbaProjectReferenceResolver is null
                                 ? new VbaProjectReferenceAmbiguityProbe(
                                     new ExcelComVbaProjectReferenceProbeAutomation())
                                 : null);
        var referencePlanner = new VbaProjectReferencePlanner(
            referenceResolver,
            ambiguityProbe);
        var commonModulesManifestReader = new CommonModulesManifestReader();
        var commonModulesInstallationTransaction = new CommonModulesInstallationTransaction(
            ownershipFactory,
            commonModulesManifestReader,
            manifestEditor,
            referencePlanner,
            mutationCoordinator,
            pathIdentityResolver);
        var commonModulesService = new CommonModulesService(commonModulesInstallationTransaction);
        var referenceService = new VbaProjectReferenceService(
            referencePlanner,
            mutationCoordinator,
            pathIdentityResolver);
        var projectContextResolver = new ProjectContextResolver(manifestStore);
        var referenceCompletionService = new VbaProjectReferenceCompletionService(
            projectContextResolver,
            referencePlanner);
        var generationAutomation = workbookGenerationAutomation ?? new ExcelComWorkbookGenerationAutomation();
        var sourceAutomation = sourceWorkbookAutomation ?? new ExcelComSourceWorkbookAutomation();
        var hostEventAutomation = hostEventCatalogAutomation ?? new ExcelComHostEventCatalogAutomation();
        var sourceAdmission = new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get);
        var referenceNormalizer = new WorkbookReferenceNormalizer(referencePlanner);
        var materializer = new WorkbookMaterializer(ownershipFactory,
            sourceAdmission,
            generationAutomation,
            referenceNormalizer,
            new WorkbookOutputTransactionFactory(ownershipFactory),
            new VbeImportSourceSetFactory(ownershipFactory),
            semanticInputProvider: projectSemanticInputProvider ?? new ProjectSemanticInputProvider(
                referencePlanner, registrySnapshot, typeLibCatalogMetadataReader ?? new ComTypeLibCatalogMetadataReader(),
                officeClickToRunTypeLibEvidenceReader ?? new RegistryOfficeClickToRunTypeLibEvidenceReader(),
                hostEventAutomation, workbookProjectIdentityProbe ?? new WorkbookProjectIdentityProbe(
                    ownershipFactory, new ExcelComWorkbookGenerationAutomation())));
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
                new ExcelProjectMaterializationDiagnosticPort(materializer),
            environmentDiagnosticPort ?? new SkippedEnvironmentDiagnosticPort());
        var doctorCommand = new DoctorCommand(
            doctorPipeline,
            new DoctorReportRenderer());
        var newProjectCommand = new NewProjectCommand(
            ownershipFactory,
            manifestStore,
            initialWorkbookCreator ?? new ExcelComInitialWorkbookCreator(),
            commonModulesManifestReader,
            referencePlanner,
            mutationLeaseProvider,
            pathIdentityResolver);
        var workbookOutputCommand = new WorkbookOutputCommand(materializer,
            persistSourceAnalysisFailureEvidence ? new SourceAnalysisEvidenceStore().Save : null);
        var sourceTestCommand = new SourceWorkbookTestCommand(materializer,
            testSourceWorkbookAutomation ?? sourceWorkbookAutomation
                ?? ExcelComSourceWorkbookAutomation.CreateForTestExecution(),
            referenceNormalizer, sourceWorkbookRecoveryOwnershipFactory ?? ownershipFactory,
            sourceWorkbookRecoveryBudget,
            saveFailureEvidence: persistSourceAnalysisFailureEvidence ? new SourceAnalysisEvidenceStore().Save : null);
        var buildCommand = new BuildCommand(workbookOutputCommand, pathIdentityResolver,
            ownershipFactory, new SourceWorkbookBuildCommand(materializer, sourceAutomation,
                referenceNormalizer, sourceWorkbookRecoveryOwnershipFactory ?? ownershipFactory,
                sourceWorkbookRecoveryBudget), sourceTestCommand);
        var debugPreparationCommand = new DebugWorkbookPreparationCommand(
            materializer,
            debugSourceWorkbookAutomationFactory
                ?? ((processId, startTicks) => new ExcelComDebugSourceWorkbookAutomation(processId, startTicks)),
            referenceNormalizer,
            sourceWorkbookRecoveryOwnershipFactory ?? ownershipFactory,
            sourceWorkbookRecoveryBudget);
        var publishCommand = new PublishCommand(workbookOutputCommand);
        var testCommand = new TestCommand(
            buildCommand,
            workbookTestRunner ?? new ExcelComWorkbookTestRunner(),
            new TestResultOutputFormatter(),
            new TestProcedureSourceLocator(),
            new SnapshotTestExecutionWorkspaceFactory(ownershipFactory, pathIdentityResolver),
            sourceTestCommand);
        var exportCommand = new ExportCommand(ownershipFactory,
            workbookModuleExporter ?? new ExcelComWorkbookModuleExporter(),
            projectWorkbookModuleExporter ?? workbookModuleExporter
                ?? new SourceWorkbookModuleExporter(sourceAutomation),
            exportDestinationFileOperations ?? new ExportDestinationFileOperations());
        var importCommand = new ImportCommand(
            materializer,
            sourceAdmission);
        var hostEventListCommand = new HostEventListCommand(hostEventAutomation);
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
            hostEventListCommand,
            projectContextResolver,
            workingDirectory,
            debugPreparationCommand);
    }

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
/// <param name="HostEventListCommand">The generic intrinsic UserForm Event catalog command.</param>
/// <param name="ProjectContextResolver">The project and document context resolver.</param>
/// <param name="WorkingDirectory">The invocation working directory.</param>
/// <param name="DebugWorkbookPreparationCommand">The non-saving, exact source-workbook debug preparation command.</param>
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
    HostEventListCommand HostEventListCommand,
    ProjectContextResolver ProjectContextResolver,
    string WorkingDirectory,
    DebugWorkbookPreparationCommand DebugWorkbookPreparationCommand);
