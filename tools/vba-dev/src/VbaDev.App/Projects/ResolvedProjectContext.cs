using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Contains a loaded project plus the selected document and its resolved filesystem paths.
/// </summary>
/// <param name="ProjectRoot">The absolute project root directory.</param>
/// <param name="ManifestPath">The absolute path to project.json.</param>
/// <param name="Manifest">The loaded project manifest.</param>
/// <param name="DocumentName">The selected document name from the manifest.</param>
/// <param name="Document">The selected document manifest entry.</param>
/// <param name="DocumentSourceSetPath">The absolute document source set path.</param>
/// <param name="TemplateDocumentPath">The absolute source template workbook path.</param>
/// <param name="BinDocumentPath">The absolute generated build workbook path.</param>
/// <param name="PublishDocumentPath">The absolute generated publish workbook path.</param>
/// <param name="CommonModulesRepositoryPath">The resolved CommonModulesRepository path, when configured.</param>
public sealed record ResolvedProjectContext(
    string ProjectRoot,
    string ManifestPath,
    ProjectManifest Manifest,
    string DocumentName,
    ProjectDocument Document,
    string DocumentSourceSetPath,
    string TemplateDocumentPath,
    string BinDocumentPath,
    string PublishDocumentPath,
    string? CommonModulesRepositoryPath);
