using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;

namespace VbaTools.Syntax;

/// <summary>
/// Identifies the complete MS-VBAL lexical forms that can accept a VBA name.
/// </summary>
[Flags]
public enum VbaIdentifierForm
{
    /// <summary>No complete identifier form accepts the name.</summary>
    None = 0,

    /// <summary>The MS-VBAL Latin identifier form.</summary>
    Latin = 1 << 0,

    /// <summary>The MS-VBAL single-byte Windows code-page identifier form.</summary>
    CodePage = 1 << 1,

    /// <summary>The MS-VBAL Windows code page 932 identifier form.</summary>
    Japanese = 1 << 2,

    /// <summary>The MS-VBAL Windows code page 949 identifier form.</summary>
    Korean = 1 << 3,

    /// <summary>The MS-VBAL Windows code page 936 identifier form.</summary>
    SimplifiedChinese = 1 << 4,

    /// <summary>The MS-VBAL Windows code page 950 identifier form.</summary>
    TraditionalChinese = 1 << 5,

    /// <summary>Every identifier form recognized by this authority.</summary>
    All = Latin | CodePage | Japanese | Korean | SimplifiedChinese | TraditionalChinese
}

/// <summary>
/// Provides the parser-owned lexical authority for VBA identifiers.
/// </summary>
public static class VbaIdentifier
{
    /// <summary>The exact MS-VBAL WSC characters, formatted for a regular-expression character class.</summary>
    public const string RegexWhitespaceCharacters =
        "\\u0009\\u0019\\u0020\\u1680\\u180e\\u2000-\\u200a\\u202f\\u205f\\u3000";

    /// <summary>A regular-expression atom that matches exactly one MS-VBAL WSC character.</summary>
    public const string RegexWhitespace = "[" + RegexWhitespaceCharacters + "]";
    internal const string RegexIdentifierCandidate =
        "[^" + RegexWhitespaceCharacters
        + "\\r\\n()%&^!#@$.,:;+\\-*/\\\\<>='\"\\[\\]]+";

    /// <summary>The MS-VBAL revision implemented by the conformance data.</summary>
    public const string SpecificationRevision = VbaIdentifierConformanceData.SpecificationRevision;

    /// <summary>The source of the Unicode-to-code-page mappings used by this authority.</summary>
    public const string CodePageMappingProvenance = VbaIdentifierConformanceData.MappingProvenance;

    /// <summary>The pinned SHA-256 of each Microsoft WindowsBestFit source table.</summary>
    public static IReadOnlyDictionary<int, string> CodePageMappingSha256
        => PinnedCodePageMappingSha256;

    private static readonly IReadOnlyDictionary<int, string> PinnedCodePageMappingSha256 =
        new ReadOnlyDictionary<int, string>(
            new Dictionary<int, string>(VbaIdentifierConformanceData.MappingSha256));

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

    /// <summary>
    /// Gets every complete MS-VBAL lexical form that accepts the supplied name.
    /// </summary>
    /// <param name="value">The exact, unnormalized candidate name.</param>
    /// <returns>The forms that accept the whole name, or <see cref="VbaIdentifierForm.None"/>.</returns>
    public static VbaIdentifierForm GetForms(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return VbaIdentifierForm.None;
        }

        var possibleForms = VbaIdentifierForm.All;
        var index = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            possibleForms &= GetCharacterForms(rune, initial: index == 0);
            if (possibleForms == VbaIdentifierForm.None)
            {
                return VbaIdentifierForm.None;
            }

