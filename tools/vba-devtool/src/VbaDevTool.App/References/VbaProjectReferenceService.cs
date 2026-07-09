using System.Text;
using System.Text.Json;
using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Domain;

namespace VbaDevTools.App.References;

public sealed class VbaProjectReferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IProjectManifestStore manifestStore;
    private readonly IVbaProjectReferenceResolver referenceResolver;

    public VbaProjectReferenceService(
        IProjectManifestStore manifestStore,
        IVbaProjectReferenceResolver referenceResolver)
    {
        this.manifestStore = manifestStore;
        this.referenceResolver = referenceResolver;
    }

    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError("reference add requires at least one reference name.");
        }

        var resolvedReferences = new List<ResolvedVbaProjectReference>();
        foreach (var referenceName in normalizedNames)
        {
            var matches = referenceResolver.Resolve(referenceName);
            if (matches.Count == 0)
            {
                return CommandResult.UsageError($"VbaProjectReference '{referenceName}' was not found.");
            }

            if (matches.Count > 1)
            {
                var candidates = string.Join(
                    ", ",
                    matches.Select(match => $"{match.Name} ({match.Guid} {match.Major}.{match.Minor})"));
                return CommandResult.UsageError($"VbaProjectReference '{referenceName}' is ambiguous: {candidates}.");
            }

            resolvedReferences.Add(matches[0]);
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
