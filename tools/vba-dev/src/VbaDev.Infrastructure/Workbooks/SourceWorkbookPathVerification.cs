using VbaDev.Infrastructure.FileSystem;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>Confirms that an Excel Save still refers to the selected source path.</summary>
internal static class SourceWorkbookPathVerification
{
    internal static bool IsSelectedWorkbookPath(object workbookObject, string selectedPath)
    {
        dynamic workbook = workbookObject;
        var observedPath = Convert.ToString(workbook.FullName);
        if (string.IsNullOrWhiteSpace(observedPath) || !Path.IsPathRooted(observedPath))
            return false;

        var resolver = new FileSystemPathIdentityResolver();
        var selected = resolver.Resolve(selectedPath);
        var observed = resolver.Resolve(observedPath);
        return Path.TrimEndingDirectorySeparator(selected.CanonicalPath).Equals(
            Path.TrimEndingDirectorySeparator(observed.CanonicalPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
