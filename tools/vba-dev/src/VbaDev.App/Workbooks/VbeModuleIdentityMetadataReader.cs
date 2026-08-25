using System.Text;
using System.Text.RegularExpressions;

namespace VbaDev.App.Workbooks;

internal sealed record VbeModuleIdentityAuthority(string? Name, string? Failure)
{
    public bool IsAuthoritative => Name is not null && Failure is null;

    public static VbeModuleIdentityAuthority Authoritative(string name) => new(name, null);

    public static VbeModuleIdentityAuthority Invalid(string failure) => new(null, failure);
}

/// <summary>
/// Reads import-time identity authority without accepting parser filename recovery.
/// </summary>
internal static partial class VbeModuleIdentityMetadataReader
{
    private const string VbaWscCharacters = "\\u0009\\u0019\\u0020\\u1680\\u2000-\\u200a\\u202f\\u205f\\u3000";
    private const string VbaWsc = "[" + VbaWscCharacters + "]";
    private static readonly IReadOnlySet<string> ReservedIdentifiers =
        new HashSet<string>(
            [
                "Abs", "AddressOf", "And", "Any", "Array", "As", "Attribute",
                "Boolean", "Byte", "ByRef", "ByVal", "Call", "Case", "CBool",
                "CByte", "CCur", "CDate", "CDecl", "CDec", "CDbl", "Circle",
                "CInt", "CLng", "CLngLng", "CLngPtr", "Close", "Const", "CSng",
                "CStr", "Currency", "CVar", "CVErr", "Date", "Debug", "Decimal",
                "Declare", "DefBool", "DefByte", "DefCur", "DefDate", "DefDbl",
                "DefDec", "DefInt", "DefLng", "DefLngLng", "DefLngPtr", "DefObj",
                "DefSng", "DefStr", "DefVar", "Dim", "Do", "DoEvents", "Double",
                "Each", "Else", "ElseIf", "Empty", "End", "EndIf", "Enum", "Eqv",
                "Erase", "Event", "Exit", "False", "Fix", "For", "Friend",
                "Function", "Get", "Global", "GoSub", "GoTo", "If", "Imp",
                "Implements", "In", "Input", "InputB", "Int", "Integer", "Is",
                "LBound", "Len", "LenB", "Let", "Like", "LINEINPUT", "Lock",
                "Long", "LongLong", "LongPtr", "Loop", "LSet", "Me", "Mod", "New",
                "Next", "Not", "Nothing", "Null", "On", "Open", "Option", "Optional",
                "Or", "ParamArray", "Preserve", "Print", "Private", "PSet", "Public",
                "Put", "RaiseEvent", "ReDim", "Rem", "Resume", "Return", "RSet",
                "Scale", "Seek", "Select", "Set", "Sgn", "Shared", "Single", "Spc",
                "Static", "Stop", "String", "Sub", "Tab", "Then", "To", "True",
                "Type", "TypeOf", "UBound", "Unlock", "Until", "Variant", "VB_Base",
                "VB_Control", "VB_Creatable", "VB_Customizable", "VB_Description",
                "VB_Exposed", "VB_Ext_KEY", "VB_GlobalNameSpace", "VB_HelpID",
                "VB_Invoke_Func", "VB_Invoke_Property", "VB_Invoke_PropertyPut",
                "VB_Invoke_PropertyPutRef", "VB_MemberFlags", "VB_Name",
                "VB_PredeclaredId", "VB_ProcData", "VB_TemplateDerived", "VB_UserMemId",
                "VB_VarDescription", "VB_VarHelpID", "VB_VarMemberFlags",
                "VB_VarProcData", "VB_VarUserMemId", "Wend", "While", "With",
                "WithEvents", "Write", "Xor"
            ],
            StringComparer.OrdinalIgnoreCase);

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
    private static readonly Regex TrimVbaLayoutPattern = new(
        "^" + VbaWsc + "+|" + VbaWsc + "+$",
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

    public static VbeModuleIdentityAuthority Read(string text, VbaSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        return sourceKind == VbaSourceKind.StandardModule
            ? ReadStandardModule(lines)
            : ReadObjectModule(lines);
    }

    private static VbeModuleIdentityAuthority ReadStandardModule(IReadOnlyList<string> lines)
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
            return VbeModuleIdentityAuthority.Invalid(
                "contains duplicate ModuleIdentity metadata.");
        }

