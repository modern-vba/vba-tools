using System.Text;
using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the exported-source structure used to locate ModuleIdentity metadata.
/// </summary>
public enum VbaModuleIdentitySourceKind
{
    /// <summary>A standard, class, or other module whose metadata starts on line one.</summary>
    StandardModule,

    /// <summary>A form or document module that can contain an exported designer header.</summary>
    ObjectModule
}

/// <summary>
/// Describes whether exported source provides authoritative ModuleIdentity metadata.
/// </summary>
public enum VbaModuleIdentityMetadataState
{
    Missing,
    Invalid,
    Authoritative
}

/// <summary>
/// Identifies the structured repair condition for invalid ModuleIdentity metadata.
/// </summary>
public enum VbaModuleIdentityMetadataCondition
{
    Duplicate,
    Malformed
}

/// <summary>
/// Represents the exact ModuleIdentity authority found in exported VBA source.
/// </summary>
public sealed record VbaModuleIdentityMetadataRecord(
    string? Name,
    VbaSyntaxRange RecordRange,
    VbaSyntaxRange RepairRange,
    bool IsMalformedOrMisplaced);

/// <summary>
/// Represents the exact ModuleIdentity authority and every repair candidate found in source.
/// </summary>
public sealed record VbaModuleIdentityMetadata
{
    public VbaModuleIdentityMetadata(
        VbaModuleIdentityMetadataState state,
        string? name,
        string? failure,
        VbaModuleIdentityMetadataCondition? condition = null,
        IReadOnlyList<VbaModuleIdentityMetadataRecord>? records = null,
        int? authoritativeRecordIndex = null)
    {
        State = state;
        Name = name;
        Failure = failure;
        Condition = condition;
        Records = records ?? Array.Empty<VbaModuleIdentityMetadataRecord>();
        AuthoritativeRecordIndex = authoritativeRecordIndex;
    }

    public VbaModuleIdentityMetadataState State { get; }

    public string? Name { get; }

    public string? Failure { get; }

    public VbaModuleIdentityMetadataCondition? Condition { get; }

    public IReadOnlyList<VbaModuleIdentityMetadataRecord> Records { get; }

    public int? AuthoritativeRecordIndex { get; }

    public bool IsAuthoritative
        => State == VbaModuleIdentityMetadataState.Authoritative
            && Name is not null
            && Failure is null;
}

/// <summary>
/// Reads explicit import-time ModuleIdentity metadata without filename recovery.
/// </summary>
public static class VbaModuleIdentityMetadataReader
{
    private const string VbaWsc = VbaIdentifier.RegexWhitespace;

