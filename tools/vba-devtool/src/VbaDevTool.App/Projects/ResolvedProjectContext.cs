using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

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
