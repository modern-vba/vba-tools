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
    private readonly ParserProvenance? parserProvenance;
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
        parserProvenance = new ParserProvenance(
            uri,
            sourceText.Text,
            tokenStream,
            module,
            diagnostics);
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
    /// Parses source text and returns the semantic reuse proof available to a consumer.
    /// </summary>
    /// <param name="uri">The document URI associated with the source text.</param>
    /// <param name="source">The complete source text to parse.</param>
    /// <param name="previousSyntaxTree">The previous syntax tree from the same consumer.</param>
    /// <returns>
    /// A syntax change set that requires module recomputation unless it proves either exact
    /// tree reuse or one safely replaced module member.
    /// </returns>
    /// <remarks>
    /// Reuse is available only when <paramref name="previousSyntaxTree"/> is an unmodified
    /// result produced by this parser. Publicly constructed or modified trees are accepted
    /// as inputs but conservatively require module recomputation.
    /// </remarks>
    public static VbaSyntaxTreeChangeSet ParseOrUpdate(
        string uri,
        string source,
        VbaSyntaxTree? previousSyntaxTree)
        => ParseOrUpdate(uri, source, previousSyntaxTree, out _);

    internal static VbaSyntaxTreeChangeSet ParseOrUpdate(
        string uri,
        string source,
        VbaSyntaxTree? previousSyntaxTree,
        out VbaIncrementalParseObservation observation)
    {
        var hasValidPreviousProvenance =
            previousSyntaxTree?.HasValidParserProvenance() == true;
        observation = VbaIncrementalParseObservation.FullModule(
            source.Length,
            previousSyntaxTree is null
                ? VbaIncrementalParseFallbackReason.NoPreviousTree
                : hasValidPreviousProvenance
                    ? VbaIncrementalParseFallbackReason.DifferentialGuardFailed
                    : VbaIncrementalParseFallbackReason.PreviousTreeProvenanceInvalid);
        if (hasValidPreviousProvenance
            && previousSyntaxTree is not null
            && previousSyntaxTree.Uri.Equals(uri, StringComparison.Ordinal)
            && previousSyntaxTree.Text.Equals(source, StringComparison.Ordinal))
        {
            observation = VbaIncrementalParseObservation.Reuse(
                previousSyntaxTree.Text.Length,
                previousSyntaxTree.Module.Kind,
                previousSyntaxTree.Module.Identity.Name);
            return new VbaSyntaxTreeChangeSet.Unchanged(previousSyntaxTree);
        }

        if (hasValidPreviousProvenance
            && previousSyntaxTree is not null
            && VbaSyntaxTreeIncrementalParser.TryParseModuleMember(
                uri,
                source,
                previousSyntaxTree,
                out var incrementalResult,
                out observation))
        {
            return incrementalResult;
        }

        return new VbaSyntaxTreeChangeSet.Module(ParseModule(uri, source));
    }

    private bool HasValidParserProvenance()
        => parserProvenance?.Matches(this) == true;

    private sealed class ParserProvenance
    {
        private readonly string uri;
        private readonly string text;
        private readonly VbaTokenStream tokenStream;
        private readonly VbaModuleSyntax module;
        private readonly IReadOnlyList<VbaSyntaxDiagnostic> diagnostics;

        public ParserProvenance(
            string uri,
            string text,
            VbaTokenStream tokenStream,
            VbaModuleSyntax module,
            IReadOnlyList<VbaSyntaxDiagnostic> diagnostics)
        {
            this.uri = uri;
            this.text = text;
            this.tokenStream = tokenStream;
            this.module = module;
            this.diagnostics = diagnostics;
        }

        public bool Matches(VbaSyntaxTree syntaxTree)
            => string.Equals(syntaxTree.Uri, uri, StringComparison.Ordinal)
                && string.Equals(syntaxTree.Text, text, StringComparison.Ordinal)
                && ReferenceEquals(syntaxTree.TokenStream, tokenStream)
                && ReferenceEquals(syntaxTree.Module, module)
                && ReferenceEquals(syntaxTree.Diagnostics, diagnostics);
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
    PreviousTreeProvenanceInvalid,
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
