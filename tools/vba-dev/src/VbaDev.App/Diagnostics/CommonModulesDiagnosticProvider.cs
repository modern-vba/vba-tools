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
            results.Add(DiagnosticResult.Fail("CommonModules manifest", ex.Message));
            return;
        }

        var entriesByFile = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddDocumentCommonModulesDiagnostics(results, project, documentName, document, entries, entriesByFile);
        }
    }

    private static void AddDocumentCommonModulesDiagnostics(
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
            AddDependencyDiagnostics(results, documentName, module.Name, entry, entriesByFile, installedByName, reachableDependencyNames, []);
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
                    $"CommonModules ({documentName}/{module.Name})",
                    "Installed dependency entry is unreachable from requested CommonModules roots."));
            }

            AddSourceDriftDiagnostic(results, documentName, module.Name, sourceSetPath, project.CommonModulesRepositoryPath!, entry);
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
        HashSet<string> visiting)
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
            if (!installedByName.ContainsKey(dependencyName))
            {
                results.Add(DiagnosticResult.Fail(
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
                visiting);
        }

        visiting.Remove(entry.ModuleFile);
    }

    private static void AddSourceDriftDiagnostic(
        List<DiagnosticResult> results,
        string documentName,
        string moduleName,
        string sourceSetPath,
        string commonModulesRepositoryPath,
        CommonModuleManifestEntry entry)
    {
        var sourceMatches = DocumentSourceSetLayout.FindSourceMatches(sourceSetPath, entry.ModuleFile);
        var repositoryPath = Path.Combine(commonModulesRepositoryPath, entry.ModuleFile);
        if (sourceMatches.Count == 0)
        {
            results.Add(DiagnosticResult.Fail(
                $"CommonModules ({documentName}/{moduleName})",
                $"Manifest-listed source file was not found under {sourceSetPath}: {entry.ModuleFile}."));
            return;
        }

        if (sourceMatches.Count > 1)
        {
            results.Add(DiagnosticResult.Fail(
                $"CommonModules ({documentName}/{moduleName})",
                $"Installed CommonModule has multiple source matches for '{entry.ModuleFile}': {string.Join(", ", sourceMatches)}."));
            return;
        }

        if (!File.Exists(repositoryPath))
        {
            results.Add(DiagnosticResult.Fail(
                $"CommonModules ({documentName}/{moduleName})",
                $"CommonModulesRepository source file was not found: {repositoryPath}."));
            return;
        }

        var sourcePath = sourceMatches[0];
        if (!File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(repositoryPath)))
        {
            results.Add(DiagnosticResult.Warn(
                $"CommonModules ({documentName}/{moduleName})",
                $"Source file differs from CommonModulesRepository: {sourcePath}."));
        }
    }
}
