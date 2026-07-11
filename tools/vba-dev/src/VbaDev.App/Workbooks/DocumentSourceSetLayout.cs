namespace VbaDev.App.Workbooks;

/// <summary>
/// Provides source set file discovery, identity checks, and form sidecar layout operations.
/// </summary>
public static class DocumentSourceSetLayout
{
    /// <summary>
    /// Enumerates exported VBA source files under a document source set.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <returns>The discovered source files with inferred source kind and form sidecar path.</returns>
    public static IReadOnlyList<VbaSourceFile> EnumerateVbaSourceFiles(string sourceSetPath)
        => EnumerateVbaSourcePaths(sourceSetPath)
            .Select(CreateSourceFile)
            .ToArray();

    /// <summary>
    /// Enumerates .bas, .cls, and .frm paths under a document source set.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <returns>The discovered source paths, or an empty list when the directory is absent.</returns>
    public static IReadOnlyList<string> EnumerateVbaSourcePaths(string sourceSetPath)
    {
        if (!Directory.Exists(sourceSetPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(sourceSetPath, "*", SearchOption.AllDirectories)
            .Where(IsVbaSourceFile)
            .ToArray();
    }

    /// <summary>
    /// Enumerates .frx form sidecar paths under a document source set.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <returns>The sidecar paths ordered by path, or an empty list when the directory is absent.</returns>
    public static IReadOnlyList<string> EnumerateFormSidecarPaths(string sourceSetPath)
    {
        if (!Directory.Exists(sourceSetPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(sourceSetPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Throws when multiple source files share the same file name inside one flat document source identity.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory used in the error message.</param>
    /// <param name="sourceFiles">The source files to inspect.</param>
    public static void ThrowIfDuplicateSourceFileNames(
        string sourceSetPath,
        IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        var duplicateGroups = FindSourceFileNameCollisions(sourceFiles.Select(source => source.SourcePath));
        if (duplicateGroups.Count == 0)
        {
            return;
        }

        var lines = duplicateGroups.Select(group =>
            $"Duplicate source file name '{group.FileName}': {string.Join(", ", group.SourcePaths)}");
        throw new InvalidOperationException(
            $"Duplicate VBA source file names were found under {sourceSetPath}.{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
    }

    /// <summary>
    /// Finds flat source identity collisions by case-insensitive file name.
    /// </summary>
    /// <param name="sourcePaths">The source paths to inspect.</param>
    /// <returns>The colliding file names and their matching paths.</returns>
    public static IReadOnlyList<DocumentSourceFileNameCollision> FindSourceFileNameCollisions(IEnumerable<string> sourcePaths)
        => sourcePaths
            .GroupBy(GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DocumentSourceFileNameCollision(
                group.Key,
                group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

    /// <summary>
    /// Inspects source identity and form sidecar placement for a document source set.
    /// </summary>
    /// <param name="documentName">The document name used in diagnostic names.</param>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <returns>Layout diagnostics for duplicate source names and misplaced form sidecars.</returns>
    public static IReadOnlyList<DocumentSourceSetLayoutDiagnostic> InspectSourceIdentity(
        string documentName,
        string sourceSetPath)
    {
        var diagnostics = new List<DocumentSourceSetLayoutDiagnostic>();
        var sourceFiles = EnumerateVbaSourcePaths(sourceSetPath);
        foreach (var group in FindSourceFileNameCollisions(sourceFiles))
        {
            diagnostics.Add(DocumentSourceSetLayoutDiagnostic.Fail(
                $"Document source identity ({documentName}/{group.FileName})",
                $"Duplicate exported source file name. Colliding files: {string.Join(", ", group.SourcePaths)}."));
        }

        var formFilesByName = sourceFiles
            .Where(IsFormFile)
            .GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var sidecarPath in EnumerateFormSidecarPaths(sourceSetPath))
        {
            var sidecarName = Path.GetFileNameWithoutExtension(sidecarPath);
            if (!formFilesByName.TryGetValue(sidecarName, out var matchingForms))
            {
                continue;
            }

            if (HasSameDirectoryForm(sidecarPath))
            {
                continue;
            }

            diagnostics.Add(DocumentSourceSetLayoutDiagnostic.Warn(
                $"Form sidecar ({documentName}/{Path.GetFileName(sidecarPath)})",
                $"Sidecar has no same-directory .frm, but a same-name form exists elsewhere: {sidecarPath}. Matching forms: {string.Join(", ", matchingForms)}."));
        }

        return diagnostics;
    }

    /// <summary>
    /// Finds source files whose file name matches a manifest or CommonModules module file.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <param name="moduleFile">The module file name to match.</param>
    /// <returns>Matching source paths ordered by path.</returns>
    public static IReadOnlyList<string> FindSourceMatches(string sourceSetPath, string moduleFile)
        => EnumerateVbaSourcePaths(sourceSetPath)
            .Where(path => GetFileName(path).Equals(moduleFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Finds form sidecars whose base name matches a module file.
    /// </summary>
    /// <param name="sourceSetPath">The source set root directory.</param>
    /// <param name="moduleFile">The source module file name.</param>
    /// <returns>Matching .frx paths ordered by path.</returns>
    public static IReadOnlyList<string> FindFormSidecars(string sourceSetPath, string moduleFile)
    {
        var formName = Path.GetFileNameWithoutExtension(moduleFile);
        return EnumerateFormSidecarPaths(sourceSetPath)
            .Where(path => Path.GetFileNameWithoutExtension(path).Equals(formName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves a same-directory .frx sidecar for a form source file.
    /// </summary>
    /// <param name="formSourcePath">The .frm source path.</param>
    /// <returns>The matching sidecar path, or null when none exists.</returns>
    public static string? ResolveExistingSidecarPath(string formSourcePath)
    {
        var directory = Path.GetDirectoryName(formSourcePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var formBaseName = Path.GetFileNameWithoutExtension(formSourcePath);
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(path).Equals(formBaseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Determines whether a sidecar has a same-directory .frm file with the same base name.
    /// </summary>
    /// <param name="sidecarPath">The .frx sidecar path.</param>
    /// <returns>True when a matching same-directory form source exists.</returns>
    public static bool HasSameDirectoryForm(string sidecarPath)
    {
        var directory = Path.GetDirectoryName(sidecarPath);
        var sidecarName = Path.GetFileNameWithoutExtension(sidecarPath);
        return directory is not null &&
            Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path =>
                    IsFormFile(path) &&
                    Path.GetFileNameWithoutExtension(path).Equals(sidecarName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Captures existing source file placement so export can preserve project-local organization directories.
    /// </summary>
    /// <param name="destinationDirectory">The destination source directory.</param>
    /// <returns>A map from source file name to its first existing relative path.</returns>
    public static IReadOnlyDictionary<string, string> CaptureExistingSourceLayout(string destinationDirectory)
    {
        var relativePathsByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateVbaSourcePaths(destinationDirectory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            relativePathsByFileName.TryAdd(
                GetFileName(path),
                Path.GetRelativePath(destinationDirectory, path));
        }

        return relativePathsByFileName;
    }

    /// <summary>
    /// Deletes exported VBA source files and form sidecars from a destination directory.
    /// </summary>
    /// <param name="destinationDirectory">The destination directory to clean.</param>
    public static void DeleteVbaSourceAndSidecars(string destinationDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
        {
            if (IsVbaSourceOrSidecar(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Restores exported source files from a temporary export directory while preserving known relative paths.
    /// </summary>
    /// <param name="temporaryDirectory">The directory containing freshly exported source files.</param>
    /// <param name="destinationDirectory">The destination source directory.</param>
    /// <param name="existingSourceLayout">The prior source file placement map captured before cleanup.</param>
    public static void RestoreExportedSourceLayout(
        string temporaryDirectory,
        string destinationDirectory,
        IReadOnlyDictionary<string, string> existingSourceLayout)
    {
        var exportedSourceFiles = EnumerateVbaSourcePaths(temporaryDirectory)
            .OrderBy(path => GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var exportedSourceFile in exportedSourceFiles)
        {
            var exportedFileName = GetFileName(exportedSourceFile);
            var targetRelativePath = existingSourceLayout.TryGetValue(exportedFileName, out var existingRelativePath)
                ? existingRelativePath
                : exportedFileName;
            var targetPath = Path.Combine(destinationDirectory, targetRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(exportedSourceFile, targetPath, overwrite: true);

            if (!IsFormFile(exportedSourceFile))
            {
                continue;
            }

            var exportedSidecar = ResolveExistingSidecarPath(exportedSourceFile);
            if (exportedSidecar is null)
            {
                continue;
            }

            File.Copy(exportedSidecar, Path.ChangeExtension(targetPath, ".frx"), overwrite: true);
        }
    }

    /// <summary>
    /// Determines whether a path is an exported VBA source file.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>True for .bas, .cls, and .frm paths.</returns>
    public static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a path is an exported VBA source file or form sidecar.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>True for .bas, .cls, .frm, and .frx paths.</returns>
    public static bool IsVbaSourceOrSidecar(string path)
    {
        var extension = Path.GetExtension(path);
        return IsVbaSourceFile(path) || extension.Equals(".frx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a path is an exported VBA form source file.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>True for .frm paths.</returns>
    public static bool IsFormFile(string path)
        => Path.GetExtension(path).Equals(".frm", StringComparison.OrdinalIgnoreCase);

    private static VbaSourceFile CreateSourceFile(string path)
    {
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bas" => VbaSourceKind.StandardModule,
            ".cls" => VbaSourceKind.ClassModule,
            ".frm" => VbaSourceKind.Form,
            _ => throw new InvalidOperationException($"Unsupported VBA source file: {path}")
        };

        var binaryPath = kind == VbaSourceKind.Form
            ? ResolveExistingSidecarPath(path)
            : null;

        return new VbaSourceFile(
            SourcePath: path,
            Kind: kind,
            BinaryPath: binaryPath);
    }

    private static string GetFileName(string path)
        => Path.GetFileName(path) ?? string.Empty;
}

/// <summary>
/// Describes a flat source identity collision in a document source set.
/// </summary>
/// <param name="FileName">The duplicate source file name.</param>
/// <param name="SourcePaths">The colliding source paths.</param>
public sealed record DocumentSourceFileNameCollision(
    string FileName,
    IReadOnlyList<string> SourcePaths);

/// <summary>
/// Represents a source set layout diagnostic produced by source identity inspection.
/// </summary>
/// <param name="Status">The diagnostic severity.</param>
/// <param name="Name">The diagnostic name.</param>
/// <param name="Message">The user-facing diagnostic message.</param>
public sealed record DocumentSourceSetLayoutDiagnostic(
    DocumentSourceSetLayoutDiagnosticStatus Status,
    string Name,
    string Message)
{
    /// <summary>
    /// Creates a failing source set layout diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A failing layout diagnostic.</returns>
    public static DocumentSourceSetLayoutDiagnostic Fail(string name, string message)
        => new(DocumentSourceSetLayoutDiagnosticStatus.Fail, name, message);

    /// <summary>
    /// Creates a warning source set layout diagnostic.
    /// </summary>
    /// <param name="name">The diagnostic name.</param>
    /// <param name="message">The diagnostic message.</param>
    /// <returns>A warning layout diagnostic.</returns>
    public static DocumentSourceSetLayoutDiagnostic Warn(string name, string message)
        => new(DocumentSourceSetLayoutDiagnosticStatus.Warn, name, message);
}

/// <summary>
/// Represents the severity of a document source set layout diagnostic.
/// </summary>
public enum DocumentSourceSetLayoutDiagnosticStatus
{
    /// <summary>
    /// A layout problem that prevents reliable source identity.
    /// </summary>
    Fail,

    /// <summary>
    /// A layout problem that should be reviewed but does not block the command.
    /// </summary>
    Warn
}
