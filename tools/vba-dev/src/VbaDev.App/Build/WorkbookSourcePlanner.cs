using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>
/// Selects and orders the VBA source files that should be imported into generated workbooks.
/// </summary>
public sealed class WorkbookSourcePlanner
{
    private const int PublishMarkerScanLineLimit = 32;
    private const string PublishExclusionMarker = "'#ExcludePublish";

    private readonly CommonModulesManifestReader commonModulesManifestReader;

    /// <summary>
    /// Creates the workbook source planner.
    /// </summary>
    /// <param name="commonModulesManifestReader">The reader used to resolve installed CommonModules dependencies.</param>
    public WorkbookSourcePlanner(CommonModulesManifestReader commonModulesManifestReader)
    {
        this.commonModulesManifestReader = commonModulesManifestReader;
    }

    /// <summary>
    /// Resolves the source files for build output, including test-only project and CommonModules sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The ordered source files to import into the build workbook.</returns>
    public IReadOnlyList<VbaSourceFile> ResolveBuildSourceFiles(ResolvedProjectContext context)
        => ResolveSourceFiles(
            context,
            includeCommonModule: _ => true,
            includeProjectLocalSource: _ => true);

    /// <summary>
    /// Resolves the source files for publish output, excluding test-only and explicitly excluded sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The ordered source files to import into the published workbook.</returns>
    public IReadOnlyList<VbaSourceFile> ResolvePublishSourceFiles(ResolvedProjectContext context)
        => ResolveSourceFiles(
            context,
            includeCommonModule: entry => !IsTestOnlyCommonModule(entry),
            includeProjectLocalSource: source => !HasPublishExclusionMarker(source));

    private IReadOnlyList<VbaSourceFile> ResolveSourceFiles(
        ResolvedProjectContext context,
        Func<CommonModuleManifestEntry, bool> includeCommonModule,
        Func<VbaSourceFile, bool> includeProjectLocalSource)
    {
        if (!File.Exists(context.TemplateDocumentPath))
        {
            throw new BuildCommandException($"Template workbook was not found: {context.TemplateDocumentPath}");
        }

        if (!Directory.Exists(context.DocumentSourceSetPath))
        {
            throw new BuildCommandException($"Document source set was not found: {context.DocumentSourceSetPath}");
        }

        var discoveredSourceFiles = DocumentSourceSetLayout
            .EnumerateVbaSourceFiles(context.DocumentSourceSetPath)
            .ToArray();

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(context.DocumentSourceSetPath, discoveredSourceFiles);

        var sourceFilesByName = discoveredSourceFiles
            .ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);

        var installedCommonModuleEntries = ResolveInstalledCommonModuleEntries(context, sourceFilesByName);
        var commonModuleEntries = installedCommonModuleEntries
            .Where(includeCommonModule)
            .ToArray();
        var commonModuleSet = new HashSet<string>(
            installedCommonModuleEntries.Select(entry => entry.ModuleFile),
            StringComparer.OrdinalIgnoreCase);
        var orderedSourceFiles = new List<VbaSourceFile>();
        foreach (var entry in commonModuleEntries)
        {
            if (!sourceFilesByName.TryGetValue(entry.ModuleFile, out var sourceFile))
            {
                throw new CommonModulesManifestException($"CommonModules dependency '{entry.ModuleFile}' is required but not installed in {context.DocumentSourceSetPath}.");
            }

            orderedSourceFiles.Add(sourceFile);
        }

        orderedSourceFiles.AddRange(sourceFilesByName
            .Values
            .Where(source => !commonModuleSet.Contains(source.FileName))
            .Where(includeProjectLocalSource)
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));

        return orderedSourceFiles;
    }

    private IReadOnlyList<CommonModuleManifestEntry> ResolveInstalledCommonModuleEntries(
        ResolvedProjectContext context,
        IReadOnlyDictionary<string, VbaSourceFile> sourceFilesByName)
    {
        if (context.CommonModulesRepositoryPath is null ||
            !Directory.Exists(context.CommonModulesRepositoryPath) ||
            !File.Exists(Path.Combine(context.CommonModulesRepositoryPath, CommonModulesManifestReader.ManifestFileName)))
        {
            return [];
        }

        var entries = commonModulesManifestReader.Load(context.CommonModulesRepositoryPath);
        var installedModuleFiles = entries
            .Where(entry => sourceFilesByName.ContainsKey(entry.ModuleFile))
            .Select(entry => entry.ModuleFile)
            .ToArray();

        return CommonModulesDependencyResolver
            .ResolveRequestedEntries(entries, installedModuleFiles)
            .ToArray();
    }

    private static bool IsTestOnlyCommonModule(CommonModuleManifestEntry entry)
        => entry.HasCategory("test-foundation") || entry.HasCategory("test-double");

    private static bool HasPublishExclusionMarker(VbaSourceFile source)
    {
        foreach (var line in File.ReadLines(source.SourcePath).Take(PublishMarkerScanLineLimit))
        {
            if (line.TrimStart().StartsWith(PublishExclusionMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

}
