using System.Text.Json;

namespace VbaLanguageServer.ProjectModel;

/// <summary>
/// Represents the project.json contract read by the language server for editor features.
/// </summary>
/// <param name="SchemaVersion">The manifest schema version understood by the language server.</param>
/// <param name="ProjectName">The workbook-backed project name.</param>
/// <param name="PrimaryDocument">The default document key for project commands.</param>
/// <param name="Documents">The document definitions keyed by document name.</param>
/// <param name="CommonModulesRepository">The optional CommonModulesRepository path.</param>
/// <param name="CommandDefaults">The command defaults carried by the manifest.</param>
public sealed record ProjectManifest(
    int SchemaVersion,
    string ProjectName,
    string PrimaryDocument,
    Dictionary<string, ProjectDocument> Documents,
    string? CommonModulesRepository = null,
    CommandDefaults? CommandDefaults = null)
{
    /// <summary>
    /// The latest manifest schema version accepted by the language server.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Describes one document source set inside a language-server project manifest.
/// </summary>
/// <param name="Kind">The Office document kind stored in project.json.</param>
/// <param name="SourcePath">The document source set path.</param>
/// <param name="TemplatePath">The source template workbook path.</param>
/// <param name="BinPath">The generated build workbook path.</param>
/// <param name="PublishPath">The generated publish workbook path.</param>
/// <param name="CommonModules">The CommonModules entries tracked for this document.</param>
/// <param name="References">The VBA project references active for this document.</param>
public sealed record ProjectDocument(
    string Kind,
    string SourcePath,
    string TemplatePath,
    string BinPath,
    string PublishPath,
    List<InstalledCommonModule>? CommonModules = null,
    List<VbaProjectReference>? References = null)
{
    /// <summary>
    /// The manifest document kind value for Excel workbooks.
    /// </summary>
    public const string ExcelKind = "excel";
}

/// <summary>
/// Tracks a CommonModules source entry installed in a document source set.
/// </summary>
/// <param name="Name">The extensionless CommonModuleName.</param>
/// <param name="Requested">Whether the module was explicitly requested rather than installed as a dependency.</param>
public sealed record InstalledCommonModule(string Name, bool Requested);

/// <summary>
/// Names a VBA project reference declared in project.json.
/// </summary>
/// <param name="Name">The human-visible reference description.</param>
public sealed record VbaProjectReference(string Name);

/// <summary>
/// Stores project-level command defaults mirrored from project.json.
/// </summary>
/// <param name="Test">The defaults for test command behavior.</param>
public sealed record CommandDefaults(TestCommandDefaults? Test = null);

/// <summary>
/// Stores default test command option values.
/// </summary>
/// <param name="Format">The default test output format.</param>
public sealed record TestCommandDefaults(string? Format = null);

/// <summary>
/// Reports invalid, missing, or unreadable language-server project manifest state.
/// </summary>
public sealed class ProjectManifestException : Exception
{
    /// <summary>
    /// Creates a project manifest exception with a user-facing message.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    public ProjectManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a project manifest exception that preserves an underlying parse or I/O failure.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ProjectManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Parses and validates language-server project manifests from JSON text.
/// </summary>
public static class ProjectManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses and validates a project manifest JSON document.
    /// </summary>
    /// <param name="json">The manifest JSON text.</param>
    /// <param name="manifestName">The manifest path or display name used in errors.</param>
    /// <returns>The parsed project manifest.</returns>
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
