using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>
/// Represents one parsed source document used by semantic inventory construction.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The complete source text.</param>
/// <param name="ModuleName">The parsed module identity.</param>
/// <param name="Definitions">The definitions declared by the document.</param>
/// <param name="SyntaxTree">The parsed syntax tree for features that need structured syntax.</param>
public sealed record VbaSourceDocument(
    string Uri,
    string Text,
    string ModuleName,
    IReadOnlyList<VbaSourceDefinition> Definitions,
    VbaSyntaxTree? SyntaxTree = null)
{
    internal VbaSourceDocumentProjection? Projection { get; init; }
}

internal sealed record VbaSourceDocumentProjection(
    VbaSyntaxTree SyntaxTree,
    IReadOnlyList<VbaSourceDefinition> Definitions);

/// <summary>
/// Represents a definition or reference location.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Range">The source range.</param>
public sealed record VbaDefinitionLocation(string Uri, VbaRange Range);
