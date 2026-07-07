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
        var context = TryResolveProjectContext(request, results);
        if (context is null)
        {
            AddSkippedProjectDiagnostics(results);
        }
        else
        {
            AddProjectDiagnostics(results, context);
        }

        results.AddRange(environmentDiagnosticPort.RunEnvironmentDiagnostics());

        var output = Render(results);
        var exitCode = results.Any(result => result.Status == DiagnosticStatus.Fail) ? 1 : 0;
        return new CommandResult(exitCode, output, string.Empty);
    }

    private ResolvedProjectContext? TryResolveProjectContext(
        DoctorCommandRequest request,
        List<DiagnosticResult> results)
    {
        try
        {
            return projectContextResolver.Resolve(new ProjectResolutionRequest(
                request.ProjectRoot,
                request.DocumentName,
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
        results.Add(DiagnosticResult.Skip("Selected document paths", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("CommonModulesRepository", "No ProjectManifest was resolved."));
        results.Add(DiagnosticResult.Skip("Command defaults", "No ProjectManifest was resolved."));
    }

    private static void AddProjectDiagnostics(List<DiagnosticResult> results, ResolvedProjectContext context)
    {
        results.Add(DiagnosticResult.Pass("Project manifest", $"Loaded {context.ManifestPath}."));
        results.Add(Directory.Exists(context.DocumentSourceSetPath)
            ? DiagnosticResult.Pass("Document source set", $"Found {context.DocumentSourceSetPath}.")
            : DiagnosticResult.Fail("Document source set", $"Create the source directory or run vba-devtool new: {context.DocumentSourceSetPath}."));
        results.Add(File.Exists(context.TemplateDocumentPath)
            ? DiagnosticResult.Pass("Source template", $"Found {context.TemplateDocumentPath}.")
            : DiagnosticResult.Fail("Source template", $"Create the macro-enabled template workbook: {context.TemplateDocumentPath}."));
        results.Add(Directory.Exists(Path.GetDirectoryName(context.BinDocumentPath))
            ? DiagnosticResult.Pass("Bin output directory", $"Found {Path.GetDirectoryName(context.BinDocumentPath)}.")
            : DiagnosticResult.Warn("Bin output directory", $"Will be created by build when needed: {Path.GetDirectoryName(context.BinDocumentPath)}."));
        results.Add(Directory.Exists(Path.GetDirectoryName(context.PublishDocumentPath))
            ? DiagnosticResult.Pass("Publish output directory", $"Found {Path.GetDirectoryName(context.PublishDocumentPath)}.")
            : DiagnosticResult.Warn("Publish output directory", $"Will be created by publish when needed: {Path.GetDirectoryName(context.PublishDocumentPath)}."));

        if (context.CommonModulesRepositoryPath is null)
        {
            results.Add(DiagnosticResult.Warn("CommonModulesRepository", "No CommonModulesRepository path is configured."));
        }
        else
        {
            results.Add(Directory.Exists(context.CommonModulesRepositoryPath)
                ? DiagnosticResult.Pass("CommonModulesRepository", $"Found {context.CommonModulesRepositoryPath}.")
                : DiagnosticResult.Warn("CommonModulesRepository", $"CommonModulesRepository was not found: {context.CommonModulesRepositoryPath}."));
        }

        try
        {
            var format = CommandDefaultResolver.ResolveTestFormat(context.Manifest, null);
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
