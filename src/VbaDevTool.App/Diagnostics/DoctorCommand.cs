using System.Text;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;
using VbaDevTools.Domain;

namespace VbaDevTools.App.Diagnostics;

public sealed class DoctorCommand
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly IEnvironmentDiagnosticPort environmentDiagnosticPort;

    public DoctorCommand(
        ProjectContextResolver projectContextResolver,
        CommonModulesManifestReader commonModulesManifestReader,
        IEnvironmentDiagnosticPort environmentDiagnosticPort)
    {
        this.projectContextResolver = projectContextResolver;
        this.commonModulesManifestReader = commonModulesManifestReader;
        this.environmentDiagnosticPort = environmentDiagnosticPort;
    }

    public CommandResult Run(DoctorCommandRequest request)
    {
        var results = new List<DiagnosticResult>();
        var project = TryResolveProject(request, results);
        if (project is null)
        {
            AddSkippedProjectDiagnostics(results);
        }
        else
        {
            AddProjectDiagnostics(results, project);
        }

        results.AddRange(environmentDiagnosticPort.RunEnvironmentDiagnostics());

        var output = Render(results);
        var exitCode = results.Any(result => result.Status == DiagnosticStatus.Fail) ? 1 : 0;
        return new CommandResult(exitCode, output, string.Empty);
    }

    private ResolvedProject? TryResolveProject(
        DoctorCommandRequest request,
        List<DiagnosticResult> results)
    {
        try
        {
            return projectContextResolver.ResolveProject(new ProjectResolutionRequest(
                request.ProjectRoot,
                null,
                request.StartDirectory));
        }
        catch (ProjectManifestException ex) when (request.ProjectRoot is null && ex.Message.Contains("walking upward", StringComparison.Ordinal))
        {
            return null;
        }
        catch (ProjectManifestException ex)
        {
            results.Add(DiagnosticResult.Fail("Project manifest", ex.Message));
            return null;
        }
    }

    private static void AddSkippedProjectDiagnostics(List<DiagnosticResult> results)
    {
        results.Add(DiagnosticResult.Skip("Project manifest", "No project.json was found; project diagnostics were skipped."));
        results.Add(DiagnosticResult.Skip("Document paths", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("CommonModulesRepository", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("Command defaults", "No ProjectManifest was resolved."));
    }

    private void AddProjectDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
    {
        results.Add(DiagnosticResult.Pass("Project manifest", $"Loaded {project.ManifestPath}."));
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sourceSetPath = project.ResolvePath(document.SourcePath);
            var templatePath = project.ResolvePath(document.TemplatePath);
            var binPath = project.ResolvePath(document.BinPath);
            var publishPath = project.ResolvePath(document.PublishPath);
            var binDirectory = Path.GetDirectoryName(binPath) ?? project.ProjectRoot;
            var publishDirectory = Path.GetDirectoryName(publishPath) ?? project.ProjectRoot;

            results.Add(Directory.Exists(sourceSetPath)
                ? DiagnosticResult.Pass($"Document source set ({documentName})", $"Found {sourceSetPath}.")
                : DiagnosticResult.Fail($"Document source set ({documentName})", $"Create the source directory or run vba-devtool new: {sourceSetPath}."));
            results.Add(File.Exists(templatePath)
                ? DiagnosticResult.Pass($"Source template ({documentName})", $"Found {templatePath}.")
                : DiagnosticResult.Fail($"Source template ({documentName})", $"Create the macro-enabled template workbook: {templatePath}."));
            results.Add(Directory.Exists(binDirectory)
                ? DiagnosticResult.Pass($"Bin output directory ({documentName})", $"Found {binDirectory}.")
                : DiagnosticResult.Warn($"Bin output directory ({documentName})", $"Will be created by build when needed: {binDirectory}."));
            results.Add(Directory.Exists(publishDirectory)
                ? DiagnosticResult.Pass($"Publish output directory ({documentName})", $"Found {publishDirectory}.")
                : DiagnosticResult.Warn($"Publish output directory ({documentName})", $"Will be created by publish when needed: {publishDirectory}."));
        }

        if (project.CommonModulesRepositoryPath is null)
        {
            results.Add(DiagnosticResult.Warn("CommonModulesRepository", "No CommonModulesRepository path is configured."));
        }
        else
        {
            results.Add(Directory.Exists(project.CommonModulesRepositoryPath)
                ? DiagnosticResult.Pass("CommonModulesRepository", $"Found {project.CommonModulesRepositoryPath}.")
                : DiagnosticResult.Warn("CommonModulesRepository", $"CommonModulesRepository was not found: {project.CommonModulesRepositoryPath}."));
        }

        AddCommonModulesDiagnostics(results, project);

        try
        {
            var format = CommandDefaultResolver.ResolveTestFormat(project.Manifest, null);
            results.Add(DiagnosticResult.Pass("Command defaults", $"Test output format resolves to '{format}'."));
        }
        catch (ProjectManifestException ex)
        {
            results.Add(DiagnosticResult.Fail("Command defaults", ex.Message));
        }
    }

    private void AddCommonModulesDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
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
                resolvedByName[module.Name] = CommonModulesService.ResolveEntry(entries, module.Name);
            }
            catch (CommonModulesManifestException)
            {
                results.Add(DiagnosticResult.Fail(
                    $"CommonModules ({documentName}/{module.Name})",
                    $"Unknown CommonModuleName '{module.Name}' in project.json."));
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
        var sourcePath = Path.Combine(sourceSetPath, entry.ModuleFile);
        var repositoryPath = Path.Combine(commonModulesRepositoryPath, entry.ModuleFile);
        if (!File.Exists(sourcePath))
        {
            results.Add(DiagnosticResult.Fail(
                $"CommonModules ({documentName}/{moduleName})",
                $"Manifest-listed source file was not found: {sourcePath}."));
            return;
        }

        if (!File.Exists(repositoryPath))
        {
            results.Add(DiagnosticResult.Fail(
                $"CommonModules ({documentName}/{moduleName})",
                $"CommonModulesRepository source file was not found: {repositoryPath}."));
            return;
        }

        if (!File.ReadAllBytes(sourcePath).SequenceEqual(File.ReadAllBytes(repositoryPath)))
        {
            results.Add(DiagnosticResult.Warn(
                $"CommonModules ({documentName}/{moduleName})",
                $"Source file differs from CommonModulesRepository: {sourcePath}."));
        }
    }

    private static string Render(IReadOnlyList<DiagnosticResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-devtool doctor");
        builder.AppendLine();
        foreach (var result in results)
        {
            builder.AppendLine($"[{RenderStatus(result.Status)}] {result.Name}: {result.Message}");
        }

        return builder.ToString();
    }

    private static string RenderStatus(DiagnosticStatus status)
        => status switch
        {
            DiagnosticStatus.Pass => "PASS",
            DiagnosticStatus.Warn => "WARN",
            DiagnosticStatus.Fail => "FAIL",
            DiagnosticStatus.Skip => "SKIP",
            _ => status.ToString().ToUpperInvariant()
        };
}
