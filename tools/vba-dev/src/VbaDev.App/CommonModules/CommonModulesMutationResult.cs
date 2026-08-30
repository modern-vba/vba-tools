using System.Text;
using System.Text.Json.Serialization;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Describes one exhaustive CommonModules mutation document result.
/// </summary>
public sealed record CommonModulesMutationDocumentResult(
    string Document,
    IReadOnlyList<CommonModulesMutationModuleResult> Modules,
    IReadOnlyList<CommonModulesReferenceChangeResult> ReferenceChanges);

/// <summary>
/// Describes the final installed state and every change for one CommonModule.
/// </summary>
public sealed record CommonModulesMutationModuleResult(
    string Name,
    string ModuleFile,
    bool Requested,
    bool TestOnly,
    bool Orphaned,
    string Status,
    IReadOnlyList<CommonModulesMutationChangeResult> Changes);

/// <summary>
/// Describes one change from the closed CommonModules mutation vocabulary.
/// </summary>
public sealed record CommonModulesMutationChangeResult(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceSetRelativePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? TestOnly = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Orphaned = null);

/// <summary>
/// Describes one VBA project reference newly added for CommonModules requirements.
/// </summary>
public sealed record CommonModulesReferenceChangeResult(
    string Kind,
    string Name,
    bool Requested);

internal static class CommonModulesMutationTextFormatter
{
    public static string Format(
        string operation,
        IReadOnlyList<CommonModulesMutationDocumentResult> documents)
    {
        var output = new StringBuilder();
        if (documents.Count == 0)
        {
            output.AppendLine("No installed CommonModules entries were found.");
        }

        var installed = 0;
        var sourceUpdated = 0;
        var promoted = 0;
        var metadataUpdated = 0;
        var referencesAdded = 0;
        var unchanged = 0;
        foreach (var document in documents)
        {
            foreach (var module in document.Modules)
            {
                if (module.Status.Equals("unchanged", StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                var metadataChanged = false;
                foreach (var change in module.Changes)
                {
                    switch (change.Kind)
                    {
                        case "installed":
                            installed++;
                            AppendSourceChange(
                                output,
                                operation,
                                document.Document,
                                operation.Equals("update", StringComparison.Ordinal) ? "Updated" : "Copied",
                                change);
                            break;
                        case "sourceUpdated":
                            sourceUpdated++;
                            AppendSourceChange(output, operation, document.Document, "Updated", change);
                            break;
                        case "directRequestPromoted":
                            promoted++;
                            output.AppendLine(
                                $"Promoted {Qualify(operation, document.Document, module.Name)} to directly requested.");
                            break;
                        case "testOnlyChanged":
                            metadataChanged = true;
                            output.AppendLine(
                                $"Changed {Qualify(operation, document.Document, module.Name)} testOnly to "
                                + $"{module.TestOnly.ToString().ToLowerInvariant()}.");
                            break;
                        case "orphanedChanged":
                            metadataChanged = true;
                            output.AppendLine(
                                $"Changed {Qualify(operation, document.Document, module.Name)} orphaned to "
                                + $"{module.Orphaned.ToString().ToLowerInvariant()}.");
                            break;
                    }
                }

                if (metadataChanged)
                {
                    metadataUpdated++;
                }
            }

            foreach (var reference in document.ReferenceChanges)
            {
                referencesAdded++;
                output.AppendLine(
                    $"Added required reference {Qualify(operation, document.Document, reference.Name)}.");
            }
        }

        if (installed == 0
            && sourceUpdated == 0
            && promoted == 0
            && metadataUpdated == 0
            && referencesAdded == 0)
        {
            output.AppendLine("No CommonModules changes.");
        }

        output.AppendLine($"Installed CommonModules: {installed}");
        output.AppendLine($"Source-updated CommonModules: {sourceUpdated}");
        output.AppendLine($"Direct-request promotions: {promoted}");
        output.AppendLine($"Metadata-updated CommonModules: {metadataUpdated}");
        output.AppendLine($"Added required references: {referencesAdded}");
        output.AppendLine($"Unchanged CommonModules: {unchanged}");
        return output.ToString();
    }

    private static void AppendSourceChange(
        StringBuilder output,
        string operation,
        string document,
        string verb,
        CommonModulesMutationChangeResult change)
    {
        var relativePath = change.SourceSetRelativePath
            ?? throw new InvalidOperationException(
                $"CommonModules {change.Kind} change is missing its source-set-relative path.");
        output.AppendLine($"{verb} {Qualify(operation, document, relativePath)}");
    }

    private static string Qualify(string operation, string document, string value)
        => operation.Equals("update", StringComparison.Ordinal)
            ? $"{document}/{value}"
            : value;
}
