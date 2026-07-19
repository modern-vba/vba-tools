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
    private VbaPositionSyntaxIndex? positionSyntaxIndex;
    private VbaSourceText? sourceText;

    internal VbaSyntaxTree(
        string uri,
        VbaSourceText sourceText,
        VbaTokenStream tokenStream,
        VbaModuleSyntax module,
        IReadOnlyList<VbaSyntaxDiagnostic> diagnostics)
        : this(uri, sourceText.Text, tokenStream, module, diagnostics)
    {
        this.sourceText = sourceText;
    }

    /// <summary>
    /// Gets the source-coordinate model created from the same text as this syntax tree.
    /// </summary>
    public VbaSourceText SourceText
    {
        get
        {
            var indexed = Volatile.Read(ref sourceText);
            if (indexed is not null && ReferenceEquals(indexed.Text, Text))
            {
                return indexed;
            }

            var created = VbaSourceText.From(Text);
            Interlocked.Exchange(ref sourceText, created);
            return created;
        }
    }

    /// <summary>
    /// Parses a VBA module source document.
    /// </summary>
    /// <param name="uri">The document URI associated with the source text.</param>
    /// <param name="source">The complete source text to parse.</param>
    /// <returns>The parsed syntax tree.</returns>
    public static VbaSyntaxTree ParseModule(string uri, string source)
        => VbaSyntaxTreeParser.ParseModule(uri, source);

    /// <summary>
    /// Gets the token-derived syntax facts at an editor position.
    /// </summary>
    public VbaPositionSyntax GetPositionSyntax(int line, int character)
    {
        var index = Volatile.Read(ref positionSyntaxIndex);
        if (index is null)
        {
            var created = new VbaPositionSyntaxIndex(this);
            index = Interlocked.CompareExchange(ref positionSyntaxIndex, created, null) ?? created;
        }

        return index.GetPositionSyntax(line, character);
    }

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
        => ParseOrUpdate(uri, source, previousSyntaxTree, out _);

    internal static VbaSyntaxTreeParseResult ParseOrUpdate(
        string uri,
        string source,
        VbaSyntaxTree? previousSyntaxTree,
        out VbaIncrementalParseObservation observation)
    {
        observation = VbaIncrementalParseObservation.FullModule(
            source.Length,
            previousSyntaxTree is null
                ? VbaIncrementalParseFallbackReason.NoPreviousTree
                : VbaIncrementalParseFallbackReason.DifferentialGuardFailed);
        if (previousSyntaxTree is not null
            && previousSyntaxTree.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)
            && previousSyntaxTree.Text.Equals(source, StringComparison.Ordinal))
        {
            observation = VbaIncrementalParseObservation.Reuse(
                previousSyntaxTree.Text.Length,
                previousSyntaxTree.Module.Kind,
                previousSyntaxTree.Module.Identity.Name);
            return new VbaSyntaxTreeParseResult(
                previousSyntaxTree,
                VbaSyntaxTreeParseUpdateKind.ModuleMember);
        }

        if (previousSyntaxTree is not null
            && VbaSyntaxTreeIncrementalParser.TryParseModuleMember(
                uri,
                source,
                previousSyntaxTree,
                out var incrementalResult,
                out observation))
        {
            return incrementalResult;
        }

        return new VbaSyntaxTreeParseResult(
            ParseModule(uri, source),
            VbaSyntaxTreeParseUpdateKind.FullModule);
    }
}

internal enum VbaIncrementalParseRoute
{
    Reuse,
    ModuleMemberSourceWindow,
    FullModule
}

internal enum VbaIncrementalParseFallbackReason
{
    None,
    NoPreviousTree,
    PreviousDiagnostics,
    UnsupportedModuleKind,
    UriMismatch,
    ChangeLocalityUnproven,
    MemberNotUnique,
    MemberBoundaryTouched,
    WindowOutOfRange,
    CrossMemberPreprocessor,
    SliceParseDiagnostics,
    MemberShapeChanged,
    ModuleIdentityChanged,
    DifferentialGuardFailed
}

internal readonly record struct VbaIncrementalParseObservation(
    VbaIncrementalParseRoute Route,
    VbaIncrementalParseFallbackReason FallbackReason,
    int DocumentUtf16Length,
    int ParseWindowUtf16Length,
    int WindowStartOffset,
    int WindowStartLine,
    int MemberUtf16Length,
    VbaModuleKind ModuleKind,
    string? ModuleIdentity)
{
    public static VbaIncrementalParseObservation Reuse(
        int documentUtf16Length,
        VbaModuleKind moduleKind,
        string? moduleIdentity)
        => new(
            VbaIncrementalParseRoute.Reuse,
            VbaIncrementalParseFallbackReason.None,
            documentUtf16Length,
            0,
            0,
            0,
            0,
            moduleKind,
            moduleIdentity);

    public static VbaIncrementalParseObservation FullModule(
        int documentUtf16Length,
        VbaIncrementalParseFallbackReason fallbackReason)
        => new(
            VbaIncrementalParseRoute.FullModule,
            fallbackReason,
            documentUtf16Length,
            0,
            0,
            0,
            0,
            VbaModuleKind.StandardModule,
            null);
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
