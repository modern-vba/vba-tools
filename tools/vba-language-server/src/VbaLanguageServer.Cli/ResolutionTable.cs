using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Identifies a definition independently from the transient definition object instance.
/// </summary>
/// <param name="Uri">The source or catalog URI that owns the definition.</param>
/// <param name="Name">The definition name.</param>
/// <param name="Range">The source range of the definition.</param>
internal sealed record VbaDefinitionIdentity(string Uri, string Name, VbaRange Range)
{
    /// <summary>
    /// Creates an identity from a source definition.
    /// </summary>
    /// <param name="definition">The definition to identify.</param>
    /// <returns>The stable definition identity.</returns>
    public static VbaDefinitionIdentity FromDefinition(VbaSourceDefinition definition)
        => new(definition.Uri, definition.Name, definition.Range);
}

/// <summary>
/// Owns per-snapshot resolution facts used by references, rename, and semantic-token features.
/// </summary>
internal sealed class VbaResolutionTable
{
    private readonly Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition;

    /// <summary>
    /// Creates a resolution table over a snapshot resolver.
    /// </summary>
    /// <param name="resolveSourceDefinition">The resolver for document positions.</param>
    public VbaResolutionTable(Func<string, int, int, VbaSourceDefinition?> resolveSourceDefinition)
    {
        this.resolveSourceDefinition = resolveSourceDefinition;
    }

    /// <summary>
    /// Resolves the definition referenced at a document position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based character.</param>
    /// <returns>The resolved definition, or null when unresolved.</returns>
    public VbaSourceDefinition? ResolveSourceDefinition(string uri, int line, int character)
        => resolveSourceDefinition(uri, line, character);

    /// <summary>
    /// Gets the stable identity for a definition.
    /// </summary>
    /// <param name="definition">The definition to identify.</param>
    /// <returns>The definition identity.</returns>
    public VbaDefinitionIdentity GetIdentity(VbaSourceDefinition definition)
        => VbaDefinitionIdentity.FromDefinition(definition);

    /// <summary>
    /// Determines whether a definition can be renamed by source edits.
    /// </summary>
    /// <param name="definition">The resolved definition.</param>
    /// <returns>True when rename should be allowed.</returns>
    public bool IsRenameTarget(VbaSourceDefinition definition)
        => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
            && (definition.Visibility == VbaSourceDefinitionVisibility.Local || IsReferenceTarget(definition));

    /// <summary>
    /// Determines whether two definitions have the same identity.
    /// </summary>
    /// <param name="left">The left definition.</param>
    /// <param name="right">The right definition.</param>
    /// <returns>True when both definitions identify the same source or catalog declaration.</returns>
    public bool SameDefinition(VbaSourceDefinition left, VbaSourceDefinition right)
        => SameIdentity(GetIdentity(left), GetIdentity(right));

    /// <summary>
    /// Determines whether two definition identities match.
    /// </summary>
    /// <param name="left">The left identity.</param>
    /// <param name="right">The right identity.</param>
    /// <returns>True when both identities point to the same definition.</returns>
    public bool SameIdentity(VbaDefinitionIdentity left, VbaDefinitionIdentity right)
        => SameUri(left.Uri, right.Uri)
            && SameName(left.Name, right.Name)
            && ComparePosition(left.Range.Start, right.Range.Start) == 0
            && ComparePosition(left.Range.End, right.Range.End) == 0;

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }
}
