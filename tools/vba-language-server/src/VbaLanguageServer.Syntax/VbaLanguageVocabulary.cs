namespace VbaLanguageServer.Syntax;

/// <summary>
/// Provides canonical casing for fixed VBA language vocabulary.
/// </summary>
public static class VbaLanguageVocabulary
{
    private static readonly IReadOnlySet<string> BareCallableKeywords =
        new HashSet<string>(["Date", "String"], StringComparer.OrdinalIgnoreCase);

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
            ["debug"] = "Debug",
            ["dim"] = "Dim",
            ["do"] = "Do",
            ["double"] = "Double",
            ["each"] = "Each",
            ["empty"] = "Empty",
            ["else"] = "Else",
            ["elseif"] = "ElseIf",
            ["end"] = "End",
            ["enum"] = "Enum",
            ["eqv"] = "Eqv",
            ["event"] = "Event",
            ["exit"] = "Exit",
            ["explicit"] = "Explicit",
            ["false"] = "False",
            ["for"] = "For",
            ["friend"] = "Friend",
            ["function"] = "Function",
            ["get"] = "Get",
            ["global"] = "Global",
            ["gosub"] = "GoSub",
            ["goto"] = "GoTo",
            ["if"] = "If",
            ["imp"] = "Imp",
            ["in"] = "In",
            ["integer"] = "Integer",
            ["is"] = "Is",
            ["let"] = "Let",
            ["lib"] = "Lib",
            ["like"] = "Like",
            ["long"] = "Long",
            ["longlong"] = "LongLong",
            ["longptr"] = "LongPtr",
            ["loop"] = "Loop",
            ["me"] = "Me",
            ["mod"] = "Mod",
            ["new"] = "New",
            ["next"] = "Next",
            ["not"] = "Not",
            ["nothing"] = "Nothing",
            ["null"] = "Null",
            ["object"] = "Object",
            ["on"] = "On",
            ["option"] = "Option",
            ["optional"] = "Optional",
            ["or"] = "Or",
            ["paramarray"] = "ParamArray",
            ["preserve"] = "Preserve",
            ["private"] = "Private",
            ["property"] = "Property",
            ["ptrsafe"] = "PtrSafe",
            ["public"] = "Public",
            ["raiseevent"] = "RaiseEvent",
            ["redim"] = "ReDim",
            ["rem"] = "Rem",
            ["resume"] = "Resume",
            ["select"] = "Select",
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
        CreateOrderedWords(CanonicalKeywords.Values);

    /// <summary>
    /// Gets words that can begin a declaration at module scope.
    /// </summary>
    public static readonly IReadOnlyList<string> ModuleDeclarationWords = CreateOrderedWords([
        "Const",
        "Declare",
        "Dim",
        "Enum",
        "Event",
        "Friend",
        "Function",
        "Global",
        "Option",
        "Private",
        "Property",
        "Public",
        "Static",
        "Sub",
        "Type"
    ]);

    /// <summary>
    /// Gets words that can begin a statement inside a procedure body.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcedureStatementWords = CreateOrderedWords([
        "Call",
        "Const",
        "Debug",
        "Dim",
        "Do",
        "Exit",
        "For",
        "GoSub",
        "GoTo",
        "If",
        "Let",
        "On",
        "RaiseEvent",
        "ReDim",
        "Rem",
        "Resume",
        "Select",
        "Set",
        "Static",
        "While",
        "With"
    ]);

    /// <summary>
    /// Gets fixed words that can supply a value where an expression operand is expected.
    /// </summary>
    public static readonly IReadOnlyList<string> ExpressionValueWords = CreateOrderedWords([
        "Empty",
        "False",
        "Me",
        "New",
        "Not",
        "Nothing",
        "Null",
        "True"
    ]);

    private static readonly IReadOnlyList<string> StandardModuleExpressionValueWords =
        CreateOrderedWords(ExpressionValueWords.Where(word => !word.Equals(
            "Me",
            StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Gets fixed expression values valid for the specified module kind.
    /// </summary>
    /// <param name="moduleKind">The parsed VBA module kind.</param>
    /// <returns>The expression vocabulary valid in the module.</returns>
    public static IReadOnlyList<string> GetExpressionValueWords(VbaModuleKind moduleKind)
        => moduleKind == VbaModuleKind.StandardModule
            ? StandardModuleExpressionValueWords
            : ExpressionValueWords;

    /// <summary>
    /// Gets fixed VBA type names that are valid in type annotation completions.
    /// </summary>
    public static readonly IReadOnlyList<string> TypeNames = CreateOrderedWords([
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
    ]);

    /// <summary>
    /// Determines whether a value is known fixed VBA language vocabulary.
    /// </summary>
    /// <param name="value">The candidate word.</param>
    /// <returns>True when the word has canonical vocabulary casing.</returns>
    public static bool IsKeyword(string value)
        => CanonicalKeywords.ContainsKey(value);

    /// <summary>
    /// Determines whether a bare word can syntactically identify a call target.
    /// </summary>
    /// <param name="value">The unqualified target word.</param>
    /// <returns>
    /// True for identifiers and callable intrinsic keywords; false for grouping syntax words.
    /// </returns>
    public static bool CanBeBareCallTarget(string value)
        => !IsKeyword(value) || BareCallableKeywords.Contains(value);

    private static IReadOnlyList<string> CreateOrderedWords(IEnumerable<string> words)
        => Array.AsReadOnly(words
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(word => word, StringComparer.OrdinalIgnoreCase)
            .ToArray());
}
