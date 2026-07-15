using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// A persisted generated reference catalog entry.
/// </summary>
public sealed record VbaProjectReferenceCatalogPersistentEntry
{
    /// <summary>
    /// Creates a persisted generated reference catalog entry.
    /// </summary>
    /// <param name="schemaVersion">The persistent cache schema version.</param>
    /// <param name="generatorVersion">The reference catalog generator version.</param>
    /// <param name="identity">The resolved TypeLib identity for the catalog.</param>
    /// <param name="catalog">The generated reference catalog metadata.</param>
    [JsonConstructor]
    public VbaProjectReferenceCatalogPersistentEntry(
        int schemaVersion,
        string generatorVersion,
        VbaProjectReferenceCatalogIdentity identity,
        VbaProjectReferenceCatalog catalog)
    {
        SchemaVersion = schemaVersion;
        GeneratorVersion = generatorVersion;
        Identity = identity;
        Catalog = catalog;
    }

    /// <summary>
    /// Creates a current-schema persisted generated reference catalog entry.
    /// </summary>
    /// <param name="identity">The resolved TypeLib identity for the catalog.</param>
    /// <param name="catalog">The generated reference catalog metadata.</param>
    public VbaProjectReferenceCatalogPersistentEntry(
        VbaProjectReferenceCatalogIdentity identity,
        VbaProjectReferenceCatalog catalog)
        : this(
            VbaProjectReferenceCatalogPersistentStore.CurrentSchemaVersion,
            VbaProjectReferenceCatalogPersistentStore.CurrentGeneratorVersion,
            identity,
            catalog)
    {
    }

    /// <summary>
    /// Gets the persistent cache schema version.
    /// </summary>
    public int SchemaVersion { get; init; }

    /// <summary>
    /// Gets the reference catalog generator version.
    /// </summary>
    public string GeneratorVersion { get; init; }

    /// <summary>
    /// Gets the resolved TypeLib identity for the catalog.
    /// </summary>
    public VbaProjectReferenceCatalogIdentity Identity { get; init; }

    /// <summary>
    /// Gets the generated reference catalog metadata.
    /// </summary>
    public VbaProjectReferenceCatalog Catalog { get; init; }
}

/// <summary>
/// Identifies the validation state of a persisted generated reference catalog load.
/// </summary>
public enum VbaProjectReferenceCatalogPersistentLoadStatus
{
    /// <summary>
    /// No persisted entry exists for the reference.
    /// </summary>
    Missing,

    /// <summary>
    /// The persisted entry is current for the active cache schema and generator version.
    /// </summary>
    Current,

    /// <summary>
    /// The persisted entry is usable but should be refreshed.
    /// </summary>
    Stale,

    /// <summary>
    /// The persisted entry could not be safely read.
    /// </summary>
    Unreadable
}

/// <summary>
/// Represents the result of loading one persisted generated reference catalog.
/// </summary>
/// <param name="Status">The load validation status.</param>
/// <param name="Entry">The loaded catalog entry, when available and usable.</param>
/// <param name="WarningMessage">A recoverable warning explaining why the entry is stale or unreadable.</param>
public sealed record VbaProjectReferenceCatalogPersistentLoadResult(
    VbaProjectReferenceCatalogPersistentLoadStatus Status,
    VbaProjectReferenceCatalogPersistentEntry? Entry,
    string? WarningMessage)
{
    /// <summary>
    /// Creates a current load result.
    /// </summary>
    /// <param name="entry">The loaded persisted catalog entry.</param>
    /// <returns>The load result.</returns>
    public static VbaProjectReferenceCatalogPersistentLoadResult Current(
        VbaProjectReferenceCatalogPersistentEntry entry)
        => new(VbaProjectReferenceCatalogPersistentLoadStatus.Current, entry, null);

    /// <summary>
    /// Creates a stale load result.
    /// </summary>
    /// <param name="entry">The loaded persisted catalog entry.</param>
    /// <param name="warningMessage">The stale-cache warning message.</param>
    /// <returns>The load result.</returns>
    public static VbaProjectReferenceCatalogPersistentLoadResult Stale(
        VbaProjectReferenceCatalogPersistentEntry entry,
        string warningMessage)
        => new(VbaProjectReferenceCatalogPersistentLoadStatus.Stale, entry, warningMessage);

    /// <summary>
    /// Creates a cache miss without a warning.
    /// </summary>
    /// <returns>The load result.</returns>
    public static VbaProjectReferenceCatalogPersistentLoadResult Miss()
        => new(VbaProjectReferenceCatalogPersistentLoadStatus.Missing, null, null);

    /// <summary>
    /// Creates a recoverable cache miss with a warning.
    /// </summary>
    /// <param name="warningMessage">The warning message.</param>
    /// <returns>The load result.</returns>
    public static VbaProjectReferenceCatalogPersistentLoadResult Warning(string warningMessage)
        => new(VbaProjectReferenceCatalogPersistentLoadStatus.Unreadable, null, warningMessage);
}

