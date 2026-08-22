using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
        var missingNames = normalizedNames
            .Where(referenceName => !document.References.Any(reference =>
                reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missingNames.Length == 0)
        {
            return CommandResult.Success("No VbaProjectReference changes." + Environment.NewLine);
        }

        VbaProjectReferenceResolutionBatch resolutionBatch;
        IReadOnlyList<ResolvedVbaProjectReference> resolvedReferences;
        try
        {
            resolutionBatch = referencePlanner.ResolveReferences(missingNames);
            resolvedReferences = referencePlanner.SelectManifestInputReferences(
                resolutionBatch,
                missingNames);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }

        var output = new StringBuilder();
        foreach (var reference in resolvedReferences)
        {
            document.References.Add(new VbaProjectReference(reference.Name));
            output.AppendLine($"Added {context.DocumentName}/{reference.Name}");
        }

        manifestEditor.Save(context.ProjectRoot, context.Manifest);

        return new CommandResult(
            0,
            output.ToString(),
            FormatWarnings(resolutionBatch.Warnings));
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
        var names = document.References
            .Select(reference => reference.Name)
            .ToArray();
        VbaProjectReferenceResolutionBatch batch;
        IReadOnlyList<VbaProjectReferenceListEntryOutput> references;
        try
        {
            batch = names.Length == 0
                ? new VbaProjectReferenceResolutionBatch(true, [], null, [])
                : referencePlanner.ResolveReferences(names);
            references = CreateListReferences(document.References, batch);
        }
        catch (InvalidOperationException exception)
        {
            return CommandResult.UsageError(exception.Message);
        }

        var warnings = batch.Warnings
            .Select(warning => new VbaProjectReferenceWarningOutput(warning.Code, warning.Message))
            .ToArray();
        var diagnostics = batch.Diagnostic is null
            ? null
            : new[]
            {
                new VbaProjectReferenceDiagnosticOutput(
                    batch.Diagnostic.Code,
                    batch.Diagnostic.Message)
            };
        var exitCode = batch.Complete
            && references.All(reference => reference.Status == "resolved")
                ? 0
                : 1;

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new VbaProjectReferenceListOutput(
                "1.0",
                "project",
                context.ProjectRoot,
                context.DocumentName,
                "configured",
                batch.Complete,
                warnings,
                references,
                diagnostics);
            return new CommandResult(
                exitCode,
                JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine,
                string.Empty);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Project: {context.ProjectRoot}");
        builder.AppendLine($"Document: {context.DocumentName}");
        builder.AppendLine("Configured references:");
        var resolvedReferences = references
            .Where(reference => reference.Status == "resolved")
            .ToArray();
        if (resolvedReferences.Length == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var reference in resolvedReferences)
            {
                builder.AppendLine($"  {reference.Name}");
            }
        }

        var issues = references
            .Where(reference => reference.Status != "resolved")
            .ToArray();
        if (issues.Length > 0)
        {
            builder.AppendLine("Resolution issues:");
            foreach (var issue in issues)
            {
                builder.AppendLine($"  {issue.Name} [{issue.Status}]: {issue.Message}");
            }
        }

        var standardError = new StringBuilder(FormatWarnings(batch.Warnings));
        if (batch.Diagnostic is not null)
        {
            standardError.AppendLine($"[ERROR] {batch.Diagnostic.Code}: {batch.Diagnostic.Message}");
        }

        return new CommandResult(
            exitCode,
            builder.ToString(),
            standardError.ToString());
    }

    private static string[] NormalizeNames(IReadOnlyList<string> referenceNames)
        => referenceNames
            .Select(referenceName => referenceName.Trim())
            .Where(referenceName => !string.IsNullOrWhiteSpace(referenceName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<VbaProjectReferenceListEntryOutput> CreateListReferences(
        IReadOnlyList<VbaProjectReference> manifestReferences,
        VbaProjectReferenceResolutionBatch batch)
    {
        if (!batch.Complete)
        {
            return [];
        }

        if (manifestReferences.Count != batch.References.Count)
        {
            throw new InvalidOperationException(
                "Reference resolver returned an incomplete configured-reference batch.");
        }

        for (var index = 0; index < manifestReferences.Count; index++)
        {
            if (!batch.References[index].RequestedName.Equals(
                    manifestReferences[index].Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Reference resolver returned an incomplete configured-reference batch.");
            }
        }

        return manifestReferences
            .Select((reference, index) => CreateListReference(reference.Name, batch.References[index]))
            .ToArray();
    }

    private static VbaProjectReferenceListEntryOutput CreateListReference(
        string manifestName,
        VbaProjectReferenceNameResolution resolution)
    {
        var candidates = resolution.Matches
            .Select(CreateIdentity)
            .Distinct()
            .OrderBy(identity => identity.Guid, StringComparer.Ordinal)
            .ThenBy(identity => identity.Major)
            .ThenBy(identity => identity.Minor)
            .ToArray();
        if (candidates.Length == 1)
        {
            return new VbaProjectReferenceListEntryOutput(
                manifestName,
                "resolved",
                candidates[0],
                null,
                null,
                null);
        }

        if (candidates.Length > 1)
        {
            return new VbaProjectReferenceListEntryOutput(
                manifestName,
                "ambiguous",
                null,
                "multipleUsableIdentities",
                candidates,
                "Multiple usable registered TypeLib identities matched this name.");
        }

        var reasonCode = resolution.IsRegistered
            ? "noUsableIdentity"
            : "notRegistered";
        var message = resolution.IsRegistered
            ? "The registered TypeLib description has no usable identity."
            : "No registered TypeLib description matched this name.";
        return new VbaProjectReferenceListEntryOutput(
            manifestName,
            "unavailable",
            null,
            reasonCode,
            Array.Empty<VbaProjectReferenceIdentityOutput>(),
            message);
    }

    private static VbaProjectReferenceIdentityOutput CreateIdentity(
        ResolvedVbaProjectReference reference)
    {
        if (!Guid.TryParse(reference.Guid, out var guid) ||
            reference.Major is < ushort.MinValue or > ushort.MaxValue ||
            reference.Minor is < ushort.MinValue or > ushort.MaxValue)
        {
            throw new InvalidOperationException(
                $"Reference resolver returned an invalid identity for '{reference.Name}'.");
        }

        return new VbaProjectReferenceIdentityOutput(
            guid.ToString("D").ToLowerInvariant(),
            reference.Major,
            reference.Minor);
    }

    private static string FormatWarnings(
        IReadOnlyList<VbaTools.TypeLibRegistry.TypeLibRegistryCatalogWarning> warnings)
        => string.Concat(warnings.Select(warning =>
            $"[WARN] {warning.Code}: {warning.Message}{Environment.NewLine}"));

    private sealed record VbaProjectReferenceListOutput(
        string SchemaVersion,
        string Scope,
        string Project,
        string Document,
        string Mode,
        bool Complete,
        IReadOnlyList<VbaProjectReferenceWarningOutput> Warnings,
        IReadOnlyList<VbaProjectReferenceListEntryOutput> References,
        IReadOnlyList<VbaProjectReferenceDiagnosticOutput>? Diagnostics);

    private sealed record VbaProjectReferenceWarningOutput(string Code, string Message);

    private sealed record VbaProjectReferenceDiagnosticOutput(string Code, string Message);

    private sealed record VbaProjectReferenceListEntryOutput(
        string Name,
        string Status,
        VbaProjectReferenceIdentityOutput? Identity,
        string? ReasonCode,
        IReadOnlyList<VbaProjectReferenceIdentityOutput>? Candidates,
        string? Message);

    private sealed record VbaProjectReferenceIdentityOutput(string Guid, int Major, int Minor);
}
