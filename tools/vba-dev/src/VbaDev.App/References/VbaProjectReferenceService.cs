using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.References;

/// <summary>
/// Implements user-facing commands for listing and editing document VBA project references.
/// </summary>
public sealed class VbaProjectReferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ProjectManifestEditor manifestEditor;
    private readonly VbaProjectReferencePlanner referencePlanner;

    /// <summary>
    /// Creates the reference command service.
    /// </summary>
    /// <param name="manifestStore">The store used to persist reference changes to vba-project.json.</param>
    /// <param name="referencePlanner">The planner used to validate and resolve requested references.</param>
    public VbaProjectReferenceService(
        IProjectManifestStore manifestStore,
        VbaProjectReferencePlanner referencePlanner)
        : this(new ProjectManifestEditor(manifestStore), referencePlanner)
    {
    }

    /// <summary>
    /// Creates the reference command service.
    /// </summary>
    /// <param name="manifestEditor">The editor used to persist reference changes to vba-project.json.</param>
    /// <param name="referencePlanner">The planner used to validate and resolve requested references.</param>
    public VbaProjectReferenceService(
        ProjectManifestEditor manifestEditor,
        VbaProjectReferencePlanner referencePlanner)
    {
        this.manifestEditor = manifestEditor;
        this.referencePlanner = referencePlanner;
    }

    /// <summary>
    /// Adds references to the selected document manifest entry.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <returns>The command result describing manifest changes or validation errors.</returns>
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

        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
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
            manifestEditor.Save(context.ProjectRoot, context.Manifest);
        }

        return output.Length == 0
            ? CommandResult.Success("No VbaProjectReference changes." + Environment.NewLine)
            : CommandResult.Success(output.ToString());
    }

    /// <summary>
    /// Removes references from the selected document manifest entry.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The reference names to remove from vba-project.json.</param>
    /// <returns>The command result describing manifest changes.</returns>
    public CommandResult Remove(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError("reference remove requires at least one reference name.");
        }

        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
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
            manifestEditor.Save(context.ProjectRoot, context.Manifest);
        }

        return output.Length == 0
            ? CommandResult.Success("No VbaProjectReference changes." + Environment.NewLine)
            : CommandResult.Success(output.ToString());
    }

    /// <summary>
    /// Lists references tracked for the selected document.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <returns>The formatted command result.</returns>
    public CommandResult List(ResolvedProjectContext context, string format)
    {
        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
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

    private sealed record VbaProjectReferenceListOutput(
        string Document,
        IReadOnlyList<VbaProjectReference> References);
}
