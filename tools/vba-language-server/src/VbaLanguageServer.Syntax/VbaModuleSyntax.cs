namespace VbaLanguageServer.Syntax;

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
    VbaFormDesignerBlock? FormDesignerBlock,
    int CodeStartLine,
    VbaSyntaxRange Range);

public sealed record VbaModuleIdentitySyntax(
    string Name,
    VbaSyntaxRange Range);

public sealed record VbaModuleAttributeSyntax(
    string Name,
    string Value,
    VbaSyntaxRange Range,
    VbaSyntaxRange NameRange,
    VbaSyntaxRange ValueRange);

public sealed record VbaModuleOptionSyntax(
    string Text,
    VbaSyntaxRange Range);

public sealed record VbaFormDesignerBlock(
    string RawText,
    VbaSyntaxRange Range);