            index++;
        }

        return index == 0 ? VbaIdentifierForm.None : possibleForms;
    }

    /// <summary>
    /// Determines whether the exact value is one complete MS-VBAL lex-identifier.
    /// </summary>
    public static bool IsLexIdentifier(string value)
        => GetForms(value) != VbaIdentifierForm.None;

    /// <summary>
    /// Determines whether the exact value is an MS-VBAL IDENTIFIER rather than a reserved identifier.
    /// </summary>
    public static bool IsIdentifier(string value)
        => IsLexIdentifier(value) && !IsReservedIdentifier(value);

    /// <summary>
    /// Determines whether the value is in the complete case-insensitive MS-VBAL reserved-identifier set.
    /// </summary>
    public static bool IsReservedIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ReservedIdentifiers.Contains(value);
    }

    /// <summary>
    /// Reads one contiguous identifier candidate and tracks the forms that accept it completely.
    /// </summary>
    /// <param name="text">Source text beginning at a possible identifier.</param>
    /// <param name="forms">The complete forms that accept the whole candidate.</param>
    /// <returns>The UTF-16 length of the candidate, or zero when it is not identifier-shaped.</returns>
    internal static int ReadCandidateLength(ReadOnlySpan<char> text, out VbaIdentifierForm forms)
    {
        forms = VbaIdentifierForm.None;
        if (text.IsEmpty || !TryReadRune(text, 0, out var first))
        {
            return 0;
        }

        var possibleForms = GetCharacterForms(first, initial: true);
        if (possibleForms == VbaIdentifierForm.None
            && GetCharacterForms(first, initial: false) == VbaIdentifierForm.None)
        {
            return 0;
        }

        var length = first.Utf16SequenceLength;
        while (length < text.Length && TryReadRune(text, length, out var next))
        {
            var subsequentForms = GetCharacterForms(next, initial: false);
            if (subsequentForms == VbaIdentifierForm.None)
            {
                break;
            }

            possibleForms &= subsequentForms;
            length += next.Utf16SequenceLength;
        }

        forms = possibleForms;
        return length;
    }

    /// <summary>
    /// Determines whether a character participates in any MS-VBAL identifier form after the initial character.
    /// </summary>
    public static bool IsWordCharacter(Rune value)
        => GetCharacterForms(value, initial: false) != VbaIdentifierForm.None;

    /// <summary>
    /// Determines whether one UTF-16 code unit participates in an MS-VBAL identifier after the initial character.
    /// </summary>
    public static bool IsWordCharacter(char value)
        => Rune.TryCreate(value, out var rune) && IsWordCharacter(rune);

    /// <summary>
    /// Determines whether a character can begin any MS-VBAL lex-identifier form.
    /// </summary>
    public static bool IsInitialCharacter(Rune value)
        => GetCharacterForms(value, initial: true) != VbaIdentifierForm.None;

    /// <summary>
    /// Determines whether one UTF-16 code unit can begin any MS-VBAL lex-identifier form.
    /// </summary>
    public static bool IsInitialCharacter(char value)
        => Rune.TryCreate(value, out var rune) && IsInitialCharacter(rune);

    /// <summary>
    /// Determines whether a character is MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static bool IsWhitespace(Rune value)
        => (value.Value is 0x0009 or 0x0019 or 0x0020 or 0x1680 or 0x180e or 0x202f or 0x205f or 0x3000
                or >= 0x2000 and <= 0x200a)
            && !IsCp2Character(value);

    /// <summary>
    /// Determines whether one UTF-16 code unit is MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static bool IsWhitespace(char value)
        => Rune.TryCreate(value, out var rune) && IsWhitespace(rune);

    /// <summary>
    /// Removes only leading MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static string TrimStartWhitespace(string value)
    {
        var index = 0;
        while (index < value.Length && IsWhitespace(value[index]))
        {
            index++;
        }

        return index == 0 ? value : value[index..];
    }

    /// <summary>
    /// Removes only trailing MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static string TrimEndWhitespace(string value)
    {
        var index = value.Length;
        while (index > 0 && IsWhitespace(value[index - 1]))
        {
            index--;
        }

        return index == value.Length ? value : value[..index];
    }

    /// <summary>
    /// Removes only leading and trailing MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static string TrimWhitespace(string value)
        => TrimEndWhitespace(TrimStartWhitespace(value));

    /// <summary>
    /// Determines whether every character is MS-VBAL layout whitespace (WSC).
    /// </summary>
    public static bool IsWhitespaceOnly(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!IsWhitespace(character))
            {
                return false;
            }
        }

        return true;
    }

    private static VbaIdentifierForm GetCharacterForms(Rune rune, bool initial)
    {
        if (IsAsciiLetter(rune.Value))
        {
            return VbaIdentifierForm.All;
        }

        if (!initial && (rune.Value is >= '0' and <= '9' || rune.Value == '_'))
        {
            return VbaIdentifierForm.All;
        }

        if (rune.Value <= 0x7f)
        {
            return VbaIdentifierForm.None;
        }

        var forms = VbaIdentifierForm.None;
        if (Contains(VbaIdentifierConformanceData.Cp2Ranges, rune))
        {
            forms |= VbaIdentifierForm.CodePage;
        }

        if (Contains(
            initial
                ? VbaIdentifierConformanceData.JapaneseInitialRanges
                : VbaIdentifierConformanceData.JapaneseSubsequentRanges,
            rune))
        {
            forms |= VbaIdentifierForm.Japanese;
        }

        if (Contains(
            initial
                ? VbaIdentifierConformanceData.SimplifiedChineseInitialRanges
                : VbaIdentifierConformanceData.SimplifiedChineseSubsequentRanges,
            rune))
        {
            forms |= VbaIdentifierForm.SimplifiedChinese;
        }

        if (Contains(
            initial
                ? VbaIdentifierConformanceData.KoreanInitialRanges
                : VbaIdentifierConformanceData.KoreanSubsequentRanges,
            rune))
        {
            forms |= VbaIdentifierForm.Korean;
        }

        if (Contains(
            initial
                ? VbaIdentifierConformanceData.TraditionalChineseInitialRanges
                : VbaIdentifierConformanceData.TraditionalChineseSubsequentRanges,
            rune))
        {
            forms |= VbaIdentifierForm.TraditionalChinese;
        }

        return forms;
    }

    private static bool TryReadRune(ReadOnlySpan<char> text, int index, out Rune rune)
        => Rune.DecodeFromUtf16(text[index..], out rune, out _) == OperationStatus.Done;

    private static bool IsAsciiLetter(int value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsCp2Character(Rune rune)
        => Contains(VbaIdentifierConformanceData.Cp2Ranges, rune);

    private static bool Contains(ReadOnlySpan<int> ranges, Rune rune)
        => VbaIdentifierConformanceData.Contains(ranges, rune.Value);
}
