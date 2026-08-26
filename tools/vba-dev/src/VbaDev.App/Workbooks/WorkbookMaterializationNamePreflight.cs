using System.Text;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Proves that source and live-workbook identities can share one effective VBA namespace.
/// </summary>
public sealed class WorkbookMaterializationNamePreflight
{
    /// <summary>
    /// Reports every case-insensitive conflict among the staged source identities.
    /// </summary>
    public void ValidateSourcePhase(IReadOnlyList<VbeImportSourceFile> sourceFiles)
        => ThrowIfFailed(InspectSourcePhase(sourceFiles));

    /// <summary>
    /// Collects invalid source authority and every case-insensitive source conflict.
    /// </summary>
    public WorkbookMaterializationNamePreflightReport InspectSourcePhase(
        IReadOnlyList<VbeImportSourceFile> sourceFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        var invalidSources = sourceFiles
            .Select((source, index) => new InvalidSourceIdentity(
                source.DiagnosticSourcePath,
                source.ModuleIdentityAuthority.Failure,
                index))
            .Where(source => source.Failure is not null)
            .OrderBy(source => source.Index)
            .ToArray();
        var conflicts = sourceFiles
            .Select((source, index) => new { Source = source, Index = index })
            .Where(item => item.Source.ModuleIdentityAuthority.IsAuthoritative)
            .Select(item => new SourceIdentity(
                item.Source.ModuleIdentityAuthority.Name!,
                item.Source.DiagnosticSourcePath,
                item.Index))
            .GroupBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .OrderBy(group => group.Min(source => source.Index))
            .ToArray();
        var findings = new List<SourceFinding>(invalidSources.Length + conflicts.Length);
        foreach (var invalidSource in invalidSources)
        {
            findings.Add(new SourceFinding(
                invalidSource.Index,
                $"VBA source '{invalidSource.SourcePath}' {invalidSource.Failure}"));
        }

        foreach (var conflict in conflicts)
        {
            var sources = conflict.OrderBy(source => source.Index).ToArray();
            var finding = new StringBuilder();
            finding.Append("Source identity '");
            finding.Append(sources[0].Name);
            finding.Append("' conflicts case-insensitively across:");
            foreach (var source in sources)
            {
                finding.AppendLine();
                finding.Append("  ");
                finding.Append(source.SourcePath);
            }

            findings.Add(new SourceFinding(
                sources.Min(source => source.Index),
                finding.ToString()));
        }

        return new WorkbookMaterializationNamePreflightReport(
            findings
                .OrderBy(finding => finding.Index)
                .Select(finding => finding.Message)
                .ToArray(),
            LiveInspectionBlocked: invalidSources.Length > 0);
    }

    /// <summary>
    /// Reports every source identity that conflicts with a component retained by materialization.
    /// </summary>
    public void ValidateLivePhase(
        IReadOnlyList<VbeImportSourceFile> sourceFiles,
        IReadOnlyList<WorkbookModule> retainedModules,
        string projectName,
        IReadOnlyList<WorkbookReference> references)
        => ThrowIfFailed(InspectLivePhase(
            sourceFiles,
            retainedModules,
            projectName,
            references));

    /// <summary>
    /// Collects every incomplete live authority and conflict that complete siblings can prove.
    /// </summary>
    public WorkbookMaterializationNamePreflightReport InspectLivePhase(
        IReadOnlyList<VbeImportSourceFile> sourceFiles,
        IReadOnlyList<WorkbookModule> retainedModules,
        string projectName,
        IReadOnlyList<WorkbookReference> references)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(retainedModules);
        ArgumentNullException.ThrowIfNull(references);
        var findings = new List<string>();
        var projectIdentityComplete = IsCompleteVbaNamespaceName(projectName);
        if (!projectIdentityComplete)
        {
            findings.Add("The actual containing project identity is incomplete.");
        }

