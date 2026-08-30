using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Implements the user-facing CommonModules command operations.
/// </summary>
public sealed class CommonModulesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly CommonModulesInstallationTransaction installationTransaction;

    /// <summary>
    /// Creates the CommonModules command service.
    /// </summary>
    /// <param name="installationTransaction">The transaction that applies source file and manifest changes.</param>
    public CommonModulesService(CommonModulesInstallationTransaction installationTransaction)
    {
        this.installationTransaction = installationTransaction;
    }

    /// <summary>
    /// Adds requested CommonModules entries to the current document source set.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="requestedModules">The requested module names or file names.</param>
    /// <param name="force">Whether existing target source files may be overwritten.</param>
    /// <returns>The command result to print and return from the CLI.</returns>
    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules, bool force)
        => AddAsync(context, requestedModules, force, "text", CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Adds requested CommonModules after completing required-reference preflight.
    /// </summary>
    public async Task<CommandResult> AddAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> requestedModules,
        bool force,
        CancellationToken cancellationToken)
        => await AddAsync(
                context,
                requestedModules,
                force,
                "text",
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Adds requested CommonModules and renders the selected success format.
    /// </summary>
    public async Task<CommandResult> AddAsync(
        ResolvedProjectContext context,
        IReadOnlyList<string> requestedModules,
        bool force,
        string format,
        CancellationToken cancellationToken)
        => await RunTransactionAsync(
                () => installationTransaction.AddAsync(
                    context,
                    requestedModules,
                    force,
                    cancellationToken),
                context.ProjectRoot,
                context.DocumentName,
                "add",
                format,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Lists the CommonModules entries tracked for the current document.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <param name="format">The output format, either text or json.</param>
    /// <returns>The formatted command result.</returns>
    public CommandResult List(ResolvedProjectContext context, string format)
    {
        var document = ProjectManifestEditor.GetDocument(context.Manifest, context.DocumentName);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new CommonModuleListOutput(context.DocumentName, document.CommonModules);
            return CommandResult.Success(JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Document: {context.DocumentName}");
        builder.AppendLine("CommonModules:");
        if (document.CommonModules.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var module in document.CommonModules)
            {
                builder.AppendLine($"  {module.Name} (requested: {module.Requested.ToString().ToLowerInvariant()})");
            }
        }

        return CommandResult.Success(builder.ToString());
    }

    /// <summary>
    /// Updates all installed CommonModules entries in the project.
    /// </summary>
    /// <param name="project">The resolved project to update.</param>
    /// <returns>The command result to print and return from the CLI.</returns>
    public CommandResult Update(ResolvedProject project)
        => UpdateAsync(project, "text", CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Updates installed CommonModules after required-reference preflight.
    /// </summary>
    public async Task<CommandResult> UpdateAsync(
        ResolvedProject project,
        CancellationToken cancellationToken)
        => await UpdateAsync(project, "text", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Updates installed CommonModules and renders the selected success format.
    /// </summary>
    public async Task<CommandResult> UpdateAsync(
        ResolvedProject project,
        string format,
        CancellationToken cancellationToken)
        => await RunTransactionAsync(
                () => installationTransaction.UpdateAsync(project, cancellationToken),
                project.ProjectRoot,
                document: null,
                "update",
                format,
                cancellationToken)
            .ConfigureAwait(false);

    private static async Task<CommandResult> RunTransactionAsync(
        Func<Task<CommonModulesTransactionCompletion>> execute,
        string projectRoot,
        string? document,
        string operation,
        string format,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!format.Equals("text", StringComparison.OrdinalIgnoreCase)
                && !format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError(
                    "CommonModules mutation format must be either text or json.");
            }

            var completion = await execute().ConfigureAwait(false);
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                var output = new CommonModulesMutationOutput(
                    SchemaVersion: "1.0",
                    Scope: "project",
                    Project: Path.GetFullPath(projectRoot),
                    Document: document,
                    Operation: operation,
                    Complete: true,
                    completion.Warnings,
                    completion.Documents);
                return CommandResult.Success(
                    JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
            }

            var warnings = string.Concat(completion.Warnings.Select(warning =>
                $"[{warning.Code}] {warning.Message}{Environment.NewLine}"));
            return new CommandResult(
                ExitCode: 0,
                StandardOutput: completion.Output,
                StandardError: warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Cancelled(
                "CommonModules operation was cancelled before project mutation.");
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (CommonModulesTransactionException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (ProjectManifestMutationException ex)
        {
            return CommandResult.UsageError($"[{ex.Code}] {ex.Message}");
        }
    }

    private sealed record CommonModuleListOutput(
        string Document,
        IReadOnlyList<InstalledCommonModule> CommonModules);

    private sealed record CommonModulesMutationOutput(
        string SchemaVersion,
        string Scope,
        string Project,
        string? Document,
        string Operation,
        bool Complete,
        IReadOnlyList<ProjectManifestMutationWarning> Warnings,
        IReadOnlyList<CommonModulesMutationDocumentResult> Documents);
}
