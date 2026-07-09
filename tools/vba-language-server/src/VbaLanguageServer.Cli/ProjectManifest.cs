using System.Text.Json;

namespace VbaLanguageServer.ProjectModel;

public sealed record ProjectManifest(
    int SchemaVersion,
    string ProjectName,
    string PrimaryDocument,
    Dictionary<string, ProjectDocument> Documents,
    string? CommonModulesRepository = null,
    CommandDefaults? CommandDefaults = null)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ProjectDocument(
    string Kind,
    string SourcePath,
    string TemplatePath,
    string BinPath,
    string PublishPath,
    List<InstalledCommonModule>? CommonModules = null,
    List<VbaProjectReference>? References = null)
{
    public const string ExcelKind = "excel";
}

public sealed record InstalledCommonModule(string Name, bool Requested);

public sealed record VbaProjectReference(string Name);

public sealed record CommandDefaults(TestCommandDefaults? Test = null);

public sealed record TestCommandDefaults(string? Format = null);

public sealed class ProjectManifestException : Exception
{
    public ProjectManifestException(string message)
        : base(message)
    {
    }

    public ProjectManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ProjectManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static ProjectManifest Parse(string json, string manifestName)
    {
        ProjectManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be parsed: {manifestName}", ex);
        }

        if (manifest is null)
        {
            throw new ProjectManifestException($"Project manifest is empty: {manifestName}");
        }

        Validate(manifest, manifestName);
        return manifest;
    }

    private static void Validate(ProjectManifest manifest, string manifestName)
    {
        if (manifest.SchemaVersion != ProjectManifest.CurrentSchemaVersion)
        {
            throw new ProjectManifestException($"Unsupported schemaVersion '{manifest.SchemaVersion}' in {manifestName}. Expected schemaVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ProjectName))
        {
            throw new ProjectManifestException($"Project manifest is missing projectName: {manifestName}");
        }

        if (string.IsNullOrWhiteSpace(manifest.PrimaryDocument))
        {
            throw new ProjectManifestException($"Project manifest is missing primaryDocument: {manifestName}");
        }

        if (manifest.Documents is null || manifest.Documents.Count == 0)
        {
            throw new ProjectManifestException($"Project manifest must define at least one document: {manifestName}");
        }

        if (!manifest.Documents.Keys.Any(name => string.Equals(name, manifest.PrimaryDocument, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProjectManifestException($"primaryDocument '{manifest.PrimaryDocument}' is not defined in documents: {manifestName}");
        }

        foreach (var (name, document) in manifest.Documents)
        {
            ValidateDocument(name, document, manifestName);
        }
    }

    private static void ValidateDocument(string name, ProjectDocument document, string manifestName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProjectManifestException($"Project manifest contains an empty document name: {manifestName}");
        }

        if (!string.Equals(document.Kind, ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectManifestException($"Unsupported document kind '{document.Kind}' for document '{name}': {manifestName}");
        }

        if (string.IsNullOrWhiteSpace(document.SourcePath) ||
            string.IsNullOrWhiteSpace(document.TemplatePath) ||
            string.IsNullOrWhiteSpace(document.BinPath) ||
            string.IsNullOrWhiteSpace(document.PublishPath))
        {
            throw new ProjectManifestException($"Document '{name}' must define sourcePath, templatePath, binPath, and publishPath: {manifestName}");
        }

        foreach (var commonModule in document.CommonModules ?? [])
        {
            if (string.IsNullOrWhiteSpace(commonModule.Name))
            {
                throw new ProjectManifestException($"Document '{name}' contains an empty CommonModules name: {manifestName}");
            }
        }

        foreach (var reference in document.References ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference.Name))
            {
                throw new ProjectManifestException($"Document '{name}' contains an empty VBA project reference name: {manifestName}");
            }
        }
    }
}
