using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Diagnostics;

/// <summary>
/// Adds project path, source layout, and CommonModulesRepository diagnostics.
/// </summary>
public sealed class ProjectConfigurationDiagnosticProvider : IDoctorProjectDiagnosticProvider, IDoctorSourceDiagnosticProvider
{
    /// <inheritdoc />
    public void AddDiagnostics(List<DiagnosticResult> results, ResolvedProject project)
        => AddDiagnostics(results, project, null);

    void IDoctorSourceDiagnosticProvider.AddDiagnostics(
        List<DiagnosticResult> results, ResolvedProject project, DoctorProjectSourceInspection sources)
        => AddDiagnostics(results, project, sources);

    private static void AddDiagnostics(
        List<DiagnosticResult> results, ResolvedProject project, DoctorProjectSourceInspection? sources)
    {
        foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sourceSetPath = project.ResolvePath(document.SourcePath);
            var templatePath = project.ResolvePath(document.TemplatePath);
            var binPath = project.ResolvePath(document.BinPath);
            var publishPath = project.ResolvePath(document.PublishPath);
            var binDirectory = Path.GetDirectoryName(binPath) ?? project.ProjectRoot;
            var publishDirectory = Path.GetDirectoryName(publishPath) ?? project.ProjectRoot;

            var sourceExists = sources?.GetDocument(documentName).SourceDirectoryExists ?? Directory.Exists(sourceSetPath);
            results.Add(sourceExists
                ? DiagnosticResult.Pass($"Document source set ({documentName})", $"Found {sourceSetPath}.")
                : DiagnosticResult.Fail($"Document source set ({documentName})", $"Create the source directory or run vba-dev new: {sourceSetPath}."));
            results.Add(File.Exists(templatePath)
                ? DiagnosticResult.Pass($"Source template ({documentName})", $"Found {templatePath}.")
                : DiagnosticResult.Fail($"Source template ({documentName})", $"Create the macro-enabled template workbook: {templatePath}."));
            if (sourceExists)
            {
                AddDocumentSourceIdentityDiagnostics(results, documentName, sourceSetPath, sources?.GetInventory(documentName));
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
    }

    private static void AddDocumentSourceIdentityDiagnostics(
        List<DiagnosticResult> results,
        string documentName,
        string sourceSetPath,
        IReadOnlyList<string>? inventory)
    {
        var diagnostics = inventory is null
            ? DocumentSourceSetLayout.InspectSourceIdentity(documentName, sourceSetPath)
            : DocumentSourceSetLayout.InspectSourceIdentity(documentName, sourceSetPath, inventory);
        foreach (var diagnostic in diagnostics)
        {
            var checkId = $"project.sourceLayout.{Uri.EscapeDataString(documentName)}.{diagnostic.Id}";
            results.Add(diagnostic.Status == DocumentSourceSetLayoutDiagnosticStatus.Fail
                ? DiagnosticResult.Fail(checkId, diagnostic.Name, diagnostic.Message)
                : DiagnosticResult.Warn(checkId, diagnostic.Name, diagnostic.Message));
        }
    }
}
