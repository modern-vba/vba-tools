namespace VbaDev.Infrastructure.Workbooks;

internal interface IOwnedExcelLoadedModules
{
    IReadOnlyList<string> CaptureLoadedModulePaths();
}

internal static class WorkbookReferenceLibraryPath
{
    internal static string Resolve(string observedPath, IReadOnlyList<string> loadedModulePaths)
    {
        if (File.Exists(observedPath) || IsResourceQualifiedTypeLibPath(observedPath)) return observedPath;
        var candidates = loadedModulePaths.Where(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(observedPath), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length != 1 || !File.Exists(candidates[0]))
            throw new InvalidOperationException($"Observed library '{observedPath}' is not readable and the owned Excel process does not expose one matching loaded library path. Repair the library or Office installation.");
        // The caller must still check this file's TypeLib GUID/version/namespace.
        return candidates[0];
    }

    private static bool IsResourceQualifiedTypeLibPath(string path)
    {
        var libraryPath = Path.GetDirectoryName(path);
        var resourceSelector = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(libraryPath)
            && resourceSelector.Length > 0
            && resourceSelector.All(char.IsAsciiDigit)
            && File.Exists(libraryPath);
    }
}
