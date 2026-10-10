namespace VbaDev.Domain;

/// <summary>Describes compatibility-only workbook bin configuration without changing files.</summary>
public static class LegacyWorkbookBinConfiguration
{
    /// <summary>Identifies the actionable compatibility warning.</summary>
    public const string WarningCode = "project-workbook-bin-deprecated";

    /// <summary>Explains the deprecated setting and the non-destructive migration.</summary>
    public static string GetWarning(string documentName)
        => $"The binPath setting for document '{documentName}' is deprecated and scheduled for removal. "
            + "Remove binPath from vba-project.json; ordinary Build, Debug, Test, and project Export use templatePath. "
            + "Existing bin files are left unchanged.";
}
