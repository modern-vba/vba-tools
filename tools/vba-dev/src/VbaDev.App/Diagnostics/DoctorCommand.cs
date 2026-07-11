using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.CommonModules;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Diagnostics;

public sealed class DoctorCommand
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly IVbaProjectReferenceResolver referenceResolver;
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;
    private readonly IEnvironmentDiagnosticPort environmentDiagnosticPort;

    public DoctorCommand(
        ProjectContextResolver projectContextResolver,
        CommonModulesManifestReader commonModulesManifestReader,
        IVbaProjectReferenceResolver referenceResolver,
        IWorkbookBuildAutomation workbookBuildAutomation,
        IEnvironmentDiagnosticPort environmentDiagnosticPort)
    {
        this.projectContextResolver = projectContextResolver;
        this.commonModulesManifestReader = commonModulesManifestReader;
        this.referenceResolver = referenceResolver;
        this.workbookBuildAutomation = workbookBuildAutomation;
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
                : DiagnosticResult.Fail($"Document source set ({documentName})", $"Create the source directory or run vba-dev new: {sourceSetPath}."));
            results.Add(File.Exists(templatePath)
                ? DiagnosticResult.Pass($"Source template ({documentName})", $"Found {templatePath}.")
                : DiagnosticResult.Fail($"Source template ({documentName})", $"Create the macro-enabled template workbook: {templatePath}."));
            if (Directory.Exists(sourceSetPath))
            {
                AddDocumentSourceIdentityDiagnostics(results, documentName, sourceSetPath);
            }

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
        AddVbaProjectReferenceDiagnostics(results, project);

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

    private static void AddDocumentSourceIdentityDiagnostics(
        List<DiagnosticResult> results,
        string documentName,
        string sourceSetPath)
    {
        var sourceFiles = EnumerateVbaSourceFiles(sourceSetPath).ToArray();
        foreach (var group in sourceFiles
                     .GroupBy(GetFileName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Skip(1).Any())
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(DiagnosticResult.Fail(
                $"Document source identity ({documentName}/{group.Key})",
                $"Duplicate exported source file name. Colliding files: {string.Join(", ", group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}."));
        }

        var formFilesByName = sourceFiles
            .Where(path => Path.GetExtension(path).Equals(".frm", StringComparison.OrdinalIgnoreCase))
            .GroupBy(GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var sidecarPath in Directory.EnumerateFiles(sourceSetPath, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var sidecarName = Path.GetFileNameWithoutExtension(sidecarPath);
            if (!formFilesByName.TryGetValue(sidecarName, out var matchingForms))
            {
                continue;
            }

            if (HasSameDirectoryForm(sidecarPath, sidecarName))
            {
                continue;
            }

            results.Add(DiagnosticResult.Warn(
                $"Form sidecar ({documentName}/{Path.GetFileName(sidecarPath)})",
                $"Sidecar has no same-directory .frm, but a same-name form exists elsewhere: {sidecarPath}. Matching forms: {string.Join(", ", matchingForms)}."));
        }
    }

    private static bool HasSameDirectoryForm(string sidecarPath, string sidecarName)
    {
        var directory = Path.GetDirectoryName(sidecarPath);
        return directory is not null &&
            Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path =>
                    Path.GetExtension(path).Equals(".frm", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileNameWithoutExtension(path).Equals(sidecarName, StringComparison.OrdinalIgnoreCase));
    }

    private void AddVbaProjectReferenceDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddManifestReferenceConsistencyDiagnostic(results, documentName, document);
            AddReferenceCatalogAvailabilityDiagnostics(results, documentName, document);
            var templateReferences = GetTemplateReferenceNames(results, project, documentName, document);
            foreach (var reference in document.References)
            {
                if (templateReferences.Contains(reference.Name))
                {
                    results.Add(DiagnosticResult.Pass(
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        "Reference is already present in the source template."));
                    continue;
                }

                var matches = referenceResolver.Resolve(reference.Name);
                if (matches.Count == 0)
                {
                    results.Add(DiagnosticResult.Fail(
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        $"Reference was not found: {reference.Name}."));
                }
                else if (matches.Count > 1)
                {
                    results.Add(DiagnosticResult.Fail(
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        $"Reference is ambiguous: {reference.Name}."));
                }
                else
                {
                    results.Add(DiagnosticResult.Pass(
                        $"VbaProjectReferences ({documentName}/{reference.Name})",
                        "Reference resolved."));
                }
            }
        }
    }

    private static void AddReferenceCatalogAvailabilityDiagnostics(
        List<DiagnosticResult> results,
        string documentName,
        ProjectDocument document)
    {
        foreach (var reference in document.References
            .Where(reference => !VbaProjectReferenceCatalogAvailability.HasUsableCatalog(reference.Name))
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(DiagnosticResult.Warn(
                $"VbaProjectReferenceCatalog ({documentName}/{reference.Name})",
                "No bundled or cached VbaProjectReferenceCatalog metadata is available. The reference remains active, but external editor definitions are unavailable."));
        }
    }

    private static void AddManifestReferenceConsistencyDiagnostic(
        List<DiagnosticResult> results,
        string documentName,
        ProjectDocument document)
    {
        var selection = VbaProjectReferenceSelection.Create(document.Kind, document.References);
        if (selection.MissingExpectedMainReference is null)
        {
            return;
        }

        results.Add(DiagnosticResult.Warn(
            $"VbaProjectReferences ({documentName})",
            $"Manifest/reference consistency warning: document kind '{document.Kind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly."));
    }

    private IReadOnlySet<string> GetTemplateReferenceNames(
        List<DiagnosticResult> results,
        ResolvedProject project,
        string documentName,
        ProjectDocument document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.References.Count == 0)
        {
            return names;
        }

        var templatePath = project.ResolvePath(document.TemplatePath);
        if (!File.Exists(templatePath))
        {
            return names;
        }

        try
        {
            using var session = workbookBuildAutomation.OpenWorkbook(templatePath);
            foreach (var reference in session.GetReferences())
            {
                names.Add(reference.Name);
            }
        }
        catch (COMException ex)
        {
            results.Add(DiagnosticResult.Warn(
                $"VbaProjectReferences ({documentName})",
                $"Could not inspect source template references: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            results.Add(DiagnosticResult.Warn(
                $"VbaProjectReferences ({documentName})",
                $"Could not inspect source template references: {ex.Message}"));
        }
        catch (IOException ex)
        {
            results.Add(DiagnosticResult.Warn(
                $"VbaProjectReferences ({documentName})",
                $"Could not inspect source template references: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            results.Add(DiagnosticResult.Warn(
                $"VbaProjectReferences ({documentName})",
                $"Could not inspect source template references: {ex.Message}"));
        }

        return names;
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
        var sourceMatches = FindSourceMatches(sourceSetPath, entry.ModuleFile);
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

    private static IReadOnlyList<string> FindSourceMatches(string sourceSetPath, string moduleFile)
        => EnumerateVbaSourceFiles(sourceSetPath)
            .Where(path => GetFileName(path).Equals(moduleFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> EnumerateVbaSourceFiles(string sourceSetPath)
    {
        if (!Directory.Exists(sourceSetPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(sourceSetPath, "*", SearchOption.AllDirectories)
            .Where(IsVbaSourceFile);
    }

    private static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileName(string path)
        => Path.GetFileName(path) ?? string.Empty;

    private static string GetFileNameWithoutExtension(string path)
        => Path.GetFileNameWithoutExtension(path) ?? string.Empty;

    private static string Render(IReadOnlyList<DiagnosticResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("vba-dev doctor");
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
