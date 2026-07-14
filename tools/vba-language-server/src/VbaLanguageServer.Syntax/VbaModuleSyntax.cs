namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents the parsed syntax model for one exported VBA module source file.
/// </summary>
/// <param name="Kind">The module kind inferred from the source file.</param>
/// <param name="Identity">The parsed module identity.</param>
/// <param name="Attributes">The module-level Attribute declarations.</param>
/// <param name="Options">The module-level Option statements.</param>
/// <param name="Members">The top-level member blocks in source order.</param>
/// <param name="Declarations">All parsed declarations available to editor features.</param>
/// <param name="CallableDeclarations">The parsed callable declarations with signature metadata.</param>
/// <param name="Statements">The parsed statement and block nodes.</param>
/// <param name="Expressions">The parsed expression fragments.</param>
/// <param name="ArgumentLists">The parsed call argument lists.</param>
/// <param name="CompletionContexts">The parsed completion contexts.</param>
/// <param name="PreprocessorDirectives">The parsed preprocessor directive lines.</param>
/// <param name="PreprocessorBlocks">The parsed preprocessor blocks.</param>
/// <param name="FormDesignerBlock">The non-code designer block for a form module, when present.</param>
/// <param name="CodeStartLine">The zero-based physical line where executable module code begins.</param>
/// <param name="Range">The source range covered by the module syntax.</param>
public sealed record VbaModuleSyntax(
    VbaModuleKind Kind,
    VbaModuleIdentitySyntax Identity,
    IReadOnlyList<VbaModuleAttributeSyntax> Attributes,
    IReadOnlyList<VbaModuleOptionSyntax> Options,
    IReadOnlyList<VbaModuleMemberSyntax> Members,
    IReadOnlyList<VbaDeclarationSyntax> Declarations,
    IReadOnlyList<VbaCallableDeclarationSyntax> CallableDeclarations,
    IReadOnlyList<VbaStatementSyntax> Statements,
    IReadOnlyList<VbaExpressionSyntax> Expressions,
    IReadOnlyList<VbaArgumentListSyntax> ArgumentLists,
    IReadOnlyList<VbaCompletionContextSyntax> CompletionContexts,
    IReadOnlyList<VbaPreprocessorDirectiveSyntax> PreprocessorDirectives,
    IReadOnlyList<VbaPreprocessorBlockSyntax> PreprocessorBlocks,
    VbaFormDesignerBlock? FormDesignerBlock,
    int CodeStartLine,
    VbaSyntaxRange Range);

/// <summary>
/// Represents the parsed module identity, usually from Attribute VB_Name.
/// </summary>
/// <param name="Name">The module identity name.</param>
/// <param name="Range">The source range of the identity name.</param>
public sealed record VbaModuleIdentitySyntax(
    string Name,
    VbaSyntaxRange Range);

/// <summary>
/// Represents a module-level Attribute assignment.
/// </summary>
/// <param name="Name">The attribute name.</param>
/// <param name="Value">The attribute value.</param>
/// <param name="Range">The source range of the full attribute statement.</param>
/// <param name="NameRange">The source range of the attribute name.</param>
/// <param name="ValueRange">The source range of the attribute value.</param>
public sealed record VbaModuleAttributeSyntax(
    string Name,
    string Value,
    VbaSyntaxRange Range,
    VbaSyntaxRange NameRange,
    VbaSyntaxRange ValueRange);

/// <summary>
/// Represents a module-level Option statement.
/// </summary>
/// <param name="Text">The full option statement text.</param>
/// <param name="Range">The source range of the option statement.</param>
public sealed record VbaModuleOptionSyntax(
    string Text,
    VbaSyntaxRange Range);

/// <summary>
/// Represents the non-code designer text at the top of an exported form module.
/// </summary>
/// <param name="RawText">The raw designer block text.</param>
/// <param name="Range">The source range covered by the designer block.</param>
public sealed record VbaFormDesignerBlock(
    string RawText,
    VbaSyntaxRange Range);
