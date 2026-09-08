using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>Captures and orders admitted source facts for generated workbooks.</summary>
public sealed class WorkbookSourcePlanner
{
    private readonly VbaSourceAdmission sourceAdmission;

    /// <summary>Creates a planner using invocation-fixed source admission.</summary>
    public WorkbookSourcePlanner()
        : this(new VbaSourceAdmission(ActiveWindowsAnsiCodePage.Get))
    {
    }

    internal WorkbookSourcePlanner(Func<int> getActiveCodePage)
        : this(new VbaSourceAdmission(getActiveCodePage))
    {
    }

    internal WorkbookSourcePlanner(VbaSourceAdmission sourceAdmission)
    {
        this.sourceAdmission = sourceAdmission ?? throw new ArgumentNullException(nameof(sourceAdmission));
    }

    internal AdmittedWorkbookGenerationSourceInput CaptureBuildSourceInput(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        ValidateSourcePaths(context);
        var admission = sourceAdmission.Admit(context.DocumentSourceSetPath, VbaSourceAdmissionIntent.Build, cancellationToken);
        return OrderAdmittedSources(context, admission);
    }

    internal AdmittedWorkbookGenerationSourceInput CapturePublishSourceInput(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        ValidateSourcePaths(context);
        var admission = sourceAdmission.AdmitPublish(context.DocumentSourceSetPath, context.Document.CommonModules, cancellationToken);
        return OrderAdmittedSources(context, admission);
    }

    internal static AdmittedWorkbookGenerationSourceInput OrderAdmittedSources(
        ResolvedProjectContext context,
        AdmittedVbaSourceSet admission)
    {
        var sourcesByName = admission.Sources.ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);
        var installedCommonModules = context.Document.CommonModules;
        var commonModuleNames = installedCommonModules.Select(entry => entry.ModuleFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AdmittedVbaSource>();
        foreach (var entry in installedCommonModules.Where(entry =>
                     admission.Intent != VbaSourceAdmissionIntent.Publish || !entry.TestOnly))
        {
            if (sourcesByName.TryGetValue(entry.ModuleFile, out var source))
            {
                ordered.Add(source);
            }
        }
        ordered.AddRange(admission.Sources
            .Where(source => !commonModuleNames.Contains(source.FileName))
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));
        return new(new AdmittedVbaSourceSet(admission.Intent, admission.ActiveCodePage, ordered));
    }

    private static void ValidateSourcePaths(ResolvedProjectContext context)
    {
        if (!File.Exists(context.TemplateDocumentPath))
        {
            throw new BuildCommandException($"Template workbook was not found: {context.TemplateDocumentPath}");
        }
        if (!Directory.Exists(context.DocumentSourceSetPath))
        {
            throw new BuildCommandException($"Document source set was not found: {context.DocumentSourceSetPath}");
        }
    }
}
