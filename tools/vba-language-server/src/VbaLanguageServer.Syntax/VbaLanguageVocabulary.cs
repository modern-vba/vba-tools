namespace VbaLanguageServer.Syntax;

public static class VbaLanguageVocabulary
{
    public static readonly IReadOnlyDictionary<string, string> CanonicalKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alias"] = "Alias",
            ["and"] = "And",
            ["as"] = "As",
            ["attribute"] = "Attribute",
            ["boolean"] = "Boolean",
            ["byref"] = "ByRef",
            ["byval"] = "ByVal",
            ["call"] = "Call",
            ["case"] = "Case",
            ["const"] = "Const",
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

    public static readonly IReadOnlyList<string> Keywords =
        CanonicalKeywords.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsKeyword(string value)
        => CanonicalKeywords.ContainsKey(value);
}
