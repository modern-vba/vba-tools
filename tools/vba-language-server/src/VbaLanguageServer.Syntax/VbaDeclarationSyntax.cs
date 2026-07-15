namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the declaration category parsed from VBA source.
/// </summary>
public enum VbaDeclarationKind
{
    /// <summary>
    /// A Sub or Function procedure declaration.
    /// </summary>
    Procedure,

    /// <summary>
    /// A Property Get, Let, or Set declaration.
    /// </summary>
    Property,

    /// <summary>
    /// A Const declaration.
    /// </summary>
    Constant,

    /// <summary>
    /// A variable, field, or module-level declaration.
    /// </summary>
    Variable,

    /// <summary>
    /// A callable parameter declaration.
    /// </summary>
    Parameter,

    /// <summary>
    /// An Enum declaration block.
    /// </summary>
    Enum,

    /// <summary>
    /// A member inside an Enum declaration block.
    /// </summary>
    EnumMember,

    /// <summary>
    /// A user-defined Type declaration block.
    /// </summary>
    Type,

    /// <summary>
    /// A member inside a user-defined Type declaration block.
    /// </summary>
    TypeMember,

    /// <summary>
    /// An Event declaration.
    /// </summary>
    Event
}

/// <summary>
/// Identifies the accessor keyword declared by a VBA Property procedure.
/// </summary>
public enum VbaPropertyAccessorKind
{
    /// <summary>
    /// A Property Get accessor.
    /// </summary>
    Get,

    /// <summary>
    /// A Property Let accessor.
    /// </summary>
    Let,

    /// <summary>
    /// A Property Set accessor.
    /// </summary>
    Set
}

/// <summary>
/// Represents the visibility scope parsed for a VBA declaration.
/// </summary>
public enum VbaDeclarationVisibility
{
    /// <summary>
    /// A declaration visible outside the current module.
    /// </summary>
    Public,

    /// <summary>
    /// A declaration visible only inside the current module.
    /// </summary>
    Private,

    /// <summary>
    /// A procedure-local declaration.
    /// </summary>
    Local
}

/// <summary>
/// Represents an explicit VBA type annotation.
/// </summary>
/// <param name="Name">The type name segment.</param>
/// <param name="Qualifier">The optional qualifier, such as a module or reference qualifier.</param>
/// <param name="IsNew">Whether the annotation includes the New keyword.</param>
public sealed record VbaTypeReferenceSyntax(
    string Name,
    string? Qualifier = null,
    bool IsNew = false);

/// <summary>
/// Represents a top-level member block used as an incremental parse replacement unit.
/// </summary>
/// <param name="Name">The member name.</param>
/// <param name="Kind">The member declaration kind.</param>
/// <param name="BlockRange">The source range covered by the member block.</param>
/// <param name="IsExternal">Whether the member is declared with Declare.</param>
/// <param name="IsStatic">Whether the member is declared Static.</param>
public sealed record VbaModuleMemberSyntax(
    string Name,
    VbaDeclarationKind Kind,
    VbaSyntaxRange BlockRange,
    bool IsExternal = false,
    bool IsStatic = false);

/// <summary>
/// Represents one parsed VBA definition available to editor features.
/// </summary>
/// <param name="Name">The declaration name.</param>
/// <param name="Kind">The declaration kind.</param>
/// <param name="Visibility">The parsed declaration visibility.</param>
/// <param name="Range">The source range of the declaration name or header.</param>
/// <param name="LineIndex">The zero-based physical line containing the declaration.</param>
/// <param name="Documentation">The attached Doxygen-style documentation comment text.</param>
/// <param name="Signature">The callable signature when the declaration is callable.</param>
/// <param name="ParentProcedureName">The containing procedure name for local declarations.</param>
/// <param name="ParentProcedureRange">The containing procedure block range for local declarations.</param>
/// <param name="ParentTypeName">The containing enum or user-defined type name for members.</param>
/// <param name="TypeReference">The parsed explicit type annotation.</param>
/// <param name="IsWithEvents">Whether the declaration includes WithEvents.</param>
/// <param name="IsExternal">Whether the declaration is an external Declare member.</param>
/// <param name="IsStatic">Whether the declaration includes Static.</param>
/// <param name="DeclarationLabel">The editor-facing declaration summary for hover display.</param>
/// <param name="CallableKind">The callable kind keyword used in rich signature labels.</param>
/// <param name="PropertyAccessorKind">The declared Property accessor kind.</param>
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
    bool IsStatic = false,
    string? DeclarationLabel = null,
    string? CallableKind = null,
    VbaPropertyAccessorKind? PropertyAccessorKind = null);

