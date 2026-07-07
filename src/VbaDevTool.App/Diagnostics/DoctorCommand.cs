using System.Text;
using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;

namespace VbaDevTools.App.Diagnostics;

public sealed class DoctorCommand
{
    private readonly ProjectContextResolver projectContextResolver;
    private readonly IEnvironmentDiagnosticPort environmentDiagnosticPort;

    public DoctorCommand(
        ProjectContextResolver projectContextResolver,
        IEnvironmentDiagnosticPort environmentDiagnosticPort)
    {
        this.projectContextResolver = projectContextResolver;
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

    private static void AddProjectDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
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