/// <summary>
/// Stores generated reference catalogs on disk for reuse across language-server sessions.
/// </summary>
public sealed class VbaProjectReferenceCatalogPersistentStore
{
    /// <summary>
    /// The environment variable that overrides the persistent reference catalog root directory.
    /// </summary>
    public const string CacheRootEnvironmentVariable = "VBA_TOOLS_REFERENCE_CATALOG_CACHE_DIR";

    /// <summary>
    /// The current persistent reference catalog cache schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The current generated reference catalog metadata version.
    /// </summary>
    public const string CurrentGeneratorVersion = "typelib-catalog-v6";

    private const string ReferencesDirectoryName = "references";
    private const string CatalogsDirectoryName = "catalogs";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string rootDirectory;

    /// <summary>
    /// Creates a persistent reference catalog store.
    /// </summary>
    /// <param name="rootDirectory">The root directory that owns persisted catalog files.</param>
    public VbaProjectReferenceCatalogPersistentStore(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Creates the default per-user persistent reference catalog store.
    /// </summary>
    /// <returns>The default persistent store.</returns>
    public static VbaProjectReferenceCatalogPersistentStore CreateDefault()
        => new(GetDefaultRootDirectory());

    /// <summary>
    /// Gets the default per-user persistent reference catalog root directory.
    /// </summary>
    /// <returns>The default root directory.</returns>
    public static string GetDefaultRootDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.Combine(Path.GetTempPath(), "vba-tools", "reference-catalogs")
            : Path.Combine(localApplicationData, "VbaTools", "ReferenceCatalogs");
    }

    /// <summary>
    /// Creates a deterministic, filesystem-safe index file key for a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The reference index file name.</returns>
    public static string CreateReferenceIndexKey(string referenceName)
        => $"ref-{HashKey(NormalizeReferenceName(referenceName))}.json";

