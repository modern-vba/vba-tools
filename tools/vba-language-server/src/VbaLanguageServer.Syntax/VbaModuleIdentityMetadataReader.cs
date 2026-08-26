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
/// Represents the exact ModuleIdentity authority found in exported VBA source.
/// </summary>
public sealed record VbaModuleIdentityMetadata(
    VbaModuleIdentityMetadataState State,
    string? Name,
    string? Failure)
{
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
        "^" + VbaWsc + "*Attribute" + VbaWsc + "+VB_Name",
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
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        return sourceKind == VbaModuleIdentitySourceKind.StandardModule
            ? ReadStandardModule(lines)
            : ReadObjectModule(lines);
    }

    private static VbaModuleIdentityMetadata ReadStandardModule(
        IReadOnlyList<string> lines)
    {
        var records = lines
            .Select((line, index) => new { Line = line, Index = index })
            .Where(item => IsVbNameLikeRecord(item.Line))
            .Select(item => new
            {
                item.Index,
                Match = ValidVbNamePattern.Match(item.Line)
            })
            .ToArray();

        if (records.Length == 0)
        {
            return Missing();
        }

        if (records.Any(record => record.Index != 0 || !record.Match.Success))
        {
            return Invalid();
        }

        if (records.Length != 1)
        {
            return Invalid("contains duplicate ModuleIdentity metadata.");
        }

        return CreateAuthority(records[0].Match);
    }

    private static VbaModuleIdentityMetadata ReadObjectModule(
        IReadOnlyList<string> lines)
    {
        var header = LocateObjectHeader(lines);
        var headerEnd = header.Start;
        var records = new List<Match>();
        var invalid = header.Invalid;
        if (header.Start >= 0)
        {
            while (headerEnd < lines.Count)
            {
                var line = lines[headerEnd];
                if (IsVbNameLikeRecord(line))
                {
                    var match = ValidVbNamePattern.Match(line);
                    if (!match.Success || !IsValidModuleName(match.Groups["name"].Value))
                    {
                        invalid = true;
                    }
                    else
                    {
                        records.Add(match);
                    }

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

        for (var index = 0; index < lines.Count; index++)
        {
            if (IsVbNameLikeRecord(lines[index])
                && (index < header.Start || index >= headerEnd))
            {
                invalid = true;
            }
        }

        if (invalid)
        {
            return Invalid();
        }

        if (records.Count == 0)
        {
            return Missing();
        }

        return CreateAuthority(records[^1]);
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

    private static VbaModuleIdentityMetadata CreateAuthority(Match record)
    {
        var name = record.Groups["name"].Value;
        return IsValidModuleName(name)
            ? new VbaModuleIdentityMetadata(
                VbaModuleIdentityMetadataState.Authoritative,
                name,
                null)
            : Invalid();
    }

    private static VbaModuleIdentityMetadata Missing()
        => new(
            VbaModuleIdentityMetadataState.Missing,
            null,
            "does not contain authoritative ModuleIdentity metadata.");

    private static VbaModuleIdentityMetadata Invalid(
        string failure = "contains invalid ModuleIdentity metadata.")
        => new(VbaModuleIdentityMetadataState.Invalid, null, failure);

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
