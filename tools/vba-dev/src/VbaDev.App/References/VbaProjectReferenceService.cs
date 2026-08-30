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
    private static readonly HashSet<string> UnverifiedReasonCodes = new(StringComparer.Ordinal)
    {
        "excelVbeFailure",
        "probeTimeout",
        "identityReadFailure",
        "cleanupFailure",
        "probeAborted",
        "cancelled"
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly VbaProjectReferencePlanner referencePlanner;
    private readonly IProjectManifestMutationCoordinator manifestMutationCoordinator;
    private readonly IFileSystemPathIdentityResolver pathIdentityResolver;

    /// <summary>
    /// Creates the reference command service.
    /// </summary>
    /// <param name="referencePlanner">The planner used to validate and resolve requested references.</param>
    /// <param name="manifestMutationCoordinator">The shared rebased manifest mutation boundary.</param>
    /// <param name="pathIdentityResolver">The source-template identity resolver.</param>
    public VbaProjectReferenceService(
        VbaProjectReferencePlanner referencePlanner,
        IProjectManifestMutationCoordinator manifestMutationCoordinator,
        IFileSystemPathIdentityResolver? pathIdentityResolver = null)
    {
        this.referencePlanner = referencePlanner;
        this.manifestMutationCoordinator = manifestMutationCoordinator;
        this.pathIdentityResolver = pathIdentityResolver ?? new FileSystemPathIdentityResolver();
    }

    /// <summary>
    /// Adds references to the selected document manifest entry.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <returns>The command result describing manifest changes or validation errors.</returns>
    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
        => AddAsync(context, referenceNames, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Adds references after completing any required VBE-equivalent ambiguity probe.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The command result describing manifest changes or validation errors.</returns>
    public async Task<CommandResult> AddAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> referenceNames,
        CancellationToken cancellationToken)
        => await AddAsync(context, referenceNames, "text", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Adds references and renders the requested mutation-result format.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The requested human-visible reference names.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The command result describing manifest changes or validation errors.</returns>
    public async Task<CommandResult> AddAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> referenceNames,
        string format,
        CancellationToken cancellationToken)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError("reference add requires at least one reference name.");
        }

        if (normalizedNames.Any(VbaProjectReferenceName.IsStandardLibrary))
        {
            return CommandResult.UsageError(
                VbaProjectReferenceName.StandardLibrarySelectionError);
        }

        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
        var invocationStartPresentNames = normalizedNames
            .Where(referenceName => document.References.Any(reference =>
                reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNames = normalizedNames
            .Where(referenceName => !invocationStartPresentNames.Contains(referenceName))
            .ToArray();
        var invocationStartTemplateIdentity = missingNames.Length == 0
            ? null
            : pathIdentityResolver.Resolve(context.TemplateDocumentPath);
        var resolutionWarnings = Array.Empty<VbaProjectReferenceWarningOutput>();
        var resolvedByRequestedName = new Dictionary<string, ResolvedVbaProjectReference>(
            StringComparer.OrdinalIgnoreCase);
        if (missingNames.Length > 0)
        {
            try
            {
                var resolutionBatch = await referencePlanner.ResolveReferencesAsync(
                        context,
                        missingNames,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return CommandResult.Cancelled(
                        "Reference add was cancelled before the manifest update.");
                }

                var resolvedReferences = referencePlanner.SelectManifestInputReferences(
                    resolutionBatch,
                    missingNames);
                resolvedByRequestedName = missingNames
                    .Zip(resolvedReferences)
                    .ToDictionary(
                        pair => pair.First,
                        pair => pair.Second,
                        StringComparer.OrdinalIgnoreCase);
                resolutionWarnings = resolutionBatch.Warnings
                    .Select(warning => new VbaProjectReferenceWarningOutput(
                        warning.Code,
                        warning.Message))
                    .ToArray();
            }
            catch (InvalidOperationException ex)
            {
                return CommandResult.UsageError(ex.Message);
            }
        }

        ProjectManifestMutationOutcome<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>> outcome;
        try
        {
            outcome = await manifestMutationCoordinator.ExecuteAsync(
                    context.ProjectRoot,
                    ProjectManifestMutationCommand.ReferenceAdd,
                    snapshot => RebaseAdd(
                        snapshot,
                        context.DocumentName,
                        normalizedNames,
                        invocationStartPresentNames,
                        resolvedByRequestedName,
                        invocationStartTemplateIdentity,
                        pathIdentityResolver),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(
                "Reference add was cancelled before the manifest update.");
        }
        catch (ProjectManifestMutationException ex)
        {
            return CommandResult.UsageError($"[{ex.Code}] {ex.Message}");
        }

        var warnings = resolutionWarnings
            .Concat(outcome.Warnings.Select(warning =>
                new VbaProjectReferenceWarningOutput(warning.Code, warning.Message)))
            .ToArray();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return RenderMutationJson(context, "add", warnings, outcome.Result);
        }

        return RenderMutationText(
            context.DocumentName,
            "add",
            warnings,
            outcome.Result);
    }

    /// <summary>
    /// Removes references from the selected document manifest entry.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The reference names to remove from vba-project.json.</param>
    /// <returns>The command result describing manifest changes.</returns>
    public CommandResult Remove(ResolvedProjectContext context, IReadOnlyList<string> referenceNames)
        => RemoveAsync(context, referenceNames, "text", CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Removes references and renders the requested mutation-result format.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="referenceNames">The reference names to remove from vba-project.json.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The command result describing manifest changes.</returns>
    public async Task<CommandResult> RemoveAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> referenceNames,
        string format,
        CancellationToken cancellationToken)
    {
        var normalizedNames = NormalizeNames(referenceNames);
        if (normalizedNames.Length == 0)
        {
            return CommandResult.UsageError(
                "reference remove requires at least one reference name.");
        }

        if (normalizedNames.Any(VbaProjectReferenceName.IsStandardLibrary))
        {
            return CommandResult.UsageError(
                VbaProjectReferenceName.StandardLibrarySelectionError);
        }

        ProjectManifestMutationOutcome<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>> outcome;
        try
        {
            outcome = await manifestMutationCoordinator.ExecuteAsync(
                    context.ProjectRoot,
                    ProjectManifestMutationCommand.ReferenceRemove,
                    snapshot => RebaseRemove(
                        snapshot,
                        context.DocumentName,
                        normalizedNames),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(
                "Reference remove was cancelled before the manifest update.");
        }
        catch (ProjectManifestMutationException ex)
        {
            return CommandResult.UsageError($"[{ex.Code}] {ex.Message}");
        }

        var warnings = outcome.Warnings
            .Select(warning => new VbaProjectReferenceWarningOutput(
                warning.Code,
                warning.Message))
            .ToArray();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return RenderMutationJson(
                context,
                "remove",
                warnings,
                outcome.Result);
        }

        return RenderMutationText(
            context.DocumentName,
            "remove",
            warnings,
            outcome.Result);
    }

    /// <summary>
    /// Lists references tracked for the selected document.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <returns>The formatted command result.</returns>
    public CommandResult List(ResolvedProjectContext context, string format)
        => ListAsync(context, format, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Lists the selected document's stored references without environment resolution.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <returns>The formatted stored-reference selection.</returns>
    public CommandResult ListSelection(ResolvedProjectContext context, string format)
    {
        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
        var references = document.References
            .Select(reference => new VbaProjectReferenceSelectionEntryOutput(reference.Name))
            .ToArray();
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new VbaProjectReferenceSelectionOutput(
                "1.0",
                "project",
                Path.GetFullPath(context.ProjectRoot),
                context.DocumentName,
                "selection",
                Complete: true,
                Warnings: [],
                references);
            return CommandResult.Success(
                JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Scope: project");
        builder.AppendLine($"Project: {Path.GetFullPath(context.ProjectRoot)}");
        builder.AppendLine($"Document: {context.DocumentName}");
        builder.AppendLine("Configured references:");
        if (references.Length == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var reference in references)
            {
                builder.AppendLine($"  {reference.Name}");
            }
        }

        return CommandResult.Success(builder.ToString());
    }

    /// <summary>
    /// Lists registered references not already selected by the document.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The formatted available-reference inventory.</returns>
    public async Task<CommandResult> ListAvailableAsync(
        ResolvedProjectContext context,
        string format,
        CancellationToken cancellationToken)
    {
        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
        VbaProjectReferenceResolutionBatch batch;
        IReadOnlyList<VbaProjectReferenceListEntryOutput> references;
        try
        {
            batch = await referencePlanner.ResolveAvailableReferencesAsync(
                    context.TemplateDocumentPath,
                    document.References.Select(reference => reference.Name).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            references = CreateAvailableListReferences(batch);
        }
        catch (InvalidOperationException exception)
        {
            return CommandResult.UsageError(exception.Message);
        }

        return RenderList(
            "project",
            context.ProjectRoot,
            context.DocumentName,
            "available",
            batch,
            references,
            format);
    }

    /// <summary>
    /// Lists every registered reference description when no project manifest can be discovered.
    /// </summary>
    /// <param name="format">The output format, either text or json.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The formatted environment-reference inventory.</returns>
    public async Task<CommandResult> ListAvailableEnvironmentAsync(
        string format,
        CancellationToken cancellationToken)
    {
        VbaProjectReferenceResolutionBatch batch;
        IReadOnlyList<VbaProjectReferenceListEntryOutput> references;
        try
        {
            batch = await referencePlanner.ResolveAvailableReferencesAsync(
                    VbaProjectReferenceProbeBaseline.BlankWorkbook,
                    [],
                    cancellationToken)
                .ConfigureAwait(false);
            references = CreateAvailableListReferences(batch);
        }
        catch (InvalidOperationException exception)
        {
            return CommandResult.UsageError(exception.Message);
        }

        var warning = new VbaProjectReferenceWarningOutput(
            "projectManifestNotFound",
            "No vba-project.json was found; references were listed for the current environment.");
        return RenderList(
            "environment",
            null,
            null,
            "available",
            batch,
            references,
            format,
            [warning]);
    }

    /// <summary>
    /// Lists references after completing any required VBE-equivalent ambiguity probe.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <param name="cancellationToken">The cooperative command cancellation token.</param>
    /// <returns>The formatted command result.</returns>
    public async Task<CommandResult> ListAsync(
        ResolvedProjectContext context,
        string format,
        CancellationToken cancellationToken)
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
                : await referencePlanner.ResolveReferencesAsync(
                        context,
                        names,
                        cancellationToken)
                    .ConfigureAwait(false);
            references = CreateListReferences(document.References, batch);
        }
        catch (InvalidOperationException exception)
        {
            return CommandResult.UsageError(exception.Message);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(
                "Reference list was cancelled before output was published.");
        }

        return RenderList(
            "project",
            context.ProjectRoot,
            context.DocumentName,
            "configured",
            batch,
            references,
            format);
    }

    private static CommandResult RenderList(
        string scope,
        string? project,
        string? document,
        string mode,
        VbaProjectReferenceResolutionBatch batch,
        IReadOnlyList<VbaProjectReferenceListEntryOutput> references,
        string format,
        IReadOnlyList<VbaProjectReferenceWarningOutput>? additionalWarnings = null)
    {
        var warnings = (additionalWarnings ?? [])
            .Concat(batch.Warnings.Select(warning =>
                new VbaProjectReferenceWarningOutput(warning.Code, warning.Message)))
            .ToArray();
        var diagnostics = batch.Diagnostics.Count == 0
            ? null
            : batch.Diagnostics
                .Select(diagnostic => new VbaProjectReferenceDiagnosticOutput(
                    diagnostic.Code,
                    diagnostic.Message))
                .ToArray();
        var complete = batch.Complete &&
                       diagnostics is null &&
                       references.All(reference => reference.Status != "unverified");
        var exitCode = complete &&
                       (mode.Equals("available", StringComparison.Ordinal) ||
                        references.All(reference => reference.Status == "resolved"))
            ? 0
            : 1;

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new VbaProjectReferenceListOutput(
                "1.0",
                scope,
                project,
                document,
                mode,
                complete,
                warnings,
                references,
                diagnostics);
            return new CommandResult(
                exitCode,
                JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine,
                FormatWarnings(additionalWarnings ?? []));
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Scope: {scope}");
        if (project is not null)
        {
            builder.AppendLine($"Project: {project}");
        }

        if (document is not null)
        {
            builder.AppendLine($"Document: {document}");
        }

        builder.AppendLine(mode.Equals("available", StringComparison.Ordinal)
            ? "Available references:"
            : "Configured references:");
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

        var standardError = new StringBuilder(FormatWarnings(warnings));
        foreach (var diagnostic in batch.Diagnostics)
        {
            standardError.AppendLine($"[ERROR] {diagnostic.Code}: {diagnostic.Message}");
        }

        return new CommandResult(
            exitCode,
            builder.ToString(),
            standardError.ToString());
    }

    private static CommandResult RenderMutationJson(
        ResolvedProjectContext context,
        string operation,
        IReadOnlyList<VbaProjectReferenceWarningOutput> warnings,
        IReadOnlyList<VbaProjectReferenceMutationEntryOutput> results)
    {
        var output = new VbaProjectReferenceMutationOutput(
            "1.0",
            "project",
            Path.GetFullPath(context.ProjectRoot),
            context.DocumentName,
            operation,
            Complete: true,
            warnings,
            results);
        return CommandResult.Success(
            JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
    }

    private static CommandResult RenderMutationText(
        string documentName,
        string operation,
        IReadOnlyList<VbaProjectReferenceWarningOutput> warnings,
        IReadOnlyList<VbaProjectReferenceMutationEntryOutput> results)
    {
        var output = new StringBuilder();
        output.AppendLine($"Reference {operation} ({documentName}):");
        foreach (var result in results)
        {
            var label = result.Status switch
            {
                "added" => "Added",
                "promoted" => "Marked as directly requested",
                "alreadyPresent" => "Already present",
                "removed" => "Removed",
                "alreadyAbsent" => "Already absent",
                _ => throw new InvalidOperationException(
                    $"Unsupported reference mutation status '{result.Status}'.")
            };
            var displayName = result.StoredName ?? result.RequestedName;
            if (result.StoredName is not null
                && !result.StoredName.Equals(result.RequestedName, StringComparison.Ordinal))
            {
                displayName += $" (requested as {result.RequestedName})";
            }

            output.AppendLine($"  {label}: {displayName}");
        }

        if (operation.Equals("add", StringComparison.Ordinal))
        {
            output.AppendLine($"Added: {results.Count(result => result.Status == "added")}");
            output.AppendLine($"Promoted: {results.Count(result => result.Status == "promoted")}");
        }
        else
        {
            output.AppendLine($"Removed: {results.Count(result => result.Status == "removed")}");
        }

        output.AppendLine($"Unchanged: {results.Count(result =>
            result.Status is "alreadyPresent" or "alreadyAbsent")}");
        return new CommandResult(
            0,
            output.ToString(),
            FormatWarnings(warnings));
    }

    private static ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>> RebaseRemove(
        ProjectManifestMutationSnapshot snapshot,
        string documentName,
        IReadOnlyList<string> normalizedNames)
    {
        if (!snapshot.Manifest.Documents.Keys.Any(name =>
                name.Equals(documentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProjectManifestException(
                $"Document '{documentName}' no longer exists in the latest project manifest: {snapshot.ManifestPath}");
        }

        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var document = ProjectManifestEditor.GetDocument(plannedManifest, documentName);
        var results = new List<VbaProjectReferenceMutationEntryOutput>();
        var changed = false;
        foreach (var referenceName in normalizedNames)
        {
            var storedReference = document.References.FirstOrDefault(reference =>
                reference.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase));
            if (storedReference is null)
            {
                results.Add(new VbaProjectReferenceMutationEntryOutput(
                    referenceName,
                    StoredName: null,
                    "alreadyAbsent"));
                continue;
            }

            document.References.Remove(storedReference);
            changed = true;
            results.Add(new VbaProjectReferenceMutationEntryOutput(
                referenceName,
                storedReference.Name,
                "removed"));
        }

        return changed
            ? ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>>.Commit(
                plannedManifest,
                results)
            : ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>>.NoOp(
                results);
    }

    private static ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>> RebaseAdd(
        ProjectManifestMutationSnapshot snapshot,
        string documentName,
        IReadOnlyList<string> normalizedNames,
        IReadOnlySet<string> invocationStartPresentNames,
        IReadOnlyDictionary<string, ResolvedVbaProjectReference> resolvedByRequestedName,
        FileSystemPathIdentity? invocationStartTemplateIdentity,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        if (!snapshot.Manifest.Documents.Keys.Any(name =>
                name.Equals(documentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProjectManifestException(
                $"Document '{documentName}' no longer exists in the latest project manifest: {snapshot.ManifestPath}");
        }

        var plannedManifest = ProjectManifestEditor.Clone(snapshot.Manifest);
        var document = ProjectManifestEditor.GetDocument(plannedManifest, documentName);
        var results = new List<VbaProjectReferenceMutationEntryOutput>();
        var changed = false;
        foreach (var requestedName in normalizedNames)
        {
            var storedIndex = document.References.FindIndex(reference =>
                reference.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
            if (storedIndex >= 0)
            {
                var storedReference = document.References[storedIndex];
                if (!storedReference.Requested)
                {
                    document.References[storedIndex] = storedReference with
                    {
                        Requested = true
                    };
                    changed = true;
                    results.Add(new VbaProjectReferenceMutationEntryOutput(
                        requestedName,
                        storedReference.Name,
                        "promoted"));
                }
                else
                {
                    results.Add(new VbaProjectReferenceMutationEntryOutput(
                        requestedName,
                        storedReference.Name,
                        "alreadyPresent"));
                }

                continue;
            }

            if (invocationStartPresentNames.Contains(requestedName))
            {
                throw new ProjectManifestMutationException(
                    "referenceSelectionChanged",
                    $"Reference '{requestedName}' was removed after reference add began.");
            }

            var resolvedReference = resolvedByRequestedName[requestedName];
            document.References.Add(new VbaProjectReference(resolvedReference.Name));
            changed = true;
            results.Add(new VbaProjectReferenceMutationEntryOutput(
                requestedName,
                resolvedReference.Name,
                "added"));
        }

        if (results.Any(result => result.Status == "added"))
        {
            var latestTemplatePath = Path.IsPathRooted(document.TemplatePath)
                ? Path.GetFullPath(document.TemplatePath)
                : Path.GetFullPath(document.TemplatePath, snapshot.ProjectRoot);
            var latestTemplateIdentity = pathIdentityResolver.Resolve(latestTemplatePath);
            if (invocationStartTemplateIdentity is null
                || !FileSystemPathIdentityRelations.Same(
                    invocationStartTemplateIdentity,
                    latestTemplateIdentity))
            {
                throw new ProjectManifestMutationException(
                    "referenceSourceTemplateChanged",
                    $"Document '{documentName}' selected a different source template after reference add began.");
            }
        }

        return changed
            ? ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>>.Commit(
                plannedManifest,
                results)
            : ProjectManifestMutationPlan<IReadOnlyList<VbaProjectReferenceMutationEntryOutput>>.NoOp(
                results);
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

    private static IReadOnlyList<VbaProjectReferenceListEntryOutput> CreateAvailableListReferences(
        VbaProjectReferenceResolutionBatch batch)
    {
        var canonicalReferences = batch.References
            .Select(reference =>
            {
                if (!reference.IsRegistered || string.IsNullOrWhiteSpace(reference.RegisteredName))
                {
                    throw new InvalidOperationException(
                        "Available-reference resolution returned a name that was not registered.");
                }

                return (
                    Name: reference.RegisteredName.Trim(),
                    Resolution: reference);
            })
            .ToArray();
        var duplicate = canonicalReferences
            .GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Available-reference resolution returned a duplicate name: '{duplicate.Key}'.");
        }

        return canonicalReferences
            .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => CreateListReference(
                reference.Name,
                reference.Resolution))
            .ToArray();
    }

    private static VbaProjectReferenceListEntryOutput CreateListReference(
        string manifestName,
        VbaProjectReferenceNameResolution resolution)
    {
        var matches = resolution.Matches
            .Select(CreateIdentity)
            .Distinct()
            .OrderBy(identity => identity.Guid, StringComparer.Ordinal)
            .ThenBy(identity => identity.Major)
            .ThenBy(identity => identity.Minor)
            .ToArray();
        var candidates = resolution.Candidates
            .Select(CreateIdentity)
            .Distinct()
            .OrderBy(identity => identity.Guid, StringComparer.Ordinal)
            .ThenBy(identity => identity.Major)
            .ThenBy(identity => identity.Minor)
            .ToArray();
        if (resolution.UnverifiedReasonCode is not null)
        {
            if (!UnverifiedReasonCodes.Contains(resolution.UnverifiedReasonCode))
            {
                throw new InvalidOperationException(
                    $"Reference resolver returned an unknown unverified reason code: '{resolution.UnverifiedReasonCode}'.");
            }

            return new VbaProjectReferenceListEntryOutput(
                manifestName,
                "unverified",
                null,
                resolution.UnverifiedReasonCode,
                candidates,
                resolution.Message ?? "Reference verification did not complete.");
        }

        if (matches.Length == 1)
        {
            return new VbaProjectReferenceListEntryOutput(
                manifestName,
                "resolved",
                matches[0],
                null,
                null,
                null);
        }

        if (matches.Length > 1)
        {
            return new VbaProjectReferenceListEntryOutput(
                manifestName,
                "ambiguous",
                null,
                "multipleUsableIdentities",
                matches,
                $"Multiple usable TypeLib identities matched this name: {FormatIdentities(matches)}.");
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
            candidates,
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

    private static string FormatIdentities(
        IReadOnlyList<VbaProjectReferenceIdentityOutput> identities)
        => string.Join(
            ", ",
            identities.Select(identity =>
                $"{identity.Guid} {identity.Major}.{identity.Minor}"));

    private static string FormatWarnings(
        IReadOnlyList<VbaTools.TypeLibRegistry.TypeLibRegistryCatalogWarning> warnings)
        => string.Concat(warnings.Select(warning =>
            $"[WARN] {warning.Code}: {warning.Message}{Environment.NewLine}"));

    private static string FormatWarnings(
        IReadOnlyList<VbaProjectReferenceWarningOutput> warnings)
        => string.Concat(warnings.Select(warning =>
            $"[WARN] {warning.Code}: {warning.Message}{Environment.NewLine}"));

    private sealed record VbaProjectReferenceListOutput(
        string SchemaVersion,
        string Scope,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Project,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Document,
        string Mode,
        bool Complete,
        IReadOnlyList<VbaProjectReferenceWarningOutput> Warnings,
        IReadOnlyList<VbaProjectReferenceListEntryOutput> References,
        IReadOnlyList<VbaProjectReferenceDiagnosticOutput>? Diagnostics);

    private sealed record VbaProjectReferenceWarningOutput(string Code, string Message);

    private sealed record VbaProjectReferenceSelectionOutput(
        string SchemaVersion,
        string Scope,
        string Project,
        string Document,
        string Mode,
        bool Complete,
        IReadOnlyList<VbaProjectReferenceWarningOutput> Warnings,
        IReadOnlyList<VbaProjectReferenceSelectionEntryOutput> References);

    private sealed record VbaProjectReferenceSelectionEntryOutput(string Name);

    private sealed record VbaProjectReferenceMutationOutput(
        string SchemaVersion,
        string Scope,
        string Project,
        string Document,
        string Operation,
        bool Complete,
        IReadOnlyList<VbaProjectReferenceWarningOutput> Warnings,
        IReadOnlyList<VbaProjectReferenceMutationEntryOutput> Results);

    private sealed record VbaProjectReferenceMutationEntryOutput(
        string RequestedName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StoredName,
        string Status);

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