    private static readonly Regex VbNamePrefixPattern = new(
        "^" + VbaWsc + "*Attribute" + VbaWsc + "+(?<keyword>VB_Name)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ValidVbNamePattern = new(
        "^" + VbaWsc + "*Attribute" + VbaWsc + "+VB_Name" + VbaWsc + "*=" + VbaWsc + "*\"(?<name>[^\"]+)\"" + VbaWsc + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AttributeKeywordPattern = new(
        "^" + VbaWsc + "*Attribute(?=" + VbaWsc + "|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BlankLinePattern = new(
        "^" + VbaWsc + "*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionKeywordPattern = new(
        "^" + VbaWsc + "*VERSION(?=" + VbaWsc + "|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BeginDesignerPattern = new(
        "^Begin(?:Property)?(?=" + VbaWsc + "|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BeginPropertyDesignerPattern = new(
        "^BeginProperty(?=" + VbaWsc + "|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectAssignmentPattern = new(
        "^Object" + VbaWsc + "*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FixedFalseClassAttributePattern = new(
        "^" + VbaWsc + "*Attribute" + VbaWsc + "+(?:VB_GlobalNameSpace|VB_Creatable)" + VbaWsc + "*=" + VbaWsc + "*False" + VbaWsc + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BooleanClassAttributePattern = new(
        "^" + VbaWsc + "*Attribute" + VbaWsc + "+(?:VB_PredeclaredId|VB_Exposed|VB_Customizable)" + VbaWsc + "*=" + VbaWsc + "*(?:True|False)" + VbaWsc + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads exact ModuleIdentity metadata from one exported VBA source text.
    /// </summary>
    public static VbaModuleIdentityMetadata Read(
        string text,
        VbaModuleIdentitySourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sourceText = VbaSourceText.From(text);
        return sourceKind == VbaModuleIdentitySourceKind.StandardModule
            ? ReadStandardModule(sourceText)
            : ReadObjectModule(sourceText);
    }

    private static VbaModuleIdentityMetadata ReadStandardModule(
        VbaSourceText sourceText)
    {
        var records = sourceText.Lines
            .Where(line => IsVbNameLikeRecord(line.Text))
            .Select(line => CreateRecord(sourceText, line, false))
            .ToArray();

        if (records.Length == 0)
        {
            return Missing();
        }

        if (records.Any(record => record.IsMalformedOrMisplaced))
        {
            return Invalid(VbaModuleIdentityMetadataCondition.Malformed, records: records);
        }

        if (records.Length != 1)
        {
            return Invalid(
                VbaModuleIdentityMetadataCondition.Duplicate,
                "contains duplicate ModuleIdentity metadata.",
                records);
        }

        if (records[0].RecordRange.Start.Line != 0)
        {
            records[0] = records[0] with { IsMalformedOrMisplaced = true };
            return Invalid(VbaModuleIdentityMetadataCondition.Malformed, records: records);
        }

        return CreateAuthority(records, 0);
    }

    private static VbaModuleIdentityMetadata ReadObjectModule(
        VbaSourceText sourceText)
    {
        var lines = sourceText.Lines.Select(line => line.Text).ToArray();
        var header = LocateObjectHeader(lines);
        var headerEnd = header.Start;
        var records = new List<VbaModuleIdentityMetadataRecord>();
        var invalid = header.Invalid;
        if (header.Start >= 0)
        {
            while (headerEnd < lines.Length)
            {
                var line = lines[headerEnd];
                if (IsVbNameLikeRecord(line))
                {
                    var record = CreateRecord(sourceText, sourceText.Lines[headerEnd], false);
                    records.Add(record);
                    invalid |= record.IsMalformedOrMisplaced;

                    headerEnd++;
                    continue;
                }

                if (IsClassHeaderAttribute(line))
                {
                    headerEnd++;
                    continue;
                }

                break;
            }
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (IsVbNameLikeRecord(lines[index])
                && (index < header.Start || index >= headerEnd))
            {
                records.Add(CreateRecord(sourceText, sourceText.Lines[index], true));
                invalid = true;
            }
        }

        if (records.Count == 0)
        {
            return Missing();
        }

        if (invalid)
        {
            return Invalid(VbaModuleIdentityMetadataCondition.Malformed, records: records);
        }

        return CreateAuthority(records, records.Count - 1);
    }

    private static ObjectHeader LocateObjectHeader(IReadOnlyList<string> lines)
    {
        var firstNonempty = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!BlankLinePattern.IsMatch(lines[index]))
            {
                firstNonempty = index;
                break;
            }
        }

        if (firstNonempty < 0)
        {
            return new ObjectHeader(-1, false);
        }

        if (AttributeKeywordPattern.IsMatch(lines[firstNonempty]))
        {
            return new ObjectHeader(firstNonempty, false);
        }

        if (!VersionKeywordPattern.IsMatch(lines[firstNonempty]))
        {
            return new ObjectHeader(-1, true);
        }

        var designerBlocks = new Stack<DesignerBlockKind>();
        var sawDesignerBlock = false;
        for (var index = firstNonempty + 1; index < lines.Count; index++)
        {
            var line = VbaIdentifier.TrimWhitespace(lines[index]);
            if (line.Length == 0)
            {
                continue;
            }

            if (BeginDesignerPattern.IsMatch(line))
            {
                designerBlocks.Push(BeginPropertyDesignerPattern.IsMatch(line)
                    ? DesignerBlockKind.Property
                    : DesignerBlockKind.Component);
                sawDesignerBlock = true;
                continue;
            }

            var closingBlock = line.Equals("End", StringComparison.OrdinalIgnoreCase)
                ? DesignerBlockKind.Component
                : line.Equals("EndProperty", StringComparison.OrdinalIgnoreCase)
                    ? DesignerBlockKind.Property
                    : (DesignerBlockKind?)null;
            if (closingBlock is not null)
            {
                if (designerBlocks.Count == 0 || designerBlocks.Peek() != closingBlock)
                {
                    return new ObjectHeader(-1, true);
                }

                designerBlocks.Pop();
                continue;
            }

            if (designerBlocks.Count > 0)
            {
                continue;
            }

            if (AttributeKeywordPattern.IsMatch(line))
            {
                return new ObjectHeader(index, false);
            }

            if (!sawDesignerBlock && ObjectAssignmentPattern.IsMatch(line))
            {
                continue;
            }

            return new ObjectHeader(-1, true);
        }

        return new ObjectHeader(-1, true);
    }

    private static VbaModuleIdentityMetadataRecord CreateRecord(
        VbaSourceText sourceText,
        VbaSourceLine line,
        bool misplaced)
    {
        var prefix = VbNamePrefixPattern.Match(line.Text);
        var match = ValidVbNamePattern.Match(line.Text);
        var recordStart = line.Text.Length - line.Text.TrimStart().Length;
        var recordEnd = line.Text.TrimEnd().Length;
        var recordRange = sourceText.RangeForLine(line, recordStart, recordEnd);
        if (match.Success)
        {
            var nameGroup = match.Groups["name"];
            var validName = IsValidModuleName(nameGroup.Value);
            return new VbaModuleIdentityMetadataRecord(
                validName ? nameGroup.Value : null,
                recordRange,
                CreateNonemptyRange(
                    sourceText,
                    line,
                    nameGroup.Index,
                    nameGroup.Length,
                    match.Value.LastIndexOf("\"\"", StringComparison.Ordinal) >= 0
                        ? match.Value.IndexOf("\"\"", StringComparison.Ordinal)
                        : prefix.Groups["keyword"].Index,
                    match.Value.LastIndexOf("\"\"", StringComparison.Ordinal) >= 0
                        ? 2
                        : prefix.Groups["keyword"].Length),
                misplaced || !validName);
        }

        var equalsIndex = line.Text.IndexOf('=', prefix.Index + prefix.Length);
        if (equalsIndex >= 0)
        {
            var rightStart = equalsIndex + 1;
            while (rightStart < line.Text.Length
                && VbaIdentifier.IsWhitespace(line.Text[rightStart]))
            {
                rightStart++;
            }

            var rightEnd = line.Text.Length;
            while (rightEnd > rightStart
                && VbaIdentifier.IsWhitespace(line.Text[rightEnd - 1]))
            {
                rightEnd--;
            }

            if (rightEnd > rightStart)
            {
                return new VbaModuleIdentityMetadataRecord(
                    null,
                    recordRange,
                    sourceText.RangeForLine(line, rightStart, rightEnd),
                    true);
            }
        }

        var keyword = prefix.Groups["keyword"];
        return new VbaModuleIdentityMetadataRecord(
            null,
            recordRange,
            sourceText.RangeForLine(line, keyword.Index, keyword.Index + keyword.Length),
            true);
    }

    private static VbaSyntaxRange CreateNonemptyRange(
        VbaSourceText sourceText,
        VbaSourceLine line,
        int start,
        int length,
        int fallbackStart,
        int fallbackLength)
        => length > 0
            ? sourceText.RangeForLine(line, start, start + length)
            : sourceText.RangeForLine(line, fallbackStart, fallbackStart + fallbackLength);

    private static VbaModuleIdentityMetadata CreateAuthority(
        IReadOnlyList<VbaModuleIdentityMetadataRecord> records,
        int authoritativeRecordIndex)
    {
        var name = records[authoritativeRecordIndex].Name;
        return name is not null
            ? new VbaModuleIdentityMetadata(
                VbaModuleIdentityMetadataState.Authoritative,
                name,
                null,
                records: records,
                authoritativeRecordIndex: authoritativeRecordIndex)
            : Invalid(VbaModuleIdentityMetadataCondition.Malformed, records: records);
    }

    private static VbaModuleIdentityMetadata Missing()
        => new(
            VbaModuleIdentityMetadataState.Missing,
            null,
            "does not contain authoritative ModuleIdentity metadata.");

    private static VbaModuleIdentityMetadata Invalid(
        VbaModuleIdentityMetadataCondition condition,
        string failure = "contains invalid ModuleIdentity metadata.",
        IReadOnlyList<VbaModuleIdentityMetadataRecord>? records = null)
        => new(
            VbaModuleIdentityMetadataState.Invalid,
            null,
            failure,
            condition,
            records);

    private static bool IsClassHeaderAttribute(string line)
        => FixedFalseClassAttributePattern.IsMatch(line)
            || BooleanClassAttributePattern.IsMatch(line);

    private static bool IsVbNameLikeRecord(string line)
    {
        var prefix = VbNamePrefixPattern.Match(line);
        if (!prefix.Success || prefix.Length == line.Length)
        {
            return prefix.Success;
        }

        var next = line[prefix.Length..].EnumerateRunes().First();
        return !VbaIdentifier.IsWordCharacter(next);
    }

    private static bool IsValidModuleName(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        return runes.Length is > 0 and <= 31
            && VbaIdentifier.IsIdentifier(value);
    }

    private enum DesignerBlockKind
    {
        Component,
        Property
    }

    private sealed record ObjectHeader(int Start, bool Invalid);
}
