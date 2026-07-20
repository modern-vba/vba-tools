using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents the stable disk metadata used to cache one exported VBA source file.
/// </summary>
internal readonly record struct VbaProjectSourceFileMetadata(
    long Length,
    long LastWriteTimeUtcTicks);

/// <summary>
/// Isolates project-manifest and exported-source reads from interactive snapshot construction.
/// </summary>
internal interface IVbaProjectFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateSourceFiles(
        string rootPath,
        string searchPattern,
        SearchOption searchOption);

    bool TryGetSourceMetadata(
        string path,
        out VbaProjectSourceFileMetadata metadata);

    string ReadManifestText(string path);

    byte[] ReadSourceBytes(string path);
}

/// <summary>
/// Determines whether exported sources remain owned by one resolved project
/// rather than a descendant project manifest.
/// </summary>
internal sealed class VbaProjectSourceOwnershipBoundary
{
    private const string ManifestFileName = "vba-project.json";
    private readonly VbaProjectResolution resolution;
    private readonly IVbaProjectFileSystem fileSystem;
    private readonly string rootPath;
    private readonly string? authorityManifestPath;
    private readonly IReadOnlyDictionary<string, bool>
        manifestBarrierOverrides;
    private readonly Dictionary<string, bool> ownedDirectories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> observedManifestBarrierPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public VbaProjectSourceOwnershipBoundary(
        VbaProjectResolution resolution,
        IVbaProjectFileSystem fileSystem,
        IReadOnlyDictionary<string, bool>? manifestBarrierOverrides = null)
    {
        this.resolution = resolution;
        this.fileSystem = fileSystem;
        rootPath = NormalizePath(resolution.RootPath);
        authorityManifestPath = string.IsNullOrWhiteSpace(
                resolution.ManifestPath)
            ? null
            : NormalizePath(resolution.ManifestPath);
        this.manifestBarrierOverrides =
            manifestBarrierOverrides
            ?? new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> ObservedManifestBarrierPaths
        => observedManifestBarrierPaths;

    public bool ContainsSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)
            || string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(sourcePath);
        if (resolution.Kind == VbaProjectResolutionKind.AdHoc)
        {
            return VbaProjectResolver.SameDirectory(fullPath, rootPath);
        }

        if (!VbaProjectResolver.IsPathUnder(fullPath, rootPath))
        {
            return false;
        }

        var sourceDirectory = Path.GetDirectoryName(fullPath);
        return sourceDirectory is not null
            && IsOwnedDirectory(sourceDirectory);
    }

    private bool IsOwnedDirectory(string directoryPath)
    {
        var fullDirectoryPath = NormalizePath(directoryPath);
        if (ownedDirectories.TryGetValue(
                fullDirectoryPath,
                out var isOwned))
        {
            return isOwned;
        }

        if (!SamePath(fullDirectoryPath, rootPath)
            && !VbaProjectResolver.IsPathUnder(
                fullDirectoryPath,
                rootPath))
        {
            ownedDirectories[fullDirectoryPath] = false;
            return false;
        }

        var parentOwned = SamePath(fullDirectoryPath, rootPath);
        if (!parentOwned)
        {
            var parentPath = Path.GetDirectoryName(fullDirectoryPath);
            parentOwned = parentPath is not null
                && IsOwnedDirectory(parentPath);
        }

        var candidateManifestPath = Path.Combine(
            fullDirectoryPath,
            ManifestFileName);
        var hasOverride =
            manifestBarrierOverrides.TryGetValue(
                candidateManifestPath,
                out var barrierOverride);
        var hasManifestBarrier = hasOverride
                ? barrierOverride
                : fileSystem.FileExists(candidateManifestPath);
        if (!hasOverride
            && hasManifestBarrier
            && !SamePath(
                candidateManifestPath,
                authorityManifestPath))
        {
            observedManifestBarrierPaths.Add(
                Path.GetFullPath(candidateManifestPath));
        }

        isOwned = parentOwned
            && (SamePath(candidateManifestPath, authorityManifestPath)
                || !hasManifestBarrier);
        ownedDirectories[fullDirectoryPath] = isOwned;
        return isOwned;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        return pathRoot is not null
            && fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase)
                ? pathRoot
                : fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
    }

    private static bool SamePath(string left, string? right)
        => right is not null
            && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads project inputs from the host filesystem.
/// </summary>
internal sealed class SystemVbaProjectFileSystem : IVbaProjectFileSystem
{
    public static SystemVbaProjectFileSystem Instance { get; } = new();

    private SystemVbaProjectFileSystem()
    {
    }

    public bool FileExists(string path)
        => File.Exists(path);

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public IEnumerable<string> EnumerateSourceFiles(
        string rootPath,
        string searchPattern,
        SearchOption searchOption)
        => Directory.EnumerateFiles(rootPath, searchPattern, searchOption);

    public bool TryGetSourceMetadata(
        string path,
        out VbaProjectSourceFileMetadata metadata)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            metadata = default;
            return false;
        }

        metadata = new VbaProjectSourceFileMetadata(
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks);
        return true;
    }

    public string ReadManifestText(string path)
        => File.ReadAllText(path);

    public byte[] ReadSourceBytes(string path)
        => File.ReadAllBytes(path);
}
