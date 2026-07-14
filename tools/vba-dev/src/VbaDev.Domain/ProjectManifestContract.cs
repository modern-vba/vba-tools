using System.Text.Json;

namespace VbaDev.Domain;

/// <summary>
/// Reports an invalid, missing, or unreadable project manifest contract.
/// </summary>
public sealed class VbaProjectManifestException : Exception
{
    /// <summary>
    /// Creates a project manifest contract exception.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    public VbaProjectManifestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a project manifest contract exception with an underlying JSON failure.
    /// </summary>
    /// <param name="message">The manifest error message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public VbaProjectManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Parses the shared vba-project.json contract from JSON text.
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
    /// <param name="manifestName">The manifest path or display name.</param>
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
            throw new VbaProjectManifestException($"Project manifest could not be parsed: {manifestName}", ex);
        }

        if (manifest is null)
        {
            throw new VbaProjectManifestException($"Project manifest is empty: {manifestName}");
        }

        ProjectManifestValidator.Validate(manifest, manifestName);
        return manifest;
    }
}

/// <summary>
/// Owns validation rules for the shared vba-project.json contract.
/// </summary>
public static class ProjectManifestValidator
{
    /// <summary>
    /// Validates a project manifest contract.
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="manifestName">The manifest path or display name.</param>
    public static void Validate(ProjectManifest manifest, string manifestName)
    {
        if (manifest.SchemaVersion != ProjectManifest.CurrentSchemaVersion)
        {
            throw new VbaProjectManifestException($"Unsupported schemaVersion '{manifest.SchemaVersion}' in {manifestName}. Expected schemaVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ProjectName))
        {
            throw new VbaProjectManifestException($"Project manifest is missing projectName: {manifestName}");
        }

        if (string.IsNullOrWhiteSpace(manifest.PrimaryDocument))
        {
            throw new VbaProjectManifestException($"Project manifest is missing primaryDocument: {manifestName}");
        }

        if (manifest.Documents is null || manifest.Documents.Count == 0)
        {
            throw new VbaProjectManifestException($"Project manifest must define at least one document: {manifestName}");
        }

        if (!manifest.Documents.Keys.Any(name => string.Equals(name, manifest.PrimaryDocument, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VbaProjectManifestException($"primaryDocument '{manifest.PrimaryDocument}' is not defined in documents: {manifestName}");
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
            throw new VbaProjectManifestException($"Project manifest contains an empty document name: {manifestName}");
        }

        if (!string.Equals(document.Kind, ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new VbaProjectManifestException($"Unsupported document kind '{document.Kind}' for document '{name}': {manifestName}");
        }

        if (string.IsNullOrWhiteSpace(document.SourcePath)
            || string.IsNullOrWhiteSpace(document.TemplatePath)
            || string.IsNullOrWhiteSpace(document.BinPath)
            || string.IsNullOrWhiteSpace(document.PublishPath))
        {
            throw new VbaProjectManifestException($"Document '{name}' must define sourcePath, templatePath, binPath, and publishPath: {manifestName}");
        }

        foreach (var commonModule in document.CommonModules ?? [])
        {
            if (string.IsNullOrWhiteSpace(commonModule.Name))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains an empty CommonModules name: {manifestName}");
            }
        }

        foreach (var reference in document.References ?? [])
        {
            if (string.IsNullOrWhiteSpace(reference.Name))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains an empty VBA project reference name: {manifestName}");
            }
        }
    }
}
