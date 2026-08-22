using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Selects and orders the VBA source files that should be imported into generated workbooks.
/// </summary>
public sealed class WorkbookSourcePlanner
{
    private const int PublishMarkerScanLineLimit = 32;
    private const string PublishExclusionMarker = "'#ExcludePublish";

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
            includeCommonModule: entry => !entry.TestOnly,
            includeProjectLocalSource: source => !HasPublishExclusionMarker(source));

    private IReadOnlyList<VbaSourceFile> ResolveSourceFiles(
        ResolvedProjectContext context,
        Func<InstalledCommonModule, bool> includeCommonModule,
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
            .Where(includeProjectLocalSource)
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));

        return orderedSourceFiles;
    }

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