    /// <summary>
    /// Creates a deterministic, filesystem-safe catalog entry file key for a resolved TypeLib identity.
    /// </summary>
    /// <param name="identity">The resolved catalog identity.</param>
    /// <returns>The catalog entry file name.</returns>
    public static string CreateCatalogEntryKey(VbaProjectReferenceCatalogIdentity identity)
        => $"catalog-{HashKey(string.Join(
            "\u001f",
            NormalizeReferenceName(identity.ReferenceName),
            identity.Guid.ToUpperInvariant(),
            identity.MajorVersion.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            identity.MinorVersion.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            identity.Lcid.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            NormalizeIdentityPath(identity.Path)))}.json";

    /// <summary>
    /// Gets the reference index path for a reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The reference index path.</returns>
    public string GetReferenceIndexPath(string referenceName)
        => Path.Combine(rootDirectory, ReferencesDirectoryName, CreateReferenceIndexKey(referenceName));

    /// <summary>
    /// Loads a persisted generated catalog entry for one reference name.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The load result.</returns>
    public VbaProjectReferenceCatalogPersistentLoadResult Load(string referenceName)
    {
        var indexPath = GetReferenceIndexPath(referenceName);
        if (!File.Exists(indexPath))
        {
            return VbaProjectReferenceCatalogPersistentLoadResult.Miss();
        }

        try
        {
            var index = ReadJson<ReferenceCatalogIndex>(indexPath);
            var staleWarnings = new List<string>();
            if (index.SchemaVersion != CurrentSchemaVersion)
            {
                return VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                    $"Persisted reference catalog index for '{referenceName}' uses unsupported schema version {index.SchemaVersion}.");
            }

            if (!index.GeneratorVersion.Equals(CurrentGeneratorVersion, StringComparison.Ordinal))
            {
                staleWarnings.Add(
                    $"Persisted reference catalog index for '{referenceName}' uses unsupported generator version '{index.GeneratorVersion}'.");
            }

            var entryPath = GetCatalogEntryPath(index.CatalogEntryKey);
            var entry = ReadJson<VbaProjectReferenceCatalogPersistentEntry>(entryPath);
            if (entry.SchemaVersion != CurrentSchemaVersion)
            {
                return VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                    $"Persisted reference catalog entry for '{referenceName}' uses unsupported schema version {entry.SchemaVersion}.");
            }

            if (!entry.GeneratorVersion.Equals(CurrentGeneratorVersion, StringComparison.Ordinal))
            {
                staleWarnings.Add(
                    $"Persisted reference catalog entry for '{referenceName}' uses unsupported generator version '{entry.GeneratorVersion}'.");
            }

            if (!entry.Identity.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            {
                return VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                    $"Persisted reference catalog entry for '{referenceName}' contains reference '{entry.Identity.ReferenceName}'.");
            }

            if (!entry.Catalog.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            {
                return VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                    $"Persisted reference catalog entry for '{referenceName}' contains catalog '{entry.Catalog.ReferenceName}'.");
            }

            return staleWarnings.Count == 0
                ? VbaProjectReferenceCatalogPersistentLoadResult.Current(entry)
                : VbaProjectReferenceCatalogPersistentLoadResult.Stale(entry, string.Join(" ", staleWarnings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return VbaProjectReferenceCatalogPersistentLoadResult.Warning(
                $"Persisted reference catalog cache for '{referenceName}' could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves a generated reference catalog entry and makes it current for its reference name.
    /// </summary>
    /// <param name="entry">The generated catalog entry to persist.</param>
    public void Save(VbaProjectReferenceCatalogPersistentEntry entry)
    {
        var catalogEntryKey = CreateCatalogEntryKey(entry.Identity);
        var catalogEntryPath = GetCatalogEntryPath(catalogEntryKey);
        var indexPath = GetReferenceIndexPath(entry.Identity.ReferenceName);
        var currentEntry = entry with
        {
            SchemaVersion = CurrentSchemaVersion,
            GeneratorVersion = CurrentGeneratorVersion
        };
        var index = new ReferenceCatalogIndex(
            CurrentSchemaVersion,
            CurrentGeneratorVersion,
            entry.Identity.ReferenceName,
            catalogEntryKey,
            entry.Identity);

        WriteJsonAtomic(catalogEntryPath, currentEntry);
        WriteJsonAtomic(indexPath, index);
    }

    private string GetCatalogEntryPath(string catalogEntryKey)
        => Path.Combine(rootDirectory, CatalogsDirectoryName, catalogEntryKey);

    private static T ReadJson<T>(string path)
    {
        var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), SerializerOptions);
        return value ?? throw new JsonException($"JSON file '{path}' did not contain a value.");
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(value, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string HashKey(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeReferenceName(string referenceName)
        => referenceName.Trim().ToUpperInvariant();

    private static string NormalizeIdentityPath(string path)
        => path.Trim().Replace('\\', '/').ToUpperInvariant();

    private sealed record ReferenceCatalogIndex(
        int SchemaVersion,
        string GeneratorVersion,
        string ReferenceName,
        string CatalogEntryKey,
        VbaProjectReferenceCatalogIdentity Identity);
}
