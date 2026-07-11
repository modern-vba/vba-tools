namespace VbaDev.App.Workbooks;

public static class DocumentSourceSetLayout
{
    public static IReadOnlyList<VbaSourceFile> EnumerateVbaSourceFiles(string sourceSetPath)
        => EnumerateVbaSourcePaths(sourceSetPath)
            .Select(CreateSourceFile)
            .ToArray();

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

    public static IReadOnlyList<DocumentSourceFileNameCollision> FindSourceFileNameCollisions(IEnumerable<string> sourcePaths)
        => sourcePaths
            .GroupBy(GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DocumentSourceFileNameCollision(
                group.Key,
                group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

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

    public static IReadOnlyList<string> FindSourceMatches(string sourceSetPath, string moduleFile)
        => EnumerateVbaSourcePaths(sourceSetPath)
            .Where(path => GetFileName(path).Equals(moduleFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> FindFormSidecars(string sourceSetPath, string moduleFile)
    {
        var formName = Path.GetFileNameWithoutExtension(moduleFile);
        return EnumerateFormSidecarPaths(sourceSetPath)
            .Where(path => Path.GetFileNameWithoutExtension(path).Equals(formName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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

    public static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsVbaSourceOrSidecar(string path)
    {
        var extension = Path.GetExtension(path);
        return IsVbaSourceFile(path) || extension.Equals(".frx", StringComparison.OrdinalIgnoreCase);
    }

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

public sealed record DocumentSourceFileNameCollision(
    string FileName,
    IReadOnlyList<string> SourcePaths);

public sealed record DocumentSourceSetLayoutDiagnostic(
    DocumentSourceSetLayoutDiagnosticStatus Status,
    string Name,
    string Message)
{
    public static DocumentSourceSetLayoutDiagnostic Fail(string name, string message)
        => new(DocumentSourceSetLayoutDiagnosticStatus.Fail, name, message);

    public static DocumentSourceSetLayoutDiagnostic Warn(string name, string message)
        => new(DocumentSourceSetLayoutDiagnosticStatus.Warn, name, message);
}

public enum DocumentSourceSetLayoutDiagnosticStatus
{
    Fail,
    Warn
}
