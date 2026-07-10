namespace VbaLanguageServer.Syntax;

public sealed record VbaToken(VbaTokenKind Kind, string Text, VbaSyntaxRange Range);
