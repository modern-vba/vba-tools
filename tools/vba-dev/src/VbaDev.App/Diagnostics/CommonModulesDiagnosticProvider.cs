using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds CommonModules repository, dependency, and source drift diagnostics.
/// </summary>
public sealed class CommonModulesDiagnosticProvider : IDoctorProjectDiagnosticProvider
{
    private readonly CommonModulesManifestReader commonModulesManifestReader;

    /// <summary>
    /// Creates a CommonModules diagnostic provider.
    /// </summary>
    /// <param name="commonModulesManifestReader">The reader used to load the CommonModules manifest.</param>
    public CommonModulesDiagnosticProvider(CommonModulesManifestReader commonModulesManifestReader)
    {
        this.commonModulesManifestReader = commonModulesManifestReader;
    }

    /// <inheritdoc />
    public void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sourceSetPath = project.ResolvePath(document.SourcePath);
            foreach (var module in document.CommonModules)
            {
                AddStoredSourceDiagnostic(results, documentName, module, sourceSetPath);
            }
        }

        if (project.CommonModulesRepositoryPath is null || !Directory.Exists(project.CommonModulesRepositoryPath))
        {
            return;
        }

        IReadOnlyList<CommonModuleManifestEntry> entries;
        try
        {
            entries = commonModulesManifestReader.Load(project.CommonModulesRepositoryPath);
        }
        catch (CommonModulesManifestException ex)
        {
            results.Add(DiagnosticResult.Fail(
                "project.commonModules.manifest",
                "CommonModules manifest",
                ex.Message));
            return;
        }

        var entriesByFile = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddDocumentRepositoryDiagnostics(results, project, documentName, document, entries, entriesByFile);
        }
    }

    private static void AddStoredSourceDiagnostic(
        List<DiagnosticResult> results,
        string documentName,
        InstalledCommonModule module,
        string sourceSetPath)
    {
        var sourceMatches = DocumentSourceSetLayout.FindSourceMatches(sourceSetPath, module.ModuleFile);
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
        IReadOnlyDictionary<string, CommonModuleManifestEntry> entriesByFile)
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
                results.Add(DiagnosticResult.Fail(
                    CommonModulesCheckId(documentName, module.Name, "manifestEntry"),
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Unknown CommonModuleName '{module.Name}' in vba-project.json."));
            }
        }

        var reachableDependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in document.CommonModules.Where(module => module.Requested))
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

            if (!module.Requested && !reachableDependencyNames.Contains(module.Name))
            {
                results.Add(DiagnosticResult.Warn(
                    CommonModulesCheckId(documentName, module.Name, "reachability"),
                    $"CommonModules ({documentName}/{module.Name})",
                    "Installed dependency entry is unreachable from requested CommonModules roots."));
            }

            AddSourceDriftDiagnostic(results, documentName, module, sourceSetPath, project.CommonModulesRepositoryPath!, entry);
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
        CommonModuleManifestEntry entry)
    {
        var sourceMatches = DocumentSourceSetLayout.FindSourceMatches(sourceSetPath, module.ModuleFile);
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
        var sourceSidecarPath = DocumentSourceSetLayout.ResolveExistingSidecarPath(sourcePath);
        var repositorySidecarPath = DocumentSourceSetLayout.ResolveExistingSidecarPath(repositoryPath);
        var hasDifferentFormSidecar = false;
        if (DocumentSourceSetLayout.IsFormFile(sourcePath) &&
            DocumentSourceSetLayout.IsFormFile(repositoryPath))
        {
            hasDifferentFormSidecar = sourceSidecarPath is null || repositorySidecarPath is null
                ? sourceSidecarPath != repositorySidecarPath
                : !File.ReadAllBytes(sourceSidecarPath).SequenceEqual(File.ReadAllBytes(repositorySidecarPath));
        }

        if (!File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(repositoryPath)) ||
            hasDifferentFormSidecar)
        {
            results.Add(DiagnosticResult.Warn(
                CommonModulesCheckId(documentName, module.Name, "repositorySource"),
                $"CommonModules ({documentName}/{module.Name})",
                $"Source file differs from CommonModulesRepository: {sourcePath}."));
        }
    }

    private static string CommonModulesCheckId(
        string documentName,
        string moduleName,
        string finding)
        => $"project.commonModules.{Uri.EscapeDataString(documentName)}." +
           $"{Uri.EscapeDataString(moduleName)}.{finding}";
}
