using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Locates project manifests and resolves document-specific project context for commands.
/// </summary>
public sealed class ProjectContextResolver
{
    private readonly IProjectManifestStore manifestStore;

    /// <summary>
    /// Creates a project context resolver.
    /// </summary>
    /// <param name="manifestStore">The manifest store used to load vba-project.json.</param>
    public ProjectContextResolver(IProjectManifestStore manifestStore)
    {
        this.manifestStore = manifestStore;
    }

    /// <summary>
    /// Resolves and loads a project manifest without selecting a document.
    /// </summary>
    /// <param name="request">The project resolution request.</param>
    /// <returns>The resolved project.</returns>
    public ResolvedProject ResolveProject(ProjectResolutionRequest request)
    {
        var projectRoot = ResolveProjectRoot(request);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = manifestStore.Load(manifestPath);

        return new ResolvedProject(
            ProjectRoot: projectRoot,
            ManifestPath: manifestPath,
            Manifest: manifest,
            CommonModulesRepositoryPath: string.IsNullOrWhiteSpace(manifest.CommonModulesRepository)
                ? null
                : ResolvePath(projectRoot, manifest.CommonModulesRepository));
    }

    /// <summary>
    /// Resolves a project manifest and selected document context.
    /// </summary>
    /// <param name="request">The project and document resolution request.</param>
    /// <returns>The resolved document context.</returns>
    public ResolvedProjectContext Resolve(ProjectResolutionRequest request)
    {
        var project = ResolveProject(request);
        var manifest = project.Manifest;
        var documentName = string.IsNullOrWhiteSpace(request.DocumentName)
            ? manifest.PrimaryDocument
            : request.DocumentName;

        if (!TryGetDocument(manifest, documentName, out var document))
        {
            throw new ProjectManifestException($"Document '{documentName}' is not defined in {ProjectManifest.ManifestFileName}.");
        }

        return new ResolvedProjectContext(
            ProjectRoot: project.ProjectRoot,
            ManifestPath: project.ManifestPath,
            Manifest: manifest,
            DocumentName: documentName,
            Document: document,
            DocumentSourceSetPath: project.ResolvePath(document.SourcePath),
            TemplateDocumentPath: project.ResolvePath(document.TemplatePath),
            BinDocumentPath: project.ResolvePath(document.BinPath),
            PublishDocumentPath: project.ResolvePath(document.PublishPath),
            CommonModulesRepositoryPath: project.CommonModulesRepositoryPath);
    }

    private static string ResolveProjectRoot(ProjectResolutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProjectRoot))
        {
            var explicitRoot = Path.GetFullPath(request.ProjectRoot);
            var explicitManifest = Path.Combine(explicitRoot, ProjectManifest.ManifestFileName);
            if (!File.Exists(explicitManifest))
            {
                throw new ProjectManifestException($"Project manifest was not found: {explicitManifest}");
            }

            return explicitRoot;
        }

        var current = new DirectoryInfo(Path.GetFullPath(request.StartDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, ProjectManifest.ManifestFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new ProjectManifestException($"Project manifest was not found while walking upward from: {request.StartDirectory}");
    }

    private static bool TryGetDocument(ProjectManifest manifest, string documentName, out ProjectDocument document)
    {
        if (manifest.Documents.TryGetValue(documentName, out document!))
        {
            return true;
        }

        foreach (var item in manifest.Documents)
        {
            if (string.Equals(item.Key, documentName, StringComparison.OrdinalIgnoreCase))
            {
                document = item.Value;
                return true;
            }
        }

        document = null!;
        return false;
    }

    private static string ResolvePath(string projectRoot, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
}
