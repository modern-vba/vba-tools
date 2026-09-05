using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaTools.Syntax;

namespace VbaDev.App.Build;

/// <summary>
/// Selects and orders the VBA source files that should be imported into generated workbooks.
/// </summary>
public sealed class WorkbookSourcePlanner
{
    private const int PublishMarkerScanLineLimit = 32;
    private const string PublishExclusionMarker = "'#ExcludePublish";
    private readonly Func<int> getActiveCodePage;
    private readonly VbaSourceAdmission sourceAdmission;

    /// <summary>
    /// Creates a source planner that uses the active Windows code page for strict marker decoding.
    /// </summary>
    public WorkbookSourcePlanner()
        : this(ActiveWindowsAnsiCodePage.Get)
    {
    }

    internal WorkbookSourcePlanner(Func<int> getActiveCodePage)
    {
        this.getActiveCodePage = getActiveCodePage
            ?? throw new ArgumentNullException(nameof(getActiveCodePage));
        sourceAdmission = new VbaSourceAdmission(getActiveCodePage);
    }

    internal WorkbookSourcePlanner(VbaSourceAdmission sourceAdmission)
        : this(ActiveWindowsAnsiCodePage.Get)
    {
        this.sourceAdmission = sourceAdmission ?? throw new ArgumentNullException(nameof(sourceAdmission));
    }

    internal AdmittedWorkbookGenerationSourceInput CaptureBuildSourceInput(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
    {
        ValidateSourcePaths(context, requireTemplate: true);
        var admission = sourceAdmission.Admit(context.DocumentSourceSetPath, VbaSourceAdmissionIntent.Build, cancellationToken);
        var sourcesByName = admission.Sources.ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);
        var ordered = OrderSourceFiles(
            context,
            admission.Sources.Select(source => new VbaSourceFile(source.SourcePath, source.Kind, source.BinaryPath)),
            includeCommonModule: _ => true,
            selectProjectLocalSource: source => source);
        return new(new AdmittedVbaSourceSet(
            admission.Intent,
            admission.ActiveCodePage,
            ordered.Select(source => sourcesByName[source.FileName])));
    }

    /// <summary>
    /// Resolves the source files for build output, including test-only project and CommonModules sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The ordered source files to import into the build workbook.</returns>
    public IReadOnlyList<VbaSourceFile> ResolveBuildSourceFiles(ResolvedProjectContext context)
        => ResolveSourceFiles(
            context,
            requireTemplate: true,
            includeCommonModule: _ => true,
            selectProjectLocalSource: source => source);

    /// <summary>
    /// Resolves the build source profile without requiring the template to exist yet.
    /// </summary>
    public IReadOnlyList<VbaSourceFile> ResolveBuildSourceFilesForPreflight(
        ResolvedProjectContext context)
        => ResolveSourceFiles(
            context,
            requireTemplate: false,
            includeCommonModule: _ => true,
            selectProjectLocalSource: source => source);

    /// <summary>
    /// Resolves the source files for publish output, excluding test-only and explicitly excluded sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The ordered source files to import into the published workbook.</returns>
    public IReadOnlyList<VbaSourceFile> ResolvePublishSourceFiles(ResolvedProjectContext context)
        => ResolvePublishSourceFiles(context, requireTemplate: true);

    /// <summary>
    /// Resolves the publish source profile without requiring the template to exist yet.
    /// </summary>
    public IReadOnlyList<VbaSourceFile> ResolvePublishSourceFilesForPreflight(
        ResolvedProjectContext context)
        => ResolvePublishSourceFiles(context, requireTemplate: false);

    private IReadOnlyList<VbaSourceFile> ResolvePublishSourceFiles(
        ResolvedProjectContext context,
        bool requireTemplate)
    {
        var activeCodePage = getActiveCodePage();
        return ResolveSourceFiles(
            context,
            requireTemplate,
            includeCommonModule: entry => !entry.TestOnly,
            selectProjectLocalSource: source => SelectPublishSource(
                source,
                activeCodePage));
    }

    private IReadOnlyList<VbaSourceFile> ResolveSourceFiles(
        ResolvedProjectContext context,
        bool requireTemplate,
        Func<InstalledCommonModule, bool> includeCommonModule,
        Func<VbaSourceFile, VbaSourceFile?> selectProjectLocalSource)
    {
        ValidateSourcePaths(context, requireTemplate);
        var discoveredSourceFiles = DocumentSourceSetLayout
            .EnumerateVbaSourceFiles(context.DocumentSourceSetPath)
            .ToArray();

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(context.DocumentSourceSetPath, discoveredSourceFiles);

        return OrderSourceFiles(context, discoveredSourceFiles, includeCommonModule, selectProjectLocalSource);
    }

    private static void ValidateSourcePaths(ResolvedProjectContext context, bool requireTemplate)
    {
        if (requireTemplate && !File.Exists(context.TemplateDocumentPath))
        {
            throw new BuildCommandException($"Template workbook was not found: {context.TemplateDocumentPath}");
        }

        if (!Directory.Exists(context.DocumentSourceSetPath))
        {
            throw new BuildCommandException($"Document source set was not found: {context.DocumentSourceSetPath}");
        }
    }

    private static IReadOnlyList<VbaSourceFile> OrderSourceFiles(
        ResolvedProjectContext context,
        IEnumerable<VbaSourceFile> discoveredSourceFiles,
        Func<InstalledCommonModule, bool> includeCommonModule,
        Func<VbaSourceFile, VbaSourceFile?> selectProjectLocalSource)
    {
        var sourceFilesByName = discoveredSourceFiles
            .ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);

        var installedCommonModuleEntries = context.Document.CommonModules;
        var commonModuleEntries = installedCommonModuleEntries
            .Where(includeCommonModule)
            .ToArray();
        var commonModuleSet = new HashSet<string>(
            installedCommonModuleEntries.Select(entry => entry.ModuleFile),
            StringComparer.OrdinalIgnoreCase);
        var orderedSourceFiles = new List<VbaSourceFile>();
        foreach (var entry in commonModuleEntries)
        {
            if (sourceFilesByName.TryGetValue(entry.ModuleFile, out var sourceFile))
            {
                orderedSourceFiles.Add(sourceFile);
            }
        }

        orderedSourceFiles.AddRange(sourceFilesByName
            .Values
            .Where(source => !commonModuleSet.Contains(source.FileName))
            .Select(selectProjectLocalSource)
            .OfType<VbaSourceFile>()
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));

        return orderedSourceFiles;
    }

    private static VbaSourceFile? SelectPublishSource(
        VbaSourceFile source,
        int activeCodePage)
    {
        var diagnosticSourcePath = source.DiagnosticSourcePath ?? source.SourcePath;
        var text = VbeImportSourceSet.DecodeSourceText(
            File.ReadAllBytes(source.SourcePath),
            activeCodePage,
            diagnosticSourcePath);
        foreach (var line in text
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Take(PublishMarkerScanLineLimit))
        {
            if (VbaIdentifier.TrimStartWhitespace(line)
                .StartsWith(PublishExclusionMarker, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return source with
        {
            ExpectedUnicodeText = text,
            ExpectedUnicodeTextSourcePath = diagnosticSourcePath
        };
    }

}
