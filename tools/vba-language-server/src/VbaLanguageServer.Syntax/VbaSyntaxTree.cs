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
        var syntaxTree = ParseModule(uri, source);
        var memberUpdate = TryCreateModuleMemberUpdate(previousSyntaxTree, syntaxTree, out var update)
            ? update
            : null;
        var updateKind = memberUpdate is not null || HasUnchangedText(previousSyntaxTree, syntaxTree)
            ? VbaSyntaxTreeParseUpdateKind.ModuleMember
            : VbaSyntaxTreeParseUpdateKind.FullModule;
        return new VbaSyntaxTreeParseResult(syntaxTree, updateKind, memberUpdate);
    }

    private static bool TryCreateModuleMemberUpdate(
        VbaSyntaxTree? previousSyntaxTree,
        VbaSyntaxTree nextSyntaxTree,
        out VbaModuleMemberIncrementalUpdate update)
    {
        update = default!;
        if (previousSyntaxTree is null || nextSyntaxTree.Diagnostics.Count > 0)
        {
            return false;
        }

        if (!previousSyntaxTree.Module.Identity.Name.Equals(nextSyntaxTree.Module.Identity.Name, StringComparison.OrdinalIgnoreCase)
            || previousSyntaxTree.Module.Kind != nextSyntaxTree.Module.Kind
            || previousSyntaxTree.Module.CodeStartLine != nextSyntaxTree.Module.CodeStartLine)
        {
            return false;
        }

        if (!TryFindChangedLineRange(
            SplitLines(previousSyntaxTree.Text),
            SplitLines(nextSyntaxTree.Text),
            out var oldStartLine,
            out var oldEndLine,
            out var newStartLine,
            out var newEndLine))
        {
            return false;
        }

        var oldMember = FindSingleContainingMember(previousSyntaxTree.Module.Members, oldStartLine, oldEndLine);
        var newMember = FindSingleContainingMember(nextSyntaxTree.Module.Members, newStartLine, newEndLine);
        if (oldMember is null
            || newMember is null
            || oldMember.Kind != newMember.Kind
            || !oldMember.Name.Equals(newMember.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TouchesMemberBoundary(oldMember, oldStartLine, oldEndLine)
            || TouchesMemberBoundary(newMember, newStartLine, newEndLine))
        {
            return false;
        }

        update = new VbaModuleMemberIncrementalUpdate(
            oldMember,
            newMember,
            oldStartLine,
            oldEndLine,
            newStartLine,
            newEndLine);
        return true;
    }

    private static bool HasUnchangedText(VbaSyntaxTree? previousSyntaxTree, VbaSyntaxTree nextSyntaxTree)
        => previousSyntaxTree is not null
            && previousSyntaxTree.Text.Equals(nextSyntaxTree.Text, StringComparison.Ordinal);

    private static string[] SplitLines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static bool TryFindChangedLineRange(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        out int oldStartLine,
        out int oldEndLine,
        out int newStartLine,
        out int newEndLine)
    {
        var prefix = 0;
        while (prefix < oldLines.Count
            && prefix < newLines.Count
            && oldLines[prefix].Equals(newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        if (prefix == oldLines.Count && prefix == newLines.Count)
        {
            oldStartLine = oldEndLine = newStartLine = newEndLine = 0;
            return false;
        }

        var oldSuffix = oldLines.Count - 1;
        var newSuffix = newLines.Count - 1;
        while (oldSuffix >= prefix
            && newSuffix >= prefix
            && oldLines[oldSuffix].Equals(newLines[newSuffix], StringComparison.Ordinal))
        {
            oldSuffix--;
            newSuffix--;
        }

        oldStartLine = prefix;
        oldEndLine = Math.Max(prefix, oldSuffix);
        newStartLine = prefix;
        newEndLine = Math.Max(prefix, newSuffix);
        return true;
    }

    private static VbaModuleMemberSyntax? FindSingleContainingMember(
        IReadOnlyList<VbaModuleMemberSyntax> members,
        int startLine,
        int endLine)
    {
        var containingMembers = members
            .Where(member => member.BlockRange.Start.Line <= startLine && member.BlockRange.End.Line >= endLine)
            .ToArray();
        return containingMembers.Length == 1 ? containingMembers[0] : null;
    }

    private static bool TouchesMemberBoundary(VbaModuleMemberSyntax member, int startLine, int endLine)
        => startLine <= member.BlockRange.Start.Line || endLine >= member.BlockRange.End.Line;
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
