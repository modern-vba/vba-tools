using System.Text.Json;
using System.Text.Json.Serialization;

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
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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
            using var document = JsonDocument.Parse(json);
            RejectExplicitNullOptionalState(document.RootElement, manifestName);
            manifest = document.RootElement.Deserialize<ProjectManifest>(JsonOptions);
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

    private static void RejectExplicitNullOptionalState(
        JsonElement root,
        string manifestName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var propertyName in new[] { "commonModulesRepository", "commandDefaults" })
        {
            if (root.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Null)
            {
                throw new VbaProjectManifestException(
                    $"Project manifest optional property '{propertyName}' must be omitted instead of null: {manifestName}");
            }
        }

        if (root.TryGetProperty("commandDefaults", out var commandDefaults)
            && commandDefaults.ValueKind == JsonValueKind.Object)
        {
            RejectExplicitNullProperties(
                commandDefaults,
                "commandDefaults",
                ["test", "excelAutomation"],
                manifestName);

            if (commandDefaults.TryGetProperty("test", out var test)
                && test.ValueKind == JsonValueKind.Object)
            {
                RejectExplicitNullProperties(
                    test,
                    "commandDefaults.test",
                    ["format", "executionTimeoutSeconds"],
                    manifestName);
            }

            if (commandDefaults.TryGetProperty("excelAutomation", out var excelAutomation)
                && excelAutomation.ValueKind == JsonValueKind.Object)
            {
                RejectExplicitNullProperties(
                    excelAutomation,
                    "commandDefaults.excelAutomation",
                    ["workbookOpenTimeoutSeconds", "workbookSaveTimeoutSeconds"],
                    manifestName);
            }
        }
    }

    private static void RejectExplicitNullProperties(
        JsonElement owner,
        string ownerName,
        IReadOnlyList<string> propertyNames,
        string manifestName)
    {
        foreach (var propertyName in propertyNames)
        {
            if (owner.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Null)
            {
                throw new VbaProjectManifestException(
                    $"Project manifest optional property '{ownerName}.{propertyName}' must be omitted instead of null: {manifestName}");
            }
        }
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

        if (manifest.CommonModulesRepository is not null
            && string.IsNullOrWhiteSpace(manifest.CommonModulesRepository))
        {
            throw new VbaProjectManifestException(
                $"Project manifest commonModulesRepository must be a non-empty path when present: {manifestName}");
        }

        foreach (var (name, document) in manifest.Documents)
        {
            if (document is null)
            {
                throw new VbaProjectManifestException(
                    $"Project manifest document '{name}' must be an object: {manifestName}");
            }

            ValidateDocument(name, document, manifestName);
        }

        ValidateCommandDefaults(manifest.CommandDefaults, manifestName);
    }

    private static void ValidateCommandDefaults(CommandDefaults? commandDefaults, string manifestName)
    {
        if (commandDefaults is not null
            && commandDefaults.Test is null
            && commandDefaults.ExcelAutomation is null)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults must contain at least one durable override: {manifestName}");
        }

        if (commandDefaults?.Test is not null
            && commandDefaults.Test.Format is null
            && commandDefaults.Test.ExecutionTimeoutSeconds is null)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults.test must contain at least one durable override: {manifestName}");
        }

        if (commandDefaults?.ExcelAutomation is not null
            && commandDefaults.ExcelAutomation.WorkbookOpenTimeoutSeconds is null
            && commandDefaults.ExcelAutomation.WorkbookSaveTimeoutSeconds is null)
        {
            throw new VbaProjectManifestException(
                $"commandDefaults.excelAutomation must contain at least one durable override: {manifestName}");
        }

        if (commandDefaults?.Test?.Format is string testFormat
            && !string.Equals(testFormat, "text", StringComparison.Ordinal)
            && !string.Equals(testFormat, "ndjson", StringComparison.Ordinal))
        {
            throw new VbaProjectManifestException(
                $"Unsupported commandDefaults.test.format '{testFormat}': {manifestName}");
        }

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

        if (!string.Equals(document.Kind, ProjectDocument.ExcelKind, StringComparison.Ordinal))
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

        if (document.CommonModules is null)
        {
            throw new VbaProjectManifestException(
                $"Document '{name}' must define the complete commonModules selection array: {manifestName}");
        }

        if (document.References is null)
        {
            throw new VbaProjectManifestException(
                $"Document '{name}' must define the complete references selection array: {manifestName}");
        }

        var commonModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commonModuleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var commonModule in document.CommonModules)
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

        foreach (var reference in document.References)
        {
            if (reference is null)
            {
                throw new VbaProjectManifestException(
                    $"Document '{name}' contains a null VBA project reference entry: {manifestName}");
            }

            if (string.IsNullOrWhiteSpace(reference.Name))
            {
                throw new VbaProjectManifestException($"Document '{name}' contains an empty VBA project reference name: {manifestName}");
            }
        }
    }

    private static bool IsSupportedVbaSourceFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".bas" or ".cls" or ".frm";
}
