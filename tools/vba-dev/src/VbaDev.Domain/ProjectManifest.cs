namespace VbaDev.Domain;

/// <summary>
/// Represents the project.json contract for a workbook-backed VBA project.
/// </summary>
/// <param name="SchemaVersion">The manifest schema version understood by this toolset.</param>
/// <param name="ProjectName">The stable project name used in command output and generated defaults.</param>
/// <param name="PrimaryDocument">The document key treated as the primary Office macro document.</param>
/// <param name="Documents">The manifest document definitions keyed by document name.</param>
/// <param name="CommonModulesRepository">The project-relative or absolute CommonModulesRepository path.</param>
/// <param name="CommandDefaults">The command option defaults stored with the project.</param>
public sealed record ProjectManifest(
    int SchemaVersion,
    string ProjectName,
    string PrimaryDocument,
    Dictionary<string, ProjectDocument> Documents,
    string? CommonModulesRepository = null,
    CommandDefaults? CommandDefaults = null)
{
    /// <summary>
    /// The latest manifest schema version emitted by VbaDev.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The file name used for project manifests on disk.
    /// </summary>
    public const string ManifestFileName = "project.json";

    /// <summary>
    /// Creates the default manifest for a new Excel-backed VBA project.
    /// </summary>
    /// <param name="projectName">The project name to store in the manifest.</param>
    /// <param name="documentName">The initial primary document name.</param>
    /// <param name="projectRoot">The project root used to relativize manifest paths.</param>
    /// <param name="commonModulesRepositoryPath">The optional CommonModulesRepository path to store.</param>
    /// <param name="commonModules">The CommonModules entries to install into the initial document.</param>
    /// <param name="references">The VBA project references required by the initial document.</param>
    /// <returns>A manifest with one Excel document and default test command settings.</returns>
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
