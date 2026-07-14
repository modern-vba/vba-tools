namespace VbaLanguageServer.Syntax;

/// <summary>
/// Contains the parsed token stream, module syntax, and syntax diagnostics for one VBA source document.
/// </summary>
/// <param name="Uri">The document URI associated with the source text.</param>
/// <param name="Text">The complete source text that was parsed.</param>
/// <param name="TokenStream">The source-range-preserving token stream.</param>
/// <param name="Module">The parsed module syntax model.</param>
/// <param name="Diagnostics">The parser recovery diagnostics produced for the source text.</param>
public sealed record VbaSyntaxTree(
    string Uri,
    string Text,
    VbaTokenStream TokenStream,
    VbaModuleSyntax Module,
    IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics)
{
    /// <summary>
    /// Parses a VBA module source document.
    /// </summary>
    /// <param name="uri">The document URI associated with the source text.</param>
    /// <param name="source">The complete source text to parse.</param>
    /// <returns>The parsed syntax tree.</returns>
    public static VbaSyntaxTree ParseModule(string uri, string source)
        => VbaSyntaxTreeParser.ParseModule(uri, source);

    /// <summary>
    /// Creates reusable lexical facts for this syntax tree's source text.
    /// </summary>
    /// <returns>The lexical fact query module.</returns>
    public VbaLexicalFacts GetLexicalFacts()
        => VbaLexicalFacts.FromSyntaxTree(this);

    /// <summary>
    /// Resolves a member-chain completion context from this syntax tree.
    /// </summary>
    public bool TryGetMemberCompletionContext(
        int line,
        int character,
        out VbaMemberChainContext context)
        => VbaSyntaxContextQueries.TryGetCompletionContext(this, line, character, out context);

    /// <summary>
    /// Determines whether a completed member access is followed by whitespace.
    /// </summary>
    public bool IsCompletedMemberAccessWithTrailingWhitespace(int line, int character)
        => VbaSyntaxContextQueries.IsCompletedMemberAccessWithTrailingWhitespace(this, line, character);

    /// <summary>
    /// Resolves a member-chain reference context from this syntax tree.
    /// </summary>
    public bool TryGetMemberReferenceContext(
        int line,
        int character,
        string memberName,
        out VbaMemberChainContext context)
        => VbaSyntaxContextQueries.TryGetMemberReferenceContext(this, line, character, memberName, out context);

    /// <summary>
    /// Resolves a callable context from this syntax tree.
    /// </summary>
    public bool TryGetCallExpressionContext(
        int line,
        int character,
        out VbaCallExpressionContext context)
        => VbaSyntaxContextQueries.TryGetCallExpressionContext(this, line, character, out context);

    /// <summary>
    /// Resolves the member chain immediately preceding an identifier occurrence.
    /// </summary>
    public static bool TryGetPreviousMemberContext(
        string codePart,
        VbaIdentifierOccurrence occurrence,
        out VbaMemberChainContext context)
        => VbaSyntaxContextQueries.TryGetPreviousMemberContext(codePart, occurrence, out context);

    /// <summary>
    /// Parses source text and classifies whether the change can be treated as a member-level update.
    /// </summary>
    /// <param name="uri">The document URI associated with the source text.</param>
    /// <param name="source">The complete source text to parse.</param>
    /// <param name="previousSyntaxTree">The previous syntax tree for incremental classification.</param>
    /// <returns>The parsed syntax tree and update kind.</returns>
    public static VbaSyntaxTreeParseResult ParseOrUpdate(
        string uri,
        string source,
        VbaSyntaxTree? previousSyntaxTree)
    {
        if (previousSyntaxTree is not null
            && previousSyntaxTree.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)
            && previousSyntaxTree.Text.Equals(source, StringComparison.Ordinal))
        {
            return new VbaSyntaxTreeParseResult(
                previousSyntaxTree,
                VbaSyntaxTreeParseUpdateKind.ModuleMember);
        }

        if (previousSyntaxTree is not null
            && VbaSyntaxTreeIncrementalParser.TryParseModuleMember(
                uri,
                source,
                previousSyntaxTree,
                out var incrementalResult))
        {
            return incrementalResult;
        }

        return new VbaSyntaxTreeParseResult(
            ParseModule(uri, source),
            VbaSyntaxTreeParseUpdateKind.FullModule);
    }
}

/// <summary>
/// Identifies the granularity of a syntax tree update.
/// </summary>
public enum VbaSyntaxTreeParseUpdateKind
{
    /// <summary>
    /// The full module should be refreshed.
    /// </summary>
    FullModule,

    /// <summary>
    /// A single module member changed without touching its block boundary.
    /// </summary>
    ModuleMember
}

/// <summary>
/// Describes a safe ModuleMember-level update plan produced during parsing.
/// </summary>
/// <param name="PreviousMember">The member that contained the previous changed line range.</param>
/// <param name="CurrentMember">The member that contains the current changed line range.</param>
/// <param name="PreviousStartLine">The first changed line in the previous text.</param>
/// <param name="PreviousEndLine">The last changed line in the previous text.</param>
/// <param name="CurrentStartLine">The first changed line in the current text.</param>
/// <param name="CurrentEndLine">The last changed line in the current text.</param>
public sealed record VbaModuleMemberIncrementalUpdate(
    VbaModuleMemberSyntax PreviousMember,
    VbaModuleMemberSyntax CurrentMember,
    int PreviousStartLine,
    int PreviousEndLine,
    int CurrentStartLine,
    int CurrentEndLine);

/// <summary>
/// Contains a parsed syntax tree and the update granularity inferred during parsing.
/// </summary>
/// <param name="SyntaxTree">The parsed syntax tree.</param>
/// <param name="UpdateKind">The inferred update kind.</param>
/// <param name="MemberUpdate">The ModuleMember update plan when a single member can be updated safely.</param>
public sealed record VbaSyntaxTreeParseResult(
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind UpdateKind,
    VbaModuleMemberIncrementalUpdate? MemberUpdate = null);
