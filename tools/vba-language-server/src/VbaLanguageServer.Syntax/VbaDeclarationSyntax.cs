namespace VbaLanguageServer.Syntax;

public enum VbaDeclarationKind
{
    Procedure,
    Property,
    Constant,
    Variable,
    Parameter,
    Enum,
    EnumMember,
    Type,
    TypeMember,
    Event
}

public enum VbaDeclarationVisibility
{
    Public,
    Private,
    Local
}

public sealed record VbaTypeReferenceSyntax(
    string Name,
    string? Qualifier = null,
    bool IsNew = false);

public sealed record VbaModuleMemberSyntax(
    string Name,
    VbaDeclarationKind Kind,
    VbaSyntaxRange BlockRange,
    bool IsExternal = false,
    bool IsStatic = false);

public sealed record VbaDeclarationSyntax(
    string Name,
    VbaDeclarationKind Kind,
    VbaDeclarationVisibility Visibility,
    VbaSyntaxRange Range,
    int LineIndex,
    string? Documentation = null,
    VbaCallableSignatureSyntax? Signature = null,
    string? ParentProcedureName = null,
    VbaSyntaxRange? ParentProcedureRange = null,
    string? ParentTypeName = null,
    VbaTypeReferenceSyntax? TypeReference = null,
    bool IsWithEvents = false,
    bool IsExternal = false,
    bool IsStatic = false);

public sealed record VbaCallableDeclarationSyntax(
    string Name,
    VbaDeclarationKind Kind,
    VbaDeclarationVisibility Visibility,
    VbaSyntaxRange Range,
    VbaSyntaxRange BlockRange,
    IReadOnlyList<VbaCallableParameterSyntax> Parameters,
    string? Documentation,
    VbaCallableSignatureSyntax Signature,
    VbaTypeReferenceSyntax? TypeReference,
    int LineIndex,
    string OriginalLine,
    bool IsExternal = false,
    bool IsStatic = false);

public sealed record VbaCallableParameterSyntax(
    string Name,
    VbaSyntaxRange Range,
    string? Documentation,
    VbaTypeReferenceSyntax? TypeReference);

public sealed record VbaCallableSignatureSyntax(
    string Label,
    IReadOnlyList<VbaCallableParameterInfoSyntax> Parameters,
    string? Documentation = null);

public sealed record VbaCallableParameterInfoSyntax(
    string Name,
    string? Documentation = null);
