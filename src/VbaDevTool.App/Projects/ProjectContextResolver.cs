using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

public sealed class ProjectContextResolver
{
    private readonly IProjectManifestStore manifestStore;

    public ProjectContextResolver(IProjectManifestStore manifestStore)
    {
        this.manifestStore = manifestStore;
    }

    public ResolvedProjectContext Resolve(ProjectResolutionRequest request)
    {
        var projectRoot = ResolveProjectRoot(request);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var manifest = manifestStore.Load(manifestPath);
        var documentName = string.IsNullOrWhiteSpace(request.DocumentName)
            ? manifest.PrimaryDocument
            : request.DocumentName;

        if (!TryGetDocument(manifest, documentName, out var document))
        {
            throw new ProjectManifestException($"Document '{documentName}' is not defined in {ProjectManifest.ManifestFileName}.");
        }

        return new ResolvedProjectContext(
            ProjectRoot: projectRoot,
            ManifestPath: manifestPath,
            Manifest: manifest,
            DocumentName: documentName,
            Document: document,
            DocumentSourceSetPath: ResolvePath(projectRoot, document.SourcePath),
            TemplateDocumentPath: ResolvePath(projectRoot, document.TemplatePath),
            BinDocumentPath: ResolvePath(projectRoot, document.BinPath),
            PublishDocumentPath: ResolvePath(projectRoot, document.PublishPath),
            CommonModulesRepositoryPath: string.IsNullOrWhiteSpace(manifest.CommonModulesRepository)
                ? null
                : ResolvePath(projectRoot, manifest.CommonModulesRepository));
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
