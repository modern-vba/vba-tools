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

        var entriesByFile = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddDocumentRepositoryDiagnostics(results, project, documentName, document, entries, entriesByFile,
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
        IReadOnlyDictionary<string, CommonModuleManifestEntry> entriesByFile,
        CapturedDoctorSourceSet? sources)
    {
        var installedByName = document.CommonModules.ToDictionary(
            module => module.Name,
            StringComparer.OrdinalIgnoreCase);
        var resolvedByName = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in document.CommonModules)
        {
            try
            {
                resolvedByName[module.Name] = CommonModulesDependencyResolver.ResolveEntry(entries, module.Name);
            }
            catch (CommonModulesManifestException)
            {
                results.Add(module.Orphaned
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
        }

        var allRequestedRootsResolve = document.CommonModules
            .Where(module => module.Requested)
            .All(module => !module.Orphaned && resolvedByName.ContainsKey(module.Name));

        var reachableDependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in document.CommonModules.Where(module =>
                     module.Requested && !module.Orphaned))
        {
            if (!resolvedByName.TryGetValue(module.Name, out var entry))
            {
                continue;
            }

            reachableDependencyNames.Add(module.Name);
            AddDependencyDiagnostics(
                results,
                documentName,
                module.Name,
                entry,
                entriesByFile,
                installedByName,
                reachableDependencyNames,
                [],
                []);
        }

        var sourceSetPath = project.ResolvePath(document.SourcePath);
        foreach (var module in document.CommonModules)
        {
            if (!resolvedByName.TryGetValue(module.Name, out var entry))
            {
                continue;
            }

            if (module.Orphaned)
            {
                results.Add(DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "orphanState"),
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Installed CommonModule '{module.Name}' is marked orphaned, but the same identity is present "
                    + "in the current CommonModulesRepository; run common-module update."));
            }

            if (allRequestedRootsResolve
                && !module.Requested
                && !reachableDependencyNames.Contains(module.Name))
            {
                results.Add(DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "reachability"),
                    $"CommonModules ({documentName}/{module.Name})",
                    "Installed dependency entry is unreachable from requested CommonModules roots."));
            }

            AddSourceDriftDiagnostic(results, documentName, module, sourceSetPath, project.CommonModulesRepositoryPath!, entry, sources);
        }
    }

    private static void AddDependencyDiagnostics(
        List<DiagnosticResult> results,
        string documentName,
        string rootName,
        CommonModuleManifestEntry entry,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> entriesByFile,
        IReadOnlyDictionary<string, InstalledCommonModule> installedByName,
        HashSet<string> reachableDependencyNames,
        HashSet<string> visiting,
        HashSet<string> reportedMissingDependencyNames)
    {
        if (!visiting.Add(entry.ModuleFile))
        {
            return;
        }

        foreach (var dependency in entry.Dependencies)
        {
            if (!entriesByFile.TryGetValue(dependency, out var dependencyEntry))
            {
                continue;
            }

            var dependencyName = Path.GetFileNameWithoutExtension(dependencyEntry.ModuleFile);
            reachableDependencyNames.Add(dependencyName);
            if (!installedByName.ContainsKey(dependencyName) &&
                reportedMissingDependencyNames.Add(dependencyName))
            {
                results.Add(DiagnosticResult.Fail(
                    CommonModulesCheckId(
                        documentName,
                        rootName,
                        $"dependency.{Uri.EscapeDataString(dependencyName)}"),
                    $"CommonModules ({documentName}/{rootName})",
                    $"Requested CommonModule '{rootName}' requires missing dependency '{dependencyName}'."));
            }

            AddDependencyDiagnostics(
                results,
                documentName,
                rootName,
                dependencyEntry,
                entriesByFile,
                installedByName,
                reachableDependencyNames,
                visiting,
                reportedMissingDependencyNames);
        }

        visiting.Remove(entry.ModuleFile);
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
