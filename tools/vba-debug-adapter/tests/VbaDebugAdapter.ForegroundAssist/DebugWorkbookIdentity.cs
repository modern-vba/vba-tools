namespace VbaDebugAdapter.ForegroundAssist;

public static class DebugWorkbookIdentity
{
    public static bool Matches(
        string workspaceRoot, string workbookFileName, string workbookPath, string ownerToken)
    {
        try
        {
            if (!IsCanonicalLowerHexId(ownerToken))
            {
                return false;
            }

            if (!string.Equals(Path.GetFileName(workbookFileName), workbookFileName,
                    StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(workbookFileName), ".xlsm",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot),
                Path.GetFullPath(workbookPath));
            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.None);
            if (parts.Length != 5 ||
                !IsCanonicalLowerHexId(parts[0]) ||
                !string.Equals(parts[1], "generations", StringComparison.OrdinalIgnoreCase) ||
                parts[2].Length == 0 ||
                !string.Equals(parts[3], "output", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parts[4], workbookFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A generated Book1.xlsm alone is not proof that this test owns the Excel window.
            var sourcePath = Path.Combine(Path.GetFullPath(workspaceRoot), parts[0],
                "generations", parts[2], "source", "nested", "Caller.bas");
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (source.Length > 64 * 1024)
            {
                return false;
            }

            var sourceBytes = new byte[checked((int)source.Length)];
            source.ReadExactly(sourceBytes);
            return sourceBytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(ownerToken)) >= 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or
            UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool IsCanonicalLowerHexId(string value) =>
        value.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
