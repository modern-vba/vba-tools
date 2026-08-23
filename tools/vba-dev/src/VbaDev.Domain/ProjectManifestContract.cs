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
            throw new VbaProjectManifestException($"Project manifest could not be parsed: {manifestName}. {ex.Message}", ex);
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

        ValidateCommandDefaults(manifest.CommandDefaults, manifestName);
    }

    private static void ValidateCommandDefaults(CommandDefaults? commandDefaults, string manifestName)
    {
        if (commandDefaults?.Test?.ExecutionTimeoutSeconds is int testExecutionTimeoutSeconds
            && testExecutionTimeoutSeconds <= 0)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults.test.executionTimeoutSeconds must be positive whole seconds: {manifestName}");
        }

        if (commandDefaults?.ExcelAutomation?.WorkbookOpenTimeoutSeconds is int workbookOpenTimeoutSeconds
            && workbookOpenTimeoutSeconds <= 0)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults.excelAutomation.workbookOpenTimeoutSeconds must be positive whole seconds: {manifestName}");
        }

        if (commandDefaults?.ExcelAutomation?.WorkbookSaveTimeoutSeconds is int workbookSaveTimeoutSeconds
            && workbookSaveTimeoutSeconds <= 0)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults.excelAutomation.workbookSaveTimeoutSeconds must be positive whole seconds: {manifestName}");
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

        var commonModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonModuleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commonModule in document.CommonModules ?? [])
        {
            if (commonModule is null)
            {
                throw new VbaProjectManifestException($"Document '{name}' contains a null CommonModules entry: {manifestName}");
            }

            if (string.IsNullOrWhiteSpace(commonModule.Name))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains an empty CommonModules name: {manifestName}");
            }

            if (string.IsNullOrWhiteSpace(commonModule.ModuleFile))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains an empty CommonModules moduleFile for '{commonModule.Name}': {manifestName}");
            }

            if (!string.Equals(commonModule.ModuleFile, Path.GetFileName(commonModule.ModuleFile), StringComparison.Ordinal)
                || !IsSupportedVbaSourceFile(commonModule.ModuleFile))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains invalid CommonModules moduleFile '{commonModule.ModuleFile}': {manifestName}");
            }

            if (!string.Equals(commonModule.Name, Path.GetFileNameWithoutExtension(commonModule.ModuleFile), StringComparison.OrdinalIgnoreCase))
            {
                throw new VbaProjectManifestException($"Document '{name}' CommonModules name '{commonModule.Name}' does not match moduleFile '{commonModule.ModuleFile}': {manifestName}");
            }

            if (!commonModuleNames.Add(commonModule.Name))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains duplicate CommonModules name '{commonModule.Name}': {manifestName}");
            }

            if (!commonModuleFiles.Add(commonModule.ModuleFile))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains duplicate CommonModules moduleFile '{commonModule.ModuleFile}': {manifestName}");
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

    private static bool IsSupportedVbaSourceFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".bas" or ".cls" or ".frm";
}
