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
        => RunTransaction(() => installationTransaction.Add(context, requestedModules, force));

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
        => RunTransaction(() => installationTransaction.Update(project));

    private static CommandResult RunTransaction(Func<string> execute)
    {
        try
        {
            return CommandResult.Success(execute());
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (CommonModulesTransactionException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    private sealed record CommonModuleListOutput(
        string Document,
        IReadOnlyList<InstalledCommonModule> CommonModules);
}
