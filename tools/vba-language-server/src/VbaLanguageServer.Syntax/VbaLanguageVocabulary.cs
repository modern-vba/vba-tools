namespace VbaLanguageServer.Syntax;

/// <summary>
/// Provides canonical casing for fixed VBA language vocabulary.
/// </summary>
public static class VbaLanguageVocabulary
{
    /// <summary>
    /// Maps known VBA vocabulary words to their canonical source formatting spelling.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CanonicalKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alias"] = "Alias",
            ["and"] = "And",
            ["as"] = "As",
            ["attribute"] = "Attribute",
            ["boolean"] = "Boolean",
            ["byte"] = "Byte",
            ["byref"] = "ByRef",
            ["byval"] = "ByVal",
            ["call"] = "Call",
            ["case"] = "Case",
            ["const"] = "Const",
            ["currency"] = "Currency",
            ["date"] = "Date",
            ["declare"] = "Declare",
            ["dim"] = "Dim",
            ["do"] = "Do",
            ["double"] = "Double",
            ["each"] = "Each",
            ["else"] = "Else",
            ["elseif"] = "ElseIf",
            ["end"] = "End",
            ["enum"] = "Enum",
            ["event"] = "Event",
            ["explicit"] = "Explicit",
            ["false"] = "False",
            ["for"] = "For",
            ["friend"] = "Friend",
            ["function"] = "Function",
            ["get"] = "Get",
            ["global"] = "Global",
            ["if"] = "If",
            ["in"] = "In",
            ["integer"] = "Integer",
            ["let"] = "Let",
            ["lib"] = "Lib",
            ["long"] = "Long",
            ["longlong"] = "LongLong",
            ["longptr"] = "LongPtr",
            ["loop"] = "Loop",
            ["new"] = "New",
            ["next"] = "Next",
            ["not"] = "Not",
            ["nothing"] = "Nothing",
            ["object"] = "Object",
            ["option"] = "Option",
            ["or"] = "Or",
            ["private"] = "Private",
            ["property"] = "Property",
            ["public"] = "Public",
            ["set"] = "Set",
            ["single"] = "Single",
            ["static"] = "Static",
            ["string"] = "String",
            ["sub"] = "Sub",
            ["then"] = "Then",
            ["to"] = "To",
            ["true"] = "True",
            ["type"] = "Type",
            ["until"] = "Until",
            ["variant"] = "Variant",
            ["wend"] = "Wend",
            ["while"] = "While",
            ["with"] = "With",
            ["withevents"] = "WithEvents",
            ["xor"] = "Xor"
        };

    /// <summary>
    /// Gets the canonical vocabulary words ordered for completion display.
    /// </summary>
    public static readonly IReadOnlyList<string> Keywords =
        CanonicalKeywords.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Gets fixed VBA type names that are valid in type annotation completions.
    /// </summary>
    public static readonly IReadOnlyList<string> TypeNames =
    [
        "Boolean",
        "Byte",
        "Currency",
        "Date",
        "Double",
        "Integer",
        "Long",
        "LongLong",
        "LongPtr",
        "Object",
        "Single",
        "String",
        "Variant"
    ];

    /// <summary>
    /// Determines whether a value is known fixed VBA language vocabulary.
    /// </summary>
    /// <param name="value">The candidate word.</param>
    /// <returns>True when the word has canonical vocabulary casing.</returns>
    public static bool IsKeyword(string value)
        => CanonicalKeywords.ContainsKey(value);
}
