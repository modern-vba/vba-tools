using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.References;

public sealed class VbaProjectReferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IProjectManifestStore manifestStore;
    private readonly VbaProjectReferencePlanner referencePlanner;

    public VbaProjectReferenceService(
        IProjectManifestStore manifestStore,
        VbaProjectReferencePlanner referencePlanner)
    {
        this.manifestStore = manifestStore;
        this.referencePlanner = referencePlanner;
    }

    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError("reference add requires at least one reference name.");
        }

        IReadOnlyList<ResolvedVbaProjectReference> resolvedReferences;
        try
        {
            resolvedReferences = referencePlanner.ResolveManifestInputReferences(normalizedNames);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }

        var document = GetDocument(context.Manifest, context.DocumentName);
        var output = new StringBuilder();
        var changed = false;
        foreach (var reference in resolvedReferences)
        {
            if (document.References.Any(existingReference => existingReference.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            document.References.Add(new VbaProjectReference(reference.Name));
            output.AppendLine($"Added {context.DocumentName}/{reference.Name}");
            changed = true;
        }

        if (changed)
        {
            manifestStore.Save(context.ProjectRoot, context.Manifest);
        }

        return output.Length == 0
            ? CommandResult.Success("No VbaProjectReference changes." + Environment.NewLine)
            : CommandResult.Success(output.ToString());
    }

    public CommandResult Remove(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError("reference remove requires at least one reference name.");
        }

        var document = GetDocument(context.Manifest, context.DocumentName);
        var output = new StringBuilder();
        var changed = false;
        foreach (var referenceName in normalizedNames)
        {
            var removed = document.References.RemoveAll(reference => reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                output.AppendLine($"Removed {context.DocumentName}/{referenceName}");
                changed = true;
            }
        }

        if (changed)
        {
            manifestStore.Save(context.ProjectRoot, context.Manifest);
        }

        return output.Length == 0
            ? CommandResult.Success("No VbaProjectReference changes." + Environment.NewLine)
            : CommandResult.Success(output.ToString());
    }

    public CommandResult List(ResolvedProjectContext context, string format)
    {
        var document = GetDocument(context.Manifest, context.DocumentName);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new VbaProjectReferenceListOutput(context.DocumentName, document.References);
            return CommandResult.Success(JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Document: {context.DocumentName}");
        builder.AppendLine("References:");
        if (document.References.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var reference in document.References)
            {
                builder.AppendLine($"  {reference.Name}");
            }
        }

        return CommandResult.Success(builder.ToString());
    }

    private static string[] NormalizeNames(IReadOnlyList<string> referenceNames)
        => referenceNames
            .Select(referenceName => referenceName.Trim())
            .Where(referenceName => !string.IsNullOrWhiteSpace(referenceName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static ProjectDocument GetDocument(ProjectManifest manifest, string documentName)
    {
        if (manifest.Documents.TryGetValue(documentName, out var document))
        {
            return document;
        }

        return manifest.Documents
            .First(item => item.Key.Equals(documentName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private sealed record VbaProjectReferenceListOutput(
        string Document,
        IReadOnlyList<VbaProjectReference> References);
}
