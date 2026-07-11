using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.CommonModules;

public sealed class CommonModulesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly CommonModulesInstallationTransaction installationTransaction;

    public CommonModulesService(CommonModulesInstallationTransaction installationTransaction)
    {
        this.installationTransaction = installationTransaction;
    }

    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules, bool force)
        => RunTransaction(() => installationTransaction.Add(context, requestedModules, force));

    public CommandResult List(ResolvedProjectContext context, string format)
    {
        var document = GetDocument(context.Manifest, context.DocumentName);
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

    private sealed record CommonModuleListOutput(
        string Document,
        IReadOnlyList<InstalledCommonModule> CommonModules);
}
