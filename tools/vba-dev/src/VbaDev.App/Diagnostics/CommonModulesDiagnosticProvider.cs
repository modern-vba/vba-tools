using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds CommonModules repository, dependency, and source drift diagnostics.
/// </summary>
public sealed class CommonModulesDiagnosticProvider : IDoctorProjectDiagnosticProvider, IDoctorSourceDiagnosticProvider
{
    private readonly CommonModulesPackageReader commonModulesPackageReader;

    /// <summary>
    /// Creates a CommonModules diagnostic provider.
    /// </summary>
    /// <param name="commonModulesManifestReader">The reader used to load the CommonModules manifest.</param>
    public CommonModulesDiagnosticProvider(CommonModulesManifestReader commonModulesManifestReader)
    {
        commonModulesPackageReader = new CommonModulesPackageReader(
            commonModulesManifestReader
            ?? throw new ArgumentNullException(nameof(commonModulesManifestReader)));
    }

    /// <inheritdoc />
    public void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
        => AddDiagnostics(results, project, null);

    void IDoctorSourceDiagnosticProvider.AddDiagnostics(
        List<DiagnosticResult> results, ResolvedProject project, DoctorProjectSourceInspection sources)
        => AddDiagnostics(results, project, sources);

    private void AddDiagnostics(
        List<DiagnosticResult> results, ResolvedProject project, DoctorProjectSourceInspection? sources)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sourceSetPath = project.ResolvePath(document.SourcePath);
            var inventory = sources?.GetInventory(documentName);
            foreach (var module in document.CommonModules)
            {
                AddStoredSourceDiagnostic(results, documentName, module, sourceSetPath, inventory);
            }
        }

        if (project.CommonModulesRepositoryPath is null)
        {
            return;
        }

        IReadOnlyList<CommonModuleManifestEntry> entries;
        try
        {
            entries = commonModulesPackageReader.Load(project.CommonModulesRepositoryPath).Entries;
        }
        catch (CommonModulesManifestException ex)
        {
            results.Add(DiagnosticResult.Fail(
                "project.commonModules.repository",
                "CommonModules repository",
                ex.Message));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            results.Add(DiagnosticResult.Fail(
                "project.commonModules.repository",
                "CommonModules repository",
                $"CommonModulesRepository could not be read: {ex.Message}"));
            return;
        }

        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddDocumentRepositoryDiagnostics(results, project, documentName, document, entries,
                sources?.GetDocument(documentName));
        }
    }

    private static void AddStoredSourceDiagnostic(
        List<DiagnosticResult> results,
        string documentName,
        InstalledCommonModule module,
        string sourceSetPath,
        IReadOnlyList<string>? inventory)
    {
        var sourceMatches = inventory is null
            ? DocumentSourceSetLayout.FindSourceMatches(sourceSetPath, module.ModuleFile)
            : DocumentSourceSetLayout.FindSourceMatches(inventory, module.ModuleFile);
        if (sourceMatches.Count == 0)
        {
            results.Add(DiagnosticResult.Fail(
                CommonModulesCheckId(documentName, module.Name, "storedSource"),
                $"CommonModules ({documentName}/{module.Name})",
                $"Installed CommonModule source file was not found under {sourceSetPath}: {module.ModuleFile}."));
            return;
        }

        if (sourceMatches.Count > 1)
        {
            results.Add(DiagnosticResult.Fail(
                CommonModulesCheckId(documentName, module.Name, "storedSource"),
                $"CommonModules ({documentName}/{module.Name})",
                $"Installed CommonModule has multiple source matches for '{module.ModuleFile}': {string.Join(", ", sourceMatches)}."));
        }
    }

    private static void AddDocumentRepositoryDiagnostics(
        List<DiagnosticResult> results,
        ResolvedProject project,
        string documentName,
        ProjectDocument document,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        CapturedDoctorSourceSet? sources)
    {
        var reconciliation = CommonModulesReconciliation.Create(entries, document.CommonModules);
        foreach (var fact in reconciliation.Installed.Where(fact => fact.RepositoryEntry is null))
        {
            var module = fact.Installed;
            results.Add(fact.State == CommonModuleRepositoryState.RetainedOrphan
                ? DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "orphaned"),
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Installed CommonModule '{module.Name}' is a retained orphan; "
                    + "its identity is absent from the current CommonModulesRepository.")
                : DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "orphanState"),
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Installed CommonModule '{module.Name}' is absent from the current CommonModulesRepository "
                    + "but is not marked orphaned; run common-module update."));
        }

        foreach (var missing in reconciliation.MissingDependencies)
        {
            results.Add(DiagnosticResult.Fail(
                CommonModulesCheckId(documentName, missing.RootName,
                    $"dependency.{Uri.EscapeDataString(missing.DependencyName)}"),
                $"CommonModules ({documentName}/{missing.RootName})",
                $"Requested CommonModule '{missing.RootName}' requires missing dependency '{missing.DependencyName}'."));
        }

        var sourceSetPath = project.ResolvePath(document.SourcePath);
        foreach (var fact in reconciliation.Installed)
        {
            if (fact.RepositoryEntry is not { } entry)
            {
                continue;
            }
            var module = fact.Installed;
            if (fact.State == CommonModuleRepositoryState.StaleOrphanMarker)
            {
                results.Add(DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "orphanState"),
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Installed CommonModule '{module.Name}' is marked orphaned, but the same identity is present "
                    + "in the current CommonModulesRepository; run common-module update."));
            }
            if (fact.IsUnreachableDependency)
            {
                results.Add(DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "reachability"),
                    $"CommonModules ({documentName}/{module.Name})",
                    "Installed dependency entry is unreachable from requested CommonModules roots."));
            }
            AddSourceDriftDiagnostic(results, documentName, module, sourceSetPath, project.CommonModulesRepositoryPath!, entry, sources);
        }
    }

    private static void AddSourceDriftDiagnostic(
        List<DiagnosticResult> results,
        string documentName,
        InstalledCommonModule module,
        string sourceSetPath,
        string commonModulesRepositoryPath,
        CommonModuleManifestEntry entry,
        CapturedDoctorSourceSet? sources)
    {
        var sourceMatches = sources is null
            ? DocumentSourceSetLayout.FindSourceMatches(sourceSetPath, module.ModuleFile)
            : DocumentSourceSetLayout.FindSourceMatches(sources.InventoryPaths, module.ModuleFile);
        var repositoryPath = Path.Combine(commonModulesRepositoryPath, entry.ModuleFile);
        if (sourceMatches.Count != 1)
        {
            return;
        }

        if (!File.Exists(repositoryPath))
        {
            results.Add(DiagnosticResult.Fail(
                CommonModulesCheckId(documentName, module.Name, "repositorySource"),
                $"CommonModules ({documentName}/{module.Name})",
                $"CommonModulesRepository source file was not found: {repositoryPath}."));
            return;
        }

        var sourcePath = sourceMatches[0];
        var sourceSidecarPath = sources is null
            ? DocumentSourceSetLayout.ResolveExistingSidecarPath(sourcePath)
            : DocumentSourceSetLayout.ResolveExistingSidecarPath(sourcePath, sources.InventoryPaths);
        var repositorySidecarPath = DocumentSourceSetLayout.ResolveExistingSidecarPath(repositoryPath);
        var hasDifferentFormSidecar = false;
        if (DocumentSourceSetLayout.IsFormFile(sourcePath) &&
            DocumentSourceSetLayout.IsFormFile(repositoryPath))
        {
            hasDifferentFormSidecar = sourceSidecarPath is null || repositorySidecarPath is null
                ? sourceSidecarPath != repositorySidecarPath
                : !ReadDocumentBytes(sourceSidecarPath, sources).SequenceEqual(File.ReadAllBytes(repositorySidecarPath));
        }

        if (!ReadDocumentBytes(sourcePath, sources).SequenceEqual(File.ReadAllBytes(repositoryPath)) ||
            hasDifferentFormSidecar)
        {
            results.Add(DiagnosticResult.Warn(
                CommonModulesCheckId(documentName, module.Name, "repositorySource"),
                $"CommonModules ({documentName}/{module.Name})",
                $"Source file differs from CommonModulesRepository: {sourcePath}."));
        }
    }

    private static IReadOnlyList<byte> ReadDocumentBytes(string path, CapturedDoctorSourceSet? sources)
        => sources is null ? File.ReadAllBytes(path) : sources.GetOriginalBytes(path);

    private static string CommonModulesCheckId(
        string documentName,
        string moduleName,
        string finding)
        => $"project.commonModules.{Uri.EscapeDataString(documentName)}." +
           $"{Uri.EscapeDataString(moduleName)}.{finding}";
}