        return CreateAuthority(records[0].Match);
    }

    private static VbeModuleIdentityAuthority ReadObjectModule(IReadOnlyList<string> lines)
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
            if (IsVbNameLikeRecord(lines[index]) &&
                (index < header.Start || index >= headerEnd))
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
            var line = TrimVbaLayout(lines[index]);
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

    private static VbeModuleIdentityAuthority CreateAuthority(Match record)
    {
        var name = record.Groups["name"].Value;
        return IsValidModuleName(name)
            ? VbeModuleIdentityAuthority.Authoritative(name)
            : Invalid();
    }

    private static VbeModuleIdentityAuthority Missing()
        => VbeModuleIdentityAuthority.Invalid(
            "does not contain authoritative ModuleIdentity metadata.");

    private static VbeModuleIdentityAuthority Invalid()
        => VbeModuleIdentityAuthority.Invalid(
            "contains invalid ModuleIdentity metadata.");

    private static bool IsClassHeaderAttribute(string line)
        => FixedFalseClassAttributePattern.IsMatch(line)
            || BooleanClassAttributePattern.IsMatch(line);

    private static string TrimVbaLayout(string value)
        => TrimVbaLayoutPattern.Replace(value, string.Empty);

    private static bool IsVbNameLikeRecord(string line)
    {
        var prefix = VbNamePrefixPattern.Match(line);
        if (!prefix.Success || prefix.Length == line.Length)
        {
            return prefix.Success;
        }

        var next = line[prefix.Length..].EnumerateRunes().First();
        return GetIdentifierForms(next, initial: false) == IdentifierForm.None;
    }

    private static bool IsValidModuleName(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length is <= 0 or > 31 || ReservedIdentifiers.Contains(value))
        {
            return false;
        }

        var possibleForms = IdentifierForm.All;
        for (var index = 0; index < runes.Length; index++)
        {
            possibleForms &= GetIdentifierForms(runes[index], initial: index == 0);
            if (possibleForms == IdentifierForm.None)
            {
                return false;
            }
        }

        return true;
    }

    private static IdentifierForm GetIdentifierForms(Rune rune, bool initial)
    {
        var forms = IdentifierForm.None;
        if (IsAsciiLetter(rune.Value) ||
            !initial && (rune.Value is >= '0' and <= '9' || rune.Value == '_'))
        {
            forms = IdentifierForm.All;
        }

        if (IsCp2Character(rune))
        {
            forms |= IdentifierForm.CodePage;
        }

        if (IsDbcsIdentifierCharacter(rune, 932, initial, IsCp932IdentifierCodePoint))
        {
            forms |= IdentifierForm.Japanese;
        }

        if (IsDbcsIdentifierCharacter(rune, 936, initial, IsCp936IdentifierCodePoint))
        {
            forms |= IdentifierForm.SimplifiedChinese;
        }

        if (IsDbcsIdentifierCharacter(rune, 949, initial, IsCp949IdentifierCodePoint))
        {
            forms |= IdentifierForm.Korean;
        }

        if (IsDbcsIdentifierCharacter(rune, 950, initial, IsCp950IdentifierCodePoint))
        {
            forms |= IdentifierForm.TraditionalChinese;
        }

        return forms;
    }

    private static bool IsAsciiLetter(int value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsCp2Character(Rune rune)
    {
        foreach (var codePage in new[] { 874, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258 })
        {
            if (TryEncodeCodePoint(rune, codePage, out var bytes) &&
                bytes.Length == 1 &&
                bytes[0] >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDbcsIdentifierCharacter(
        Rune rune,
        int codePage,
        bool initial,
        Func<int, bool, bool> isAllowedCodePoint)
    {
        if (!TryEncodeCodePoint(rune, codePage, out var bytes) || bytes.Length is < 1 or > 2)
        {
            return false;
        }

        var codePoint = bytes.Aggregate(0, (value, next) => (value << 8) | next);
        return codePoint > 0x7f && isAllowedCodePoint(codePoint, initial);
    }

    private static bool TryEncodeCodePoint(Rune rune, int codePage, out byte[] bytes)
    {
        try
        {
            var encoding = Encoding.GetEncoding(
                codePage,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            bytes = encoding.GetBytes(rune.ToString());
            return encoding.GetString(bytes).Equals(rune.ToString(), StringComparison.Ordinal);
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
        catch (DecoderFallbackException)
        {
            bytes = [];
            return false;
        }
        catch (ArgumentException)
        {
            bytes = [];
            return false;
        }
    }

    private static bool IsCp932IdentifierCodePoint(int codePoint, bool initial)
    {
        if (codePoint <= 0xff && codePoint is >= 0x81 and <= 0x9f or >= 0xe0 and <= 0xfc)
        {
            return false;
        }

        if (codePoint == 0x8140 ||
            codePoint is >= 0x8143 and <= 0x8151 ||
            codePoint is >= 0x815e and <= 0x8197)
        {
            return false;
        }

        return !initial || codePoint is < 0x824f or > 0x8258;
    }

    private static bool IsCp936IdentifierCodePoint(int codePoint, bool initial)
        => codePoint is >= 0xa3c1 and <= 0xa3da
            or >= 0xa3e1 and <= 0xa3fa
            or >= 0xa1a2 and <= 0xa1aa
            or >= 0xa1ac and <= 0xa1ad
            or >= 0xa1b2 and <= 0xa1e6
            or >= 0xa1e8 and <= 0xa1ef
            or >= 0xa2b1 and <= 0xa2fc
            or >= 0xa4a1 and <= 0xfe4f
            || !initial && (codePoint == 0xa3df || codePoint is >= 0xa3b0 and <= 0xa3b9);

    private static bool IsCp949IdentifierCodePoint(int codePoint, bool initial)
    {
        var lead = codePoint >> 8;
        var trailing = codePoint & 0xff;
        return lead < 0xa1 ||
            lead > 0xaf ||
            trailing < 0xa1 ||
            trailing > 0xfe ||
            codePoint is >= 0xa3c1 and <= 0xa3da ||
            codePoint is >= 0xa3e1 and <= 0xa3fa ||
            codePoint is >= 0xa4a1 and <= 0xa4fe ||
            !initial && (codePoint == 0xa3df || codePoint is >= 0xa3b0 and <= 0xa3b9);
    }

    private static bool IsCp950IdentifierCodePoint(int codePoint, bool initial)
        => codePoint is >= 0xa2cf and <= 0xa2fe
            or >= 0xa340 and <= 0xf9dd
            || !initial && (codePoint == 0xa1c5 || codePoint is >= 0xa2af and <= 0xa2b8);

    [Flags]
    private enum IdentifierForm
    {
        None = 0,
        Latin = 1 << 0,
        CodePage = 1 << 1,
        Japanese = 1 << 2,
        Korean = 1 << 3,
        SimplifiedChinese = 1 << 4,
        TraditionalChinese = 1 << 5,
        All = Latin | CodePage | Japanese | Korean | SimplifiedChinese | TraditionalChinese
    }

    private enum DesignerBlockKind
    {
        Component,
        Property
    }

    private sealed record ObjectHeader(int Start, bool Invalid);
}
