using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

public sealed class WorkbookSourcePlanner
{
    private const int PublishMarkerScanLineLimit = 32;
    private const string PublishExclusionMarker = "'#ExcludePublish";

    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly CommonModulesService commonModulesService;

    public WorkbookSourcePlanner(
        CommonModulesManifestReader commonModulesManifestReader,
        CommonModulesService commonModulesService)
    {
        this.commonModulesManifestReader = commonModulesManifestReader;
        this.commonModulesService = commonModulesService;
    }

    public IReadOnlyList<VbaSourceFile> ResolveBuildSourceFiles(ResolvedProjectContext context)
        => ResolveSourceFiles(
            context,
            includeCommonModule: _ => true,
            includeProjectLocalSource: _ => true);

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

        var sourceFilesByName = Directory
            .EnumerateFiles(context.DocumentSourceSetPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsVbaSourceFile)
            .Select(CreateSourceFile)
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

        return commonModulesService
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

    private static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static VbaSourceFile CreateSourceFile(string path)
    {
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bas" => VbaSourceKind.StandardModule,
            ".cls" => VbaSourceKind.ClassModule,
            ".frm" => VbaSourceKind.Form,
            _ => throw new BuildCommandException($"Unsupported VBA source file: {path}")
        };

        var binaryPath = kind == VbaSourceKind.Form
            ? Path.ChangeExtension(path, ".frx")
            : null;

        return new VbaSourceFile(
            SourcePath: path,
            Kind: kind,
            BinaryPath: binaryPath is not null && File.Exists(binaryPath) ? binaryPath : null);
    }
}
