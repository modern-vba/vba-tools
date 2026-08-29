namespace VbaDev.Domain;

/// <summary>
/// Stable reason identifiers for project-creation path validation version 1.0.
/// </summary>
public static class ProjectCreationPathValidationReasons
{
    public const string ProjectNameEmpty = "projectNameEmpty";
    public const string ProjectNameIllFormedUnicode = "projectNameIllFormedUnicode";
    public const string ProjectNameDotSegment = "projectNameDotSegment";
    public const string ProjectNameContainsPathSeparator = "projectNameContainsPathSeparator";
    public const string ProjectNameContainsWindowsInvalidCharacter = "projectNameContainsWindowsInvalidCharacter";
    public const string ProjectNameContainsUnicodeControlCharacter = "projectNameContainsUnicodeControlCharacter";
    public const string ProjectNameHasLeadingOrTrailingWhitespace = "projectNameHasLeadingOrTrailingWhitespace";
    public const string ProjectNameEndsWithDot = "projectNameEndsWithDot";
    public const string ProjectNameUsesReservedDeviceName = "projectNameUsesReservedDeviceName";
    public const string ExcelPathContainsUnsupportedCharacter = "excelPathContainsUnsupportedCharacter";
    public const string ExcelPathTooLong = "excelPathTooLong";
}

/// <summary>
/// Reports the first stable rejection reason from a project-creation path contract.
/// </summary>
/// <param name="Reason">The stable rejection reason, or null when the value is valid.</param>
public sealed record ProjectCreationPathValidationResult(string? Reason)
{
    /// <summary>Gets whether the candidate satisfies the contract.</summary>
    public bool IsValid => Reason is null;
}

/// <summary>
/// Validates a project name as an exact host-neutral Windows basename.
/// </summary>
public static class ProjectNameLexicalContract
{
    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM¹",
            "COM²",
            "COM³",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT¹",
            "LPT²",
            "LPT³"
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first stable rejection reason without rewriting the candidate.
    /// </summary>
    /// <param name="candidate">The exact project-name UTF-16 sequence.</param>
    /// <returns>The validation result.</returns>
    public static ProjectCreationPathValidationResult Validate(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Length == 0)
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameEmpty);
        }

        if (!IsWellFormedUtf16(candidate))
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameIllFormedUnicode);
        }

        if (candidate is "." or "..")
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameDotSegment);
        }

        if (candidate.IndexOfAny(['/', '\\']) >= 0)
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameContainsPathSeparator);
        }

        if (candidate.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameContainsWindowsInvalidCharacter);
        }

        if (candidate.Any(IsUnicodeControlCodeUnit))
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameContainsUnicodeControlCharacter);
        }

        if (IsContractWhitespace(candidate[0]) || IsContractWhitespace(candidate[^1]))
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameHasLeadingOrTrailingWhitespace);
        }

        if (candidate.EndsWith(".", StringComparison.Ordinal))
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameEndsWithDot);
        }

        var firstDot = candidate.IndexOf('.', StringComparison.Ordinal);
        var deviceNameCandidate = firstDot >= 0 ? candidate[..firstDot] : candidate;
        if (ReservedDeviceNames.Contains(deviceNameCandidate))
        {
            return Rejected(ProjectCreationPathValidationReasons.ProjectNameUsesReservedDeviceName);
        }

        return new ProjectCreationPathValidationResult(null);
    }

    private static ProjectCreationPathValidationResult Rejected(string reason)
        => new(reason);

    private static bool IsWellFormedUtf16(string candidate)
    {
        for (var index = 0; index < candidate.Length; index++)
        {
            var codeUnit = candidate[index];
            if (char.IsHighSurrogate(codeUnit))
            {
                if (index + 1 >= candidate.Length || !char.IsLowSurrogate(candidate[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(codeUnit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnicodeControlCodeUnit(char codeUnit)
        => codeUnit <= '\u001f' || codeUnit is >= '\u007f' and <= '\u009f';

    private static bool IsContractWhitespace(char codeUnit)
        => codeUnit is >= '\u0009' and <= '\u000d'
            or '\u0020'
            or '\u0085'
            or '\u00a0'
            or '\u1680'
            or >= '\u2000' and <= '\u200a'
            or >= '\u2028' and <= '\u2029'
            or '\u202f'
            or '\u205f'
            or '\u3000';
}

/// <summary>
/// Validates a complete Excel-facing workbook path using version 1.0 limits.
/// </summary>
public static class ExcelWorkbookPathContract
{
    /// <summary>The inclusive Excel workbook-path limit in UTF-16 code units.</summary>
    public const int MaximumUtf16CodeUnitLength = 218;

    /// <summary>
    /// Returns the first stable rejection reason for a complete workbook path.
    /// </summary>
    /// <param name="candidate">The exact absolute Excel-facing path.</param>
    /// <returns>The validation result.</returns>
    public static ProjectCreationPathValidationResult Validate(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.IndexOfAny(['[', ']']) >= 0)
        {
            return new ProjectCreationPathValidationResult(
                ProjectCreationPathValidationReasons.ExcelPathContainsUnsupportedCharacter);
        }

        return new ProjectCreationPathValidationResult(
            candidate.Length > MaximumUtf16CodeUnitLength
                ? ProjectCreationPathValidationReasons.ExcelPathTooLong
                : null);
    }
}