        var completeRetainedModules = retainedModules
            .Select((module, index) => new { Module = module, Index = index })
            .Where(item =>
            {
                if (IsCompleteVbaNamespaceName(item.Module.Name))
                {
                    return true;
                }

                findings.Add(
                    $"The actual retained component identity at index {item.Index} is incomplete.");
                return false;
            })
            .Select(item => item.Module)
            .ToArray();
        var completeReferences = references
            .Where(reference =>
            {
                if (IsCompleteVbaNamespaceName(reference.NamespaceName))
                {
                    return true;
                }

                findings.Add(
                    $"The active reference identity is incomplete for '{reference.Name}'.");
                return false;
            })
            .ToArray();

        var conflicts = sourceFiles
            .Select((source, index) => new
            {
                Source = source,
                Index = index,
                Retained = completeRetainedModules
                    .Where(module => module.Name.Equals(
                        source.ModuleIdentityAuthority.Name,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(module => module.Name, StringComparer.Ordinal)
                    .ToArray(),
                ConflictsWithProject = projectIdentityComplete && projectName.Equals(
                    source.ModuleIdentityAuthority.Name,
                    StringComparison.OrdinalIgnoreCase),
                References = completeReferences
                    .Where(reference => reference.NamespaceName!.Equals(
                        source.ModuleIdentityAuthority.Name,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            })
            .Where(item => item.Retained.Length > 0 ||
                item.ConflictsWithProject ||
                item.References.Length > 0)
            .OrderBy(item => item.Index)
            .ToArray();
        foreach (var conflict in conflicts)
        {
            foreach (var retained in conflict.Retained)
            {
                findings.Add(
                    $"Source identity '{conflict.Source.ModuleIdentityAuthority.Name}' from " +
                    $"'{conflict.Source.DiagnosticSourcePath}' conflicts with retained component " +
                    $"'{retained.Name}'.");
            }

            if (conflict.ConflictsWithProject)
            {
                findings.Add(
                    $"Source identity '{conflict.Source.ModuleIdentityAuthority.Name}' from " +
                    $"'{conflict.Source.DiagnosticSourcePath}' conflicts with containing project " +
                    $"'{projectName}'.");
            }

            foreach (var reference in conflict.References)
            {
                findings.Add(
                    $"Source identity '{conflict.Source.ModuleIdentityAuthority.Name}' from " +
                    $"'{conflict.Source.DiagnosticSourcePath}' conflicts with active reference " +
                    $"'{reference.NamespaceName}'.");
            }
        }

        return new WorkbookMaterializationNamePreflightReport(
            findings,
            LiveInspectionBlocked: false);
    }

    /// <summary>
    /// Throws one deterministic failure containing every supplied phase finding.
    /// </summary>
    public void ThrowIfFailed(
        params WorkbookMaterializationNamePreflightReport[] reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var findings = reports
            .Where(report => report is not null)
            .SelectMany(report => report.Findings)
            .ToArray();
        if (findings.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "VBA namespace preflight failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, findings));
    }

    private sealed record SourceIdentity(string Name, string SourcePath, int Index);

    private static bool IsCompleteVbaNamespaceName(string? value)
        => value is not null && VbaIdentifier.IsIdentifier(value);

    private sealed record InvalidSourceIdentity(string SourcePath, string? Failure, int Index);

    private sealed record SourceFinding(int Index, string Message);
}

/// <summary>
/// Contains ordered namespace findings collected without mutating a workbook.
/// </summary>
/// <param name="Findings">The deterministic finding text in report order.</param>
/// <param name="LiveInspectionBlocked">Whether invalid source authority prevents trustworthy live comparison.</param>
public sealed record WorkbookMaterializationNamePreflightReport(
    IReadOnlyList<string> Findings,
    bool LiveInspectionBlocked)
{
    /// <summary>
    /// Gets whether this phase found an authority gap or conflict.
    /// </summary>
    public bool HasFailures => Findings.Count > 0;
}
