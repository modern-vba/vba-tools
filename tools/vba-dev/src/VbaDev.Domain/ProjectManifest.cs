namespace VbaDev.Domain;

public sealed record ProjectManifest(
    int SchemaVersion,
    string ProjectName,
    string PrimaryDocument,
    Dictionary<string, ProjectDocument> Documents,
    string? CommonModulesRepository = null,
    CommandDefaults? CommandDefaults = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string ManifestFileName = "project.json";

    public static ProjectManifest CreateDefault(
        string projectName,
        string documentName,
        string projectRoot,
        string? commonModulesRepositoryPath,
        IReadOnlyList<InstalledCommonModule>? commonModules = null,
        IReadOnlyList<VbaProjectReference>? references = null)
    {
        var documents = new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
        {
            [documentName] = ProjectDocument.CreateExcel(documentName, commonModules, references)
        };

        return new ProjectManifest(
            CurrentSchemaVersion,
            projectName,
            documentName,
            documents,
            ToManifestPath(projectRoot, commonModulesRepositoryPath),
            new CommandDefaults(Test: new TestCommandDefaults(Format: "text")));
    }

    private static string? ToManifestPath(string projectRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(fullProjectRoot, fullPath);

        return relativePath.Contains(':', StringComparison.Ordinal)
            ? fullPath.Replace('\\', '/')
            : relativePath.Replace('\\', '/');
    }
}