/// <summary>
/// Represents a parsed callable declaration and its full source block.
/// </summary>
/// <param name="Name">The callable name.</param>
/// <param name="Kind">The callable declaration kind.</param>
/// <param name="Visibility">The parsed callable visibility.</param>
/// <param name="Range">The source range of the callable declaration header.</param>
/// <param name="BlockRange">The source range of the full callable block.</param>
/// <param name="Parameters">The parsed callable parameters.</param>
/// <param name="Documentation">The attached Doxygen-style documentation comment text.</param>
/// <param name="Signature">The display signature for completion, hover, and signature help.</param>
/// <param name="TypeReference">The callable return type, when present.</param>
/// <param name="LineIndex">The zero-based physical line containing the callable declaration.</param>
/// <param name="OriginalLine">The original declaration line text.</param>
/// <param name="IsExternal">Whether the callable is an external Declare member.</param>
/// <param name="IsStatic">Whether the callable includes Static.</param>
/// <param name="DeclarationKeyword">The callable keyword used by editor-facing declaration labels.</param>
/// <param name="PropertyAccessorKind">The declared Property accessor kind.</param>
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
    bool IsStatic = false,
    string? DeclarationKeyword = null,
    VbaPropertyAccessorKind? PropertyAccessorKind = null);

/// <summary>
/// Represents one parsed parameter in a callable declaration.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Range">The source range of the parameter name.</param>
/// <param name="Documentation">The parameter documentation from an attached documentation comment.</param>
/// <param name="TypeReference">The parsed explicit parameter type annotation.</param>
/// <param name="IsOptional">Whether the parameter is declared Optional.</param>
/// <param name="IsByRef">Whether the parameter is effectively passed ByRef.</param>
/// <param name="IsParamArray">Whether the parameter is declared ParamArray.</param>
/// <param name="IsArray">Whether the parameter name carries a VBA array marker.</param>
public sealed record VbaCallableParameterSyntax(
    string Name,
    VbaSyntaxRange Range,
    string? Documentation,
    VbaTypeReferenceSyntax? TypeReference,
    bool IsOptional = false,
    bool IsByRef = true,
    bool IsParamArray = false,
    bool IsArray = false);

/// <summary>
/// Represents the display signature for a callable definition.
/// </summary>
/// <param name="Label">The complete signature label shown to the user.</param>
/// <param name="Parameters">The ordered parameter metadata for signature help.</param>
/// <param name="Documentation">The callable documentation text.</param>
public sealed record VbaCallableSignatureSyntax(
    string Label,
    IReadOnlyList<VbaCallableParameterInfoSyntax> Parameters,
    string? Documentation = null);

/// <summary>
/// Represents parameter metadata used by callable signature help.
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Documentation">The parameter documentation text.</param>
/// <param name="IsOptional">Whether the parameter is declared Optional.</param>
/// <param name="TypeReference">The parsed explicit parameter type annotation.</param>
/// <param name="IsByRef">Whether the parameter is effectively passed ByRef.</param>
/// <param name="IsParamArray">Whether the parameter is declared ParamArray.</param>
/// <param name="IsArray">Whether the parameter name carries a VBA array marker.</param>
public sealed record VbaCallableParameterInfoSyntax(
    string Name,
    string? Documentation = null,
    bool IsOptional = false,
    VbaTypeReferenceSyntax? TypeReference = null,
    bool IsByRef = true,
    bool IsParamArray = false,
    bool IsArray = false);
