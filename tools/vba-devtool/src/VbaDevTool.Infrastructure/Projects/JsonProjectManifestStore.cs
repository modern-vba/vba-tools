using System.Text;
using System.Text.Json;
using VbaDevTools.App.Projects;
using VbaDevTools.Domain;

namespace VbaDevTools.Infrastructure.Projects;

public sealed class JsonProjectManifestStore : IProjectManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);

    public ProjectManifest Load(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = reader.ReadToEnd();
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions);
            if (manifest is null)
            {
                throw new ProjectManifestException($"Project manifest is empty: {manifestPath}");
            }

            Validate(manifest, manifestPath);
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be parsed: {manifestPath}", ex);
        }
        catch (IOException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be read: {manifestPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProjectManifestException($"Project manifest could not be read: {manifestPath}", ex);
        }
    }

    public void Save(string projectRoot, ProjectManifest manifest)
    {
        Validate(manifest, ProjectManifest.ManifestFileName);
        Directory.CreateDirectory(projectRoot);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(manifestPath, json + Environment.NewLine, Utf16LeWithBom);
    }

    private static void Validate(ProjectManifest manifest, string manifestPath)
    {
        if (manifest.SchemaVersion != ProjectManifest.CurrentSchemaVersion)
        {
            throw new ProjectManifestException($"Unsupported schemaVersion '{manifest.SchemaVersion}' in {manifestPath}. Expected schemaVersion 1.");
        }

        if (string.IsNullOrWhiteSpace(manifest.ProjectName))
        {
            throw new ProjectManifestException($"Project manifest is missing projectName: {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.PrimaryDocument))
        {
            throw new ProjectManifestException($"Project manifest is missing primaryDocument: {manifestPath}");
        }

        if (manifest.Documents.Count == 0)
        {
            throw new ProjectManifestException($"Project manifest must define at least one document: {manifestPath}");
        }

        if (!ContainsDocument(manifest, manifest.PrimaryDocument))
        {
            throw new ProjectManifestException($"primaryDocument '{manifest.PrimaryDocument}' is not defined in documents: {manifestPath}");
        }

        foreach (var (name, document) in manifest.Documents)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ProjectManifestException($"Project manifest contains an empty document name: {manifestPath}");
            }

            if (!string.Equals(document.Kind, ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectManifestException($"Unsupported document kind '{document.Kind}' for document '{name}': {manifestPath}");
            }

            if (string.IsNullOrWhiteSpace(document.SourcePath) ||
                string.IsNullOrWhiteSpace(document.TemplatePath) ||
                string.IsNullOrWhiteSpace(document.BinPath) ||
                string.IsNullOrWhiteSpace(document.PublishPath))
            {
                throw new ProjectManifestException($"Document '{name}' must define sourcePath, templatePath, binPath, and publishPath: {manifestPath}");
            }

            foreach (var commonModule in document.CommonModules)
            {
                if (string.IsNullOrWhiteSpace(commonModule.Name))
                {
                    throw new ProjectManifestException($"Document '{name}' contains an empty CommonModules name: {manifestPath}");
                }
            }

            foreach (var reference in document.References)
            {
                if (string.IsNullOrWhiteSpace(reference.Name))
                {
                    throw new ProjectManifestException($"Document '{name}' contains an empty VBA project reference name: {manifestPath}");
                }
            }
        }
    }

    private static bool ContainsDocument(ProjectManifest manifest, string documentName)
        => manifest.Documents.Keys.Any(name => string.Equals(name, documentName, StringComparison.OrdinalIgnoreCase));
}
