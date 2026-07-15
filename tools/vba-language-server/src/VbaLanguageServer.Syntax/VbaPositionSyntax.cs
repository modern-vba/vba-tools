namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the lexical region that owns an editor position.
/// </summary>
public enum VbaPositionRegion
{
    Outside,
    Code,
    String,
    Comment,
    Preprocessor,
    Designer
}

/// <summary>
/// Identifies the grammatical slot that can accept a completion candidate at an editor position.
/// </summary>
public enum VbaCompletionExpectation
{
    /// <summary>
    /// No completion candidate is grammatically valid at the position.
    /// </summary>
    None,

    /// <summary>
    /// A declaration can begin at module scope.
    /// </summary>
    ModuleDeclaration,

    /// <summary>
    /// A syntax-owned word is expected to continue the active grammar production.
    /// </summary>
    SyntaxWord,

    /// <summary>
    /// Compatibility alias for the former declaration-specific domain name.
    /// </summary>
    DeclarationContinuation = SyntaxWord,

    /// <summary>
    /// A statement can begin inside a procedure body.
    /// </summary>
    ProcedureStatement,

    /// <summary>
    /// A syntax-owned branch or block statement is expected.
    /// </summary>
    ContextualStatement,

    /// <summary>
    /// The name of an existing callable is expected after Call.
    /// </summary>
    CallableName,

    /// <summary>
    /// A readable expression value is expected.
    /// </summary>
    ExpressionValue,

    /// <summary>
    /// A writable assignment target is expected.
    /// </summary>
    AssignmentTarget,

    /// <summary>
    /// A type name is expected.
    /// </summary>
    TypeName,

    /// <summary>
    /// A type that can be created with New is expected.
    /// </summary>
    CreatableType,

    /// <summary>
    /// A class type that can be named by an Implements statement is expected.
    /// </summary>
    ImplementsType,

    /// <summary>
    /// A fresh call argument, including an unused named argument, can begin.
    /// </summary>
    CallArgument,

    /// <summary>
    /// The value of an already selected named argument is expected.
    /// </summary>
    NamedArgumentValue,

    /// <summary>
    /// An event name is expected after RaiseEvent.
    /// </summary>
    EventName,

    /// <summary>
    /// A procedure label is expected by a branch statement.
    /// </summary>
    LabelName
}

/// <summary>
/// Identifies the VBA call form surrounding an editor position.
/// </summary>
public enum VbaCallSyntaxForm
{
    Parenthesized,
    Statement
}

/// <summary>
/// Represents an identifier token at an editor position.
/// </summary>
public sealed record VbaPositionIdentifierSyntax(
    string Name,
    VbaSyntaxRange Range,
    bool IsKeyword);

/// <summary>
/// Represents a token-derived dotted name chain.
/// </summary>
public sealed record VbaMemberAccessSyntax(
    IReadOnlyList<VbaPositionIdentifierSyntax> Segments,
    int TargetSegmentIndex,
    bool IsLeadingDot,
    bool IsIncomplete,
    bool HasTrailingWhitespace,
    VbaSyntaxRange Range)
{
    /// <summary>
    /// Gets the target segment, or null when the chain ends in a dot.
    /// </summary>
    public VbaPositionIdentifierSyntax? Target
        => TargetSegmentIndex >= 0 && TargetSegmentIndex < Segments.Count
            ? Segments[TargetSegmentIndex]
            : null;

    /// <summary>
    /// Gets the receiver segments before the target.
    /// </summary>
    public IReadOnlyList<VbaPositionIdentifierSyntax> ReceiverSegments
        => Segments.Take(Math.Clamp(TargetSegmentIndex, 0, Segments.Count)).ToArray();

    /// <summary>
    /// Gets whether this chain can syntactically identify a call target.
    /// </summary>
    public bool AllowsCallTargetSyntax
        => Target is { } target
            && (!target.IsKeyword
                || IsLeadingDot
                || TargetSegmentIndex > 0
                || VbaLanguageVocabulary.CanBeBareCallTarget(target.Name));
}

/// <summary>
/// Identifies the grammar production that owns a position type reference.
/// </summary>
public enum VbaPositionTypeReferenceContext
{
    Annotation,
    Creation,
    TypeOf,
    Implements
}

/// <summary>
/// Represents an incomplete or complete type reference around an editor position.
/// </summary>
public sealed record VbaPositionTypeReferenceSyntax(
    VbaPositionIdentifierSyntax? Qualifier,
    VbaPositionIdentifierSyntax? Name,
    bool IsNew,
    bool IsIncomplete,
    VbaSyntaxRange Range,
    VbaPositionTypeReferenceContext Context = VbaPositionTypeReferenceContext.Annotation);

/// <summary>
/// Represents one token-derived call argument.
/// </summary>
public sealed record VbaCallArgumentSyntax(
    int Index,
    string? Name,
    bool IsOmitted,
    VbaSyntaxRange Range);

/// <summary>
/// Represents the call site surrounding an editor position.
/// </summary>
public sealed record VbaCallSiteSyntax(
    VbaCallSyntaxForm Form,
    VbaMemberAccessSyntax Callee,
    IReadOnlyList<VbaCallArgumentSyntax> Arguments,
    int ActiveArgumentIndex,
    string? ActiveNamedArgument,
    bool IsIncomplete);

/// <summary>
/// Represents one enclosing With scope. A null receiver preserves an invalid nested scope as a fail-closed barrier.
/// </summary>
public sealed record VbaWithScopeSyntax(VbaMemberAccessSyntax? Receiver);

/// <summary>
/// Contains the syntax facts needed by editor features at one position.
/// </summary>
public sealed record VbaPositionSyntax(
    VbaPositionRegion Region,
    VbaPositionIdentifierSyntax? Identifier,
    VbaCompletionExpectation CompletionExpectation,
    IReadOnlyList<string> ContextualStatements,
    IReadOnlyList<string> SyntaxWords,
    IReadOnlyList<string> SupplementalSyntaxWords,
    IReadOnlyList<string> StarterWords,
    VbaMemberAccessSyntax? MemberAccess,
    VbaPositionTypeReferenceSyntax? TypeReference,
    VbaCallSiteSyntax? CallSite,
    IReadOnlyList<VbaWithScopeSyntax> EnclosingWithScopes,
    IReadOnlyList<VbaEnclosingBlockSyntax> EnclosingBlocks,
    VbaLabelReferenceSyntax? LabelReference,
    VbaSyntaxRange? CompletionReplacementRange)
{
    /// <summary>
    /// Gets syntax-owned words through the former compatibility name.
    /// </summary>
    public IReadOnlyList<string> ContextualWords => SyntaxWords;

    /// <summary>
    /// Gets the result returned for a position outside the source document.
    /// </summary>
    public static VbaPositionSyntax Outside { get; } = new(
        VbaPositionRegion.Outside,
        null,
        VbaCompletionExpectation.None,
        [],
        [],
        [],
        [],
        null,
        null,
        null,
        [],
        [],
        null,
        null);
}

/// <summary>
/// Owns token-derived position queries for one immutable syntax tree.
/// </summary>
internal sealed class VbaPositionSyntaxIndex
{
    private static readonly IReadOnlyList<string> StandardModuleStarterWords = [
        "Const",
        "Declare",
        "Dim",
        "Enum",
        "Function",
        "Global",
        "Option",
        "Private",
        "Public",
        "Static",
        "Sub",
        "Type"
    ];
    private static readonly IReadOnlyList<string> ObjectModuleStarterWords = [
        "Const",
        "Dim",
        "Event",
        "Friend",
        "Function",
        "Option",
        "Private",
        "Property",
        "Public",
        "Static",
        "Sub",
        "WithEvents"
    ];
    private static readonly IReadOnlyList<string> ClassModuleStarterWords = [
        "Const",
        "Dim",
        "Event",
        "Friend",
        "Function",
        "Implements",
        "Option",
        "Private",
        "Property",
        "Public",
        "Static",
        "Sub",
        "WithEvents"
    ];
    private static readonly IReadOnlyList<string> StandardVisibilityDeclarationWords = [
        "Const",
        "Declare",
        "Enum",
        "Function",
        "Static",
        "Sub",
        "Type"
    ];
    private static readonly IReadOnlyList<string> ObjectPublicDeclarationWords = [
        "Event",
        "Function",
        "Property",
        "Static",
        "Sub",
        "WithEvents"
    ];
    private static readonly IReadOnlyList<string> ObjectPrivateDeclarationWords = [
        "Const",
        "Declare",
        "Enum",
        "Function",
        "Property",
        "Static",
        "Sub",
        "Type",
        "WithEvents"
    ];
    private static readonly IReadOnlyList<string> ObjectFriendDeclarationWords = [
        "Function",
        "Property",
        "Static",
        "Sub"
    ];
    private static readonly IReadOnlyList<string> StandardCallableDeclarationWords = [
        "Function",
        "Sub"
    ];
    private static readonly IReadOnlyList<string> ObjectCallableDeclarationWords = [
        "Function",
        "Property",
        "Sub"
    ];
    private static readonly IReadOnlyList<string> PropertyAccessorWords = [
        "Get",
        "Let",
        "Set"
    ];
    private static readonly IReadOnlyList<string> DeclareWords = [
        "Function",
        "PtrSafe",
        "Sub"
    ];
    private static readonly IReadOnlyList<string> PtrSafeDeclareWords = [
        "Function",
        "Sub"
    ];
    private static readonly IReadOnlyList<string> OptionWords = [
        "Base",
        "Compare",
        "Explicit",
        "Private"
    ];
    private static readonly IReadOnlyList<string> OptionBaseWords = [
        "0",
        "1"
    ];
    private static readonly IReadOnlyList<string> OptionCompareWords = [
        "Binary",
        "Text"
    ];
    private static readonly IReadOnlyList<string> OptionPrivateWords = [
        "Module"
    ];
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StandardDeclarationContinuationWords =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Public"] = StandardVisibilityDeclarationWords,
            ["Private"] = StandardVisibilityDeclarationWords,
            ["Static"] = StandardCallableDeclarationWords,
            ["Public Static"] = StandardCallableDeclarationWords,
            ["Private Static"] = StandardCallableDeclarationWords,
            ["Declare"] = DeclareWords,
            ["Public Declare"] = DeclareWords,
            ["Private Declare"] = DeclareWords,
            ["Declare PtrSafe"] = PtrSafeDeclareWords,
            ["Public Declare PtrSafe"] = PtrSafeDeclareWords,
            ["Private Declare PtrSafe"] = PtrSafeDeclareWords,
            ["Option"] = OptionWords,
            ["Option Base"] = OptionBaseWords,
            ["Option Compare"] = OptionCompareWords,
            ["Option Private"] = OptionPrivateWords
        };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ObjectDeclarationContinuationWords =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Public"] = ObjectPublicDeclarationWords,
            ["Private"] = ObjectPrivateDeclarationWords,
            ["Friend"] = ObjectFriendDeclarationWords,
            ["Static"] = ObjectCallableDeclarationWords,
            ["Public Static"] = ObjectCallableDeclarationWords,
            ["Private Static"] = ObjectCallableDeclarationWords,
            ["Friend Static"] = ObjectCallableDeclarationWords,
            ["Property"] = PropertyAccessorWords,
            ["Public Property"] = PropertyAccessorWords,
            ["Private Property"] = PropertyAccessorWords,
            ["Friend Property"] = PropertyAccessorWords,
            ["Private Declare"] = DeclareWords,
            ["Private Declare PtrSafe"] = PtrSafeDeclareWords,
            ["Option"] = OptionWords,
            ["Option Base"] = OptionBaseWords,
            ["Option Compare"] = OptionCompareWords,
            ["Option Private"] = OptionPrivateWords
        };

    private readonly VbaModuleKind moduleKind;
    private readonly VbaSourceText sourceText;
    private readonly IReadOnlyList<VbaToken> tokens;
    private readonly IReadOnlyList<IReadOnlyList<VbaToken>> nonWhitespaceTokensByLine;
    private readonly IReadOnlyList<StatementSpan> statements;
    private readonly IReadOnlyList<WithScopeSpan> withScopes;
    private readonly IReadOnlyList<int> withScopePrefixMaximumEnds;
    private readonly IReadOnlyList<VbaCallableDeclarationSyntax> callableDeclarations;
    private readonly IReadOnlyList<VbaBlockSyntax> blocks;
    private readonly VbaSyntaxRange? designerRange;

    public VbaPositionSyntaxIndex(VbaSyntaxTree tree)
    {
        moduleKind = tree.Module.Kind;
        sourceText = tree.SourceText;
        tokens = tree.TokenStream.Tokens;
        nonWhitespaceTokensByLine = BuildNonWhitespaceTokensByLine(sourceText.Lines.Count, tokens);
        statements = BuildStatements(tree.Text.Length, tokens);
        withScopes = BuildWithScopes(tree.Text.Length, statements)
            .OrderBy(scope => scope.StartOffset)
            .ThenBy(scope => scope.Depth)
            .ToArray();
        withScopePrefixMaximumEnds = BuildPrefixMaximumEnds(withScopes);
        callableDeclarations = tree.Module.CallableDeclarations;
        blocks = tree.Module.Blocks;
        designerRange = tree.Module.FormDesignerBlock?.Range;
    }

    public VbaPositionSyntax GetPositionSyntax(int line, int character)
    {
        if (!TryGetPosition(line, character, out var position))
        {
            return VbaPositionSyntax.Outside;
        }

        var statement = FindStatement(position.Offset);
        var region = GetRegion(position, statement);
        if (region != VbaPositionRegion.Code)
        {
            return new VbaPositionSyntax(
                region,
                null,
                VbaCompletionExpectation.None,
                [],
                [],
                [],
                [],
                null,
                null,
                null,
                [],
                [],
                null,
                null);
        }

        statement = GetActiveCompletionStatement(statement, position);

        var identifier = FindIdentifier(statement, position);
        var memberAccess = TryGetMemberAccess(statement, position);
        var typeReference = TryGetTypeReference(statement, position);
        var callSite = TryGetCallSite(statement, position);
        var enclosingWithScopes = GetEnclosingWithScopes(position.Offset);
        var enclosingBlocks = GetEnclosingBlocks(position.Offset);
        var labelReference = TryGetLabelReference(statement, position);
        var contextualStatements = GetContextualStatements(
            statement,
            position,
            enclosingBlocks);
        var syntaxWords = GetSyntaxWords(statement, position, enclosingBlocks, callSite);
        var supplementalSyntaxWords = GetSupplementalSyntaxWords(statement, position);
        var starterWords = GetStarterWords(statement, position, enclosingBlocks);
        var completionExpectation = GetCompletionExpectation(
            statement,
            position,
            memberAccess,
            typeReference,
            callSite,
            enclosingBlocks,
            labelReference,
            contextualStatements,
            syntaxWords,
            starterWords);
        var completionReplacementRange = GetCompletionReplacementRange(
            statement,
            position,
            identifier,
            labelReference,
            completionExpectation);
        return new VbaPositionSyntax(
            region,
            identifier,
            completionExpectation,
            contextualStatements,
            syntaxWords,
            supplementalSyntaxWords,
            starterWords,
            memberAccess,
            typeReference,
            callSite,
            enclosingWithScopes,
            enclosingBlocks,
            labelReference,
            completionReplacementRange);
    }

    private bool TryGetPosition(int line, int character, out VbaSyntaxPosition position)
    {
        position = default!;
        if (line < 0 || line >= sourceText.Lines.Count)
        {
            return false;
        }

        var sourceLine = sourceText.Lines[line];
        if (character < 0 || character > sourceLine.Text.Length)
        {
            return false;
        }

        position = new VbaSyntaxPosition(line, character, sourceLine.StartOffset + character);
        return true;
    }

    private VbaPositionRegion GetRegion(VbaSyntaxPosition position, StatementSpan statement)
    {
        if (designerRange is not null
            && position.Offset >= designerRange.Start.Offset
            && position.Offset < designerRange.End.Offset)
        {
            return VbaPositionRegion.Designer;
        }

        var tokenIndex = FindTokenIndex(position.Offset);
        var token = tokenIndex >= 0 ? tokens[tokenIndex] : null;
        if (token is not null
            && token.Kind == VbaTokenKind.NewLine
            && tokenIndex > 0
            && tokens[tokenIndex - 1].Range.End.Offset == position.Offset
            && tokens[tokenIndex - 1].Kind is VbaTokenKind.Comment or VbaTokenKind.PreprocessorDirective)
        {
            token = tokens[tokenIndex - 1];
        }

        if (token is not null
            && position.Line == token.Range.Start.Line
            && position.Offset <= token.Range.End.Offset)
        {
            if (token.Kind == VbaTokenKind.StringLiteral)
            {
                return VbaPositionRegion.String;
            }

            if (token.Kind == VbaTokenKind.Comment)
            {
                return VbaPositionRegion.Comment;
            }

            if (token.Kind == VbaTokenKind.PreprocessorDirective)
            {
                return VbaPositionRegion.Preprocessor;
            }
        }

        var first = statement.SignificantTokens.FirstOrDefault();
        if (first is not null
            && first.Text.Equals("Rem", StringComparison.OrdinalIgnoreCase)
            && position.Offset >= first.Range.Start.Offset)
        {
            return VbaPositionRegion.Comment;
        }

        return VbaPositionRegion.Code;
    }

    private VbaPositionIdentifierSyntax? FindIdentifier(
        StatementSpan statement,
        VbaSyntaxPosition position)
        => statement.SignificantTokens
            .Where(IsNameToken)
            .Where(token => token.Range.Start.Line == position.Line)
            .Where(token => token.Range.Start.Character <= position.Character)
            .Where(token => position.Character <= token.Range.End.Character)
            .OrderByDescending(token => token.Range.Start.Offset)
            .Select(ToIdentifier)
            .FirstOrDefault();

    private VbaMemberAccessSyntax? TryGetMemberAccess(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var nameIndex = -1;
        for (var index = 0; index < significant.Count; index++)
        {
            var token = significant[index];
            if (IsNameToken(token)
                && token.Range.Start.Line == position.Line
                && token.Range.Start.Character <= position.Character
                && position.Character <= token.Range.End.Character)
            {
                nameIndex = index;
            }
        }

        if (nameIndex < 0)
        {
            var previousIndex = -1;
            for (var index = 0; index < significant.Count; index++)
            {
                if (significant[index].Range.End.Offset <= position.Offset)
                {
                    previousIndex = index;
                }
            }

            if (previousIndex >= 0 && IsNameToken(significant[previousIndex]))
            {
                nameIndex = previousIndex;
            }
        }

        if (nameIndex >= 0)
        {
            var start = nameIndex;
            while (start >= 2 && IsDot(significant[start - 1]) && IsNameToken(significant[start - 2]))
            {
                start -= 2;
            }

            if (start > 0 && IsDot(significant[start - 1]))
            {
                start--;
            }

            var end = nameIndex;
            while (end + 2 < significant.Count && IsDot(significant[end + 1]) && IsNameToken(significant[end + 2]))
            {
                end += 2;
            }

            var hasTrailingDot = end + 1 < significant.Count && IsDot(significant[end + 1]);
            if (hasTrailingDot)
            {
                end++;
            }

            if (!significant.Skip(start).Take(end - start + 1).Any(IsDot))
            {
                return null;
            }

            var segments = significant
                .Skip(start)
                .Take(end - start + 1)
                .Where(IsNameToken)
                .Select(ToIdentifier)
                .ToArray();
            var targetIndex = significant
                .Skip(start)
                .Take(nameIndex - start)
                .Count(IsNameToken);
            return new VbaMemberAccessSyntax(
                segments,
                targetIndex,
                IsDot(significant[start]),
                hasTrailingDot,
                position.Offset > significant[nameIndex].Range.End.Offset,
                new VbaSyntaxRange(significant[start].Range.Start, significant[end].Range.End));
        }

        var anchorIndex = -1;
        for (var index = 0; index < significant.Count; index++)
        {
            if (significant[index].Range.End.Offset <= position.Offset)
            {
                anchorIndex = index;
            }
        }

        if (anchorIndex < 0 || !IsDot(significant[anchorIndex]))
        {
            return null;
        }

        var chainStart = anchorIndex;
        if (anchorIndex > 0 && IsNameToken(significant[anchorIndex - 1]))
        {
            chainStart = anchorIndex - 1;
            while (chainStart >= 2
                && IsDot(significant[chainStart - 1])
                && IsNameToken(significant[chainStart - 2]))
            {
                chainStart -= 2;
            }

            if (chainStart > 0 && IsDot(significant[chainStart - 1]))
            {
                chainStart--;
            }
        }

        var chainSegments = significant
            .Skip(chainStart)
            .Take(anchorIndex - chainStart + 1)
            .Where(IsNameToken)
            .Select(ToIdentifier)
            .ToArray();
        return new VbaMemberAccessSyntax(
            chainSegments,
            chainSegments.Length,
            IsDot(significant[chainStart]),
            true,
            position.Offset > significant[anchorIndex].Range.End.Offset,
            new VbaSyntaxRange(significant[chainStart].Range.Start, significant[anchorIndex].Range.End));
    }

    private VbaPositionTypeReferenceSyntax? TryGetTypeReference(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var markerIndex = -1;
        var context = VbaPositionTypeReferenceContext.Annotation;
        var typeOfIndex = -1;
        var hasTypeOfTransition = false;
        for (var index = 0; index < significant.Count; index++)
        {
            var token = significant[index];
            if (token.Range.Start.Offset > position.Offset)
            {
                break;
            }

            if (IsWord(token, "TypeOf"))
            {
                typeOfIndex = index;
                hasTypeOfTransition = false;
            }

            if (index == 0
                && moduleKind == VbaModuleKind.ClassModule
                && IsWord(token, "Implements"))
            {
                markerIndex = index;
                context = VbaPositionTypeReferenceContext.Implements;
            }
            else if (IsKeyword(token, "As"))
            {
                markerIndex = index;
                context = VbaPositionTypeReferenceContext.Annotation;
            }
            else if (IsKeyword(token, "New"))
            {
                markerIndex = index;
                context = VbaPositionTypeReferenceContext.Creation;
            }
            else if (typeOfIndex >= 0
                && !hasTypeOfTransition
                && IsWord(token, "Is"))
            {
                markerIndex = index;
                context = VbaPositionTypeReferenceContext.TypeOf;
                hasTypeOfTransition = true;
            }
        }

        if (markerIndex < 0)
        {
            return null;
        }

        var marker = significant[markerIndex];
        var isNew = IsKeyword(marker, "New");
        var valueStart = markerIndex + 1;
        if (!isNew
            && valueStart < significant.Count
            && IsKeyword(significant[valueStart], "New"))
        {
            isNew = true;
            context = VbaPositionTypeReferenceContext.Creation;
            valueStart++;
        }

        VbaPositionIdentifierSyntax? qualifier = null;
        VbaPositionIdentifierSyntax? name = null;
        var consumedEnd = valueStart - 1;
        if (valueStart < significant.Count && IsNameToken(significant[valueStart]))
        {
            consumedEnd = valueStart;
            if (valueStart + 1 < significant.Count && IsDot(significant[valueStart + 1]))
            {
                qualifier = ToIdentifier(significant[valueStart]);
                consumedEnd = valueStart + 1;
                if (valueStart + 2 < significant.Count && IsNameToken(significant[valueStart + 2]))
                {
                    name = ToIdentifier(significant[valueStart + 2]);
                    consumedEnd = valueStart + 2;
                }
            }
            else
            {
                name = ToIdentifier(significant[valueStart]);
            }
        }

        if (significant
            .Skip(consumedEnd + 1)
            .Any(token => token.Range.Start.Offset < position.Offset))
        {
            return null;
        }

        var end = consumedEnd >= valueStart
            ? significant[consumedEnd].Range.End
            : marker.Range.End;
        return new VbaPositionTypeReferenceSyntax(
            qualifier,
            name,
            isNew,
            name is null,
            new VbaSyntaxRange(marker.Range.Start, end),
            context);
    }

    private VbaCallSiteSyntax? TryGetCallSite(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        var parsed = VbaCallSyntaxParser.TryParsePositionCall(sourceText, significant, position);
        if (parsed is null)
        {
            return null;
        }

        var callee = CreateChain(
            significant,
            parsed.CalleeStartIndex,
            parsed.CalleeEndIndex,
            parsed.CalleeEndIndex);
        if (callee is null)
        {
            return null;
        }

        return new VbaCallSiteSyntax(
            parsed.Form,
            callee,
            parsed.Arguments,
            parsed.ActiveArgumentIndex,
            parsed.ActiveNamedArgument,
            parsed.IsIncomplete);
    }

    private VbaCompletionExpectation GetCompletionExpectation(
        StatementSpan statement,
        VbaSyntaxPosition position,
        VbaMemberAccessSyntax? memberAccess,
        VbaPositionTypeReferenceSyntax? typeReference,
        VbaCallSiteSyntax? callSite,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks,
        VbaLabelReferenceSyntax? labelReference,
        IReadOnlyList<string> contextualStatements,
        IReadOnlyList<string> syntaxWords,
        IReadOnlyList<string> starterWords)
    {
        if (IsIncompleteStatementNamedArgumentColon(position))
        {
            return VbaCompletionExpectation.None;
        }

        if (IsAfterLineContinuation(position))
        {
            return VbaCompletionExpectation.None;
        }

        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();

        if (enclosingBlocks.Any(block => block.Block.IsMalformedBarrier)
            && !IsRecoverableForTargetPrefix(prefix, position))
        {
            return VbaCompletionExpectation.None;
        }

        if (IsCompletedGrammarMarkerAwaitingSeparator(
                statement,
                prefix,
                position,
                contextualStatements))
        {
            return VbaCompletionExpectation.None;
        }

        if (IsRecoverableForTargetPrefix(prefix, position))
        {
            return VbaCompletionExpectation.AssignmentTarget;
        }

        if (syntaxWords.Count == 0
            && HasUnsupportedSpecialOperandContext(prefix, position, callSite))
        {
            return VbaCompletionExpectation.None;
        }

        if (typeReference is not null)
        {
            if (typeReference.Name is not null
                && position.Offset > typeReference.Range.End.Offset)
            {
                if (typeReference.Context == VbaPositionTypeReferenceContext.TypeOf
                    && syntaxWords.Count > 0)
                {
                    return VbaCompletionExpectation.SyntaxWord;
                }

                return VbaCompletionExpectation.None;
            }

            if (typeReference.Context == VbaPositionTypeReferenceContext.Implements)
            {
                return VbaCompletionExpectation.ImplementsType;
            }

            return typeReference.IsNew
                ? VbaCompletionExpectation.CreatableType
                : VbaCompletionExpectation.TypeName;
        }

        if (syntaxWords.Count > 0)
        {
            return VbaCompletionExpectation.SyntaxWord;
        }

        var defaultExpectation = GetDefaultExpectation(statement, position);
        if (defaultExpectation == VbaCompletionExpectation.ModuleDeclaration
            && prefix.Length == 1
            && IsNameToken(prefix[0])
            && !HasTrailingWhitespace(position, prefix[0])
            && contextualStatements.Count == 0)
        {
            return starterWords.Count > 0
                ? VbaCompletionExpectation.ModuleDeclaration
                : VbaCompletionExpectation.None;
        }

        var continuationExpectation = GetStatementContinuationExpectation(
            statement,
            prefix,
            position,
            memberAccess);
        if (continuationExpectation is not null)
        {
            return continuationExpectation.Value;
        }

        if (memberAccess?.HasTrailingWhitespace == true)
        {
            return VbaCompletionExpectation.None;
        }

        var eventExpectation = GetEventNameExpectation(prefix, position);
        if (eventExpectation is not null)
        {
            return eventExpectation.Value;
        }

        if (labelReference is not null)
        {
            return labelReference.IsIncomplete
                ? VbaCompletionExpectation.LabelName
                : VbaCompletionExpectation.None;
        }

        if (IsContextualStatementPosition(
                statement,
                position,
                enclosingBlocks,
                contextualStatements))
        {
            return VbaCompletionExpectation.ContextualStatement;
        }

        var caseExpressionExpectation = GetCaseExpressionExpectation(prefix, position);
        if (caseExpressionExpectation is not null)
        {
            return caseExpressionExpectation.Value;
        }

        var onSelectorExpectation = GetOnSelectorExpressionExpectation(prefix, position);
        if (onSelectorExpectation is not null)
        {
            return onSelectorExpectation.Value;
        }

        if (memberAccess is not null)
        {
            return GetMemberCompletionExpectation(
                statement,
                position,
                memberAccess,
                callSite);
        }

        if (callSite is not null)
        {
            var conditionExpectation = GetConditionExpressionExpectation(
                prefix,
                position,
                callSite);
            if (conditionExpectation is not null)
            {
                return conditionExpectation.Value;
            }

            if (FindAssignmentOperator(statement.SignificantTokens) is null
                && IsExplicitAssignmentTarget(prefix, position))
            {
                return VbaCompletionExpectation.AssignmentTarget;
            }

            if (GetDefaultExpectation(statement, position) == VbaCompletionExpectation.ModuleDeclaration)
            {
                return VbaCompletionExpectation.None;
            }

            if (!callSite.Callee.AllowsCallTargetSyntax)
            {
                return VbaCompletionExpectation.None;
            }

            return GetCallArgumentExpectation(prefix, position, callSite);
        }

        if (prefix.Length == 0)
        {
            return defaultExpectation;
        }

        var assignmentOperator = FindAssignmentOperator(statement.SignificantTokens);
        if (assignmentOperator is not null)
        {
            if (position.Offset <= assignmentOperator.Range.Start.Offset)
            {
                return VbaCompletionExpectation.AssignmentTarget;
            }

            return ClassifyExpressionTail(
                prefix.Where(token => token.Range.Start.Offset >= assignmentOperator.Range.End.Offset).ToArray(),
                position,
                VbaCompletionExpectation.ExpressionValue);
        }

        if (prefix[^1].Kind == VbaTokenKind.Operator)
        {
            return prefix[^1].Text == ":="
                ? VbaCompletionExpectation.None
                : VbaCompletionExpectation.ExpressionValue;
        }

        if (IsKeywordOperator(prefix[^1]))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        if (prefix[^1].Text.Equals("Then", StringComparison.OrdinalIgnoreCase)
            && HasTrailingWhitespace(position, prefix[^1]))
        {
            return VbaCompletionExpectation.ProcedureStatement;
        }

        if (IsExplicitAssignmentTarget(prefix, position))
        {
            return VbaCompletionExpectation.AssignmentTarget;
        }

        var expressionStart = FindExpressionStart(prefix);
        if (expressionStart >= 0)
        {
            return ClassifyExpressionTail(
                prefix.Skip(expressionStart).ToArray(),
                position,
                VbaCompletionExpectation.ExpressionValue);
        }

        if (prefix[^1].Kind == VbaTokenKind.Punctuation
            && prefix[^1].Text == ")")
        {
            return VbaCompletionExpectation.None;
        }

        return IsRecognizedCompletionStarter(
                prefix,
                position,
                defaultExpectation,
                enclosingBlocks,
                starterWords)
            ? defaultExpectation
            : VbaCompletionExpectation.None;
    }

    private bool IsRecoverableForTargetPrefix(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
        => prefix.Count == 2
            && IsWord(prefix[0], "For")
            && IsNameToken(prefix[1])
            && !HasTrailingWhitespace(position, prefix[1]);

    private bool IsIncompleteStatementNamedArgumentColon(VbaSyntaxPosition position)
    {
        if (position.Offset == 0
            || position.Character == 0
            || sourceText.Text[position.Offset - 1] != ':')
        {
            return false;
        }

        var colonOffset = position.Offset - 1;
        var precedingStatement = statements.LastOrDefault(statement =>
            statement.EndOffset == colonOffset
            && statement.NextOffset == position.Offset);
        if (precedingStatement is null
            || precedingStatement.SignificantTokens.Count == 0)
        {
            return false;
        }

        var lastToken = precedingStatement.SignificantTokens[^1];
        if (!IsNameToken(lastToken) || lastToken.Range.End.Offset != colonOffset)
        {
            return false;
        }

        var colonPosition = new VbaSyntaxPosition(
            position.Line,
            position.Character - 1,
            colonOffset);
        var call = VbaCallSyntaxParser.TryParsePositionCall(
            sourceText,
            precedingStatement.SignificantTokens,
            colonPosition);
        return call?.Form == VbaCallSyntaxForm.Statement
            && call.ActiveArgumentIndex >= 0;
    }

    private VbaCompletionExpectation GetMemberCompletionExpectation(
        StatementSpan statement,
        VbaSyntaxPosition position,
        VbaMemberAccessSyntax memberAccess,
        VbaCallSiteSyntax? callSite)
    {
        var assignmentOperator = FindAssignmentOperator(statement.SignificantTokens);
        if (assignmentOperator is not null)
        {
            return memberAccess.Range.Start.Offset < assignmentOperator.Range.Start.Offset
                ? VbaCompletionExpectation.AssignmentTarget
                : VbaCompletionExpectation.ExpressionValue;
        }

        var prefixBeforeMember = statement.SignificantTokens
            .Where(token => token.Range.End.Offset <= memberAccess.Range.Start.Offset)
            .ToArray();
        if (IsExplicitAssignmentTarget(prefixBeforeMember, position))
        {
            return VbaCompletionExpectation.AssignmentTarget;
        }

        if (callSite is not null
            && memberAccess.Range.Start.Offset >= callSite.Callee.Range.End.Offset)
        {
            var argumentTokens = statement.SignificantTokens
                .Where(token => token.Range.Start.Offset >= callSite.Callee.Range.End.Offset)
                .Where(token => token.Range.End.Offset <= memberAccess.Range.Start.Offset)
                .ToArray();
            return argumentTokens.Any(token =>
                token.Kind == VbaTokenKind.Operator && token.Text == ":=")
                    ? VbaCompletionExpectation.NamedArgumentValue
                    : VbaCompletionExpectation.ExpressionValue;
        }

        var expressionStart = FindExpressionStart(prefixBeforeMember);
        return expressionStart >= 0
            ? VbaCompletionExpectation.ExpressionValue
            : GetDefaultExpectation(statement, position);
    }

    private VbaCompletionExpectation GetCallArgumentExpectation(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCallSiteSyntax callSite)
    {
        if (callSite.ActiveArgumentIndex < 0
            || callSite.ActiveArgumentIndex >= callSite.Arguments.Count)
        {
            return VbaCompletionExpectation.None;
        }

        var activeArgument = callSite.Arguments[callSite.ActiveArgumentIndex];
        var argumentPrefix = prefix
            .Where(token => token.Range.Start.Offset >= activeArgument.Range.Start.Offset)
            .ToArray();
        if (argumentPrefix.Length == 0)
        {
            return VbaCompletionExpectation.CallArgument;
        }

        var namedOperatorIndex = FindToken(argumentPrefix, token =>
            token.Kind == VbaTokenKind.Operator && token.Text == ":=");
        if (namedOperatorIndex >= 0)
        {
            return ClassifyExpressionTail(
                argumentPrefix.Skip(namedOperatorIndex + 1).ToArray(),
                position,
                VbaCompletionExpectation.NamedArgumentValue);
        }

        if (callSite.ActiveNamedArgument is not null)
        {
            return VbaCompletionExpectation.CallArgument;
        }

        if (argumentPrefix.Length == 1
            && IsNameToken(argumentPrefix[0])
            && !HasTrailingWhitespace(position, argumentPrefix[0]))
        {
            return VbaCompletionExpectation.CallArgument;
        }

        return ClassifyExpressionTail(
            argumentPrefix,
            position,
            VbaCompletionExpectation.ExpressionValue);
    }

    private VbaCompletionExpectation GetDefaultExpectation(
        StatementSpan statement,
        VbaSyntaxPosition position)
        => callableDeclarations.Any(declaration =>
                !declaration.IsExternal
                && position.Offset >= declaration.BlockRange.Start.Offset
                && position.Offset <= declaration.BlockRange.End.Offset
                && (position.Line > declaration.LineIndex
                    || statement.StartOffset > declaration.Range.End.Offset))
            ? VbaCompletionExpectation.ProcedureStatement
            : VbaCompletionExpectation.ModuleDeclaration;

    private IReadOnlyList<string> GetStarterWords(
        StatementSpan statement,
        VbaSyntaxPosition position,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks)
    {
        if (GetDefaultExpectation(statement, position)
                != VbaCompletionExpectation.ModuleDeclaration
            || enclosingBlocks.Count > 0)
        {
            return [];
        }

        IReadOnlyList<string> words = GetModuleStarterWords();
        if (callableDeclarations.Any(declaration =>
            !declaration.IsExternal
            && declaration.Range.Start.Offset < position.Offset))
        {
            words = words
                .Where(word => !word.Equals("Option", StringComparison.OrdinalIgnoreCase)
                    && !word.Equals("Implements", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var prefix = GetStatementPrefix(statement, position);
        if (prefix is null)
        {
            return words;
        }

        if (prefix.Value.HasTrailingWhitespace || prefix.Value.Text.Contains(' '))
        {
            return [];
        }

        return words
            .Where(word => word.StartsWith(
                prefix.Value.Text,
                StringComparison.OrdinalIgnoreCase))
            .Where(word => !word.Equals(
                prefix.Value.Text,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private IReadOnlyList<string> GetSyntaxWords(
        StatementSpan statement,
        VbaSyntaxPosition position,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks,
        VbaCallSiteSyntax? callSite)
    {
        if (GetDefaultExpectation(statement, position)
            == VbaCompletionExpectation.ProcedureStatement)
        {
            return GetProcedureSyntaxWords(statement, position, enclosingBlocks, callSite);
        }

        var prefix = GetStatementPrefix(statement, position);
        if (prefix is null)
        {
            return [];
        }

        var continuationWords = moduleKind == VbaModuleKind.StandardModule
            ? StandardDeclarationContinuationWords
            : ObjectDeclarationContinuationWords;

        if (prefix.Value.HasTrailingWhitespace)
        {
            return continuationWords.TryGetValue(
                prefix.Value.Text,
                out var exactWords)
                    ? exactWords
                    : [];
        }

        foreach (var continuation in continuationWords
            .OrderByDescending(entry => entry.Key.Length))
        {
            var statePrefix = continuation.Key + " ";
            if (!prefix.Value.Text.StartsWith(
                    statePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var partialWord = prefix.Value.Text[statePrefix.Length..];
            if (partialWord.Length == 0 || partialWord.Contains(' '))
            {
                continue;
            }

            return continuation.Value
                .Where(word => word.StartsWith(
                    partialWord,
                    StringComparison.OrdinalIgnoreCase))
                .Where(word => !word.Equals(
                    partialWord,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return [];
    }

    private IReadOnlyList<string> GetProcedureSyntaxWords(
        StatementSpan statement,
        VbaSyntaxPosition position,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks,
        VbaCallSiteSyntax? callSite)
    {
        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();
        if (prefix.Length == 0)
        {
            return [];
        }

        var debugWords = GetDebugSyntaxWords(prefix, position);
        if (debugWords.Count > 0)
        {
            return debugWords;
        }

        if (IsWord(prefix[0], "Do") || IsWord(prefix[0], "Loop"))
        {
            return GetFollowingSyntaxWords(prefix, position, 1, ["While", "Until"]);
        }

        var typeOfWords = GetTypeOfSyntaxWords(prefix, position, callSite);
        if (typeOfWords.Count > 0)
        {
            return typeOfWords;
        }

        if (IsWord(prefix[0], "Select"))
        {
            return GetFollowingSyntaxWords(prefix, position, 1, ["Case"]);
        }

        var forWords = GetForSyntaxWords(prefix, position);
        if (forWords.Count > 0)
        {
            return forWords;
        }

        if (prefix.Length >= 3
            && IsWord(prefix[0], "For")
            && IsWord(prefix[1], "Each")
            && IsNameToken(prefix[2]))
        {
            return GetFollowingSyntaxWords(prefix, position, 3, ["In"]);
        }

        if ((IsWord(prefix[0], "If") || IsWord(prefix[0], "ElseIf"))
            && !prefix.Any(token => IsWord(token, "Then")))
        {
            if (prefix.Length >= 3
                && IsNameToken(prefix[^1])
                && !IsPunctuation(prefix[^2], ".")
                && !HasTrailingWhitespace(position, prefix[^1])
                && "Then".StartsWith(prefix[^1].Text, StringComparison.OrdinalIgnoreCase))
            {
                return ["Then"];
            }

            if (prefix.Length >= 2
                && HasTrailingWhitespace(position, prefix[^1])
                && IsCompletedExpressionToken(prefix[^1]))
            {
                return ["Then"];
            }
        }

        if (prefix.Length >= 2
            && IsWord(prefix[0], "On")
            && IsWord(prefix[1], "Error"))
        {
            return GetFollowingSyntaxWords(prefix, position, 2, ["GoTo", "Resume"]);
        }

        if (IsWord(prefix[0], "On")
            && (prefix.Length < 2 || !IsWord(prefix[1], "Error")))
        {
            var dispatchWords = GetOnDispatchSyntaxWords(prefix, position);
            if (dispatchWords.Count > 0)
            {
                return dispatchWords;
            }
        }

        if (IsWord(prefix[0], "Exit"))
        {
            return GetFollowingSyntaxWords(
                prefix,
                position,
                1,
                GetExitSyntaxWords(enclosingBlocks));
        }

        return [];
    }

    private IReadOnlyList<string> GetDebugSyntaxWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count < 2
            || !IsWord(prefix[0], "Debug")
            || !IsPunctuation(prefix[1], "."))
        {
            return [];
        }

        IReadOnlyList<string> words = ["Assert", "Print"];
        if (prefix.Count == 2)
        {
            return words;
        }

        if (prefix.Count != 3
            || !IsNameToken(prefix[2])
            || HasTrailingWhitespace(position, prefix[2]))
        {
            return [];
        }

        return words
            .Where(word => word.StartsWith(prefix[2].Text, StringComparison.OrdinalIgnoreCase))
            .Where(word => !word.Equals(prefix[2].Text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private IReadOnlyList<string> GetForSyntaxWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count < 3
            || !IsWord(prefix[0], "For")
            || IsWord(prefix[1], "Each"))
        {
            return [];
        }

        var depth = 0;
        var assignmentIndex = -1;
        var toIndex = -1;
        var stepIndex = -1;
        for (var index = 1; index < prefix.Count; index++)
        {
            var token = prefix[index];
            if (IsPunctuation(token, "("))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(token, ")"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0 || (index > 0 && IsPunctuation(prefix[index - 1], ".")))
            {
                continue;
            }

            if (assignmentIndex < 0 && token.Kind == VbaTokenKind.Operator && token.Text == "=")
            {
                assignmentIndex = index;
            }
            else if (assignmentIndex >= 0 && toIndex < 0 && IsWord(token, "To"))
            {
                toIndex = index;
            }
            else if (toIndex >= 0 && IsWord(token, "Step"))
            {
                stepIndex = index;
            }
        }

        if (assignmentIndex < 0 || stepIndex >= 0)
        {
            return [];
        }

        var expressionStart = (toIndex >= 0 ? toIndex : assignmentIndex) + 1;
        var expressionTokens = prefix.Skip(expressionStart).ToArray();
        var word = toIndex >= 0 ? "Step" : "To";
        return GetExpressionTransitionWord(expressionTokens, position, word);
    }

    private IReadOnlyList<string> GetExpressionTransitionWord(
        IReadOnlyList<VbaToken> expressionTokens,
        VbaSyntaxPosition position,
        string word)
    {
        if (expressionTokens.Count == 0)
        {
            return [];
        }

        var last = expressionTokens[^1];
        if (HasTrailingWhitespace(position, last)
            && IsCompletedExpressionToken(last)
            && !HasAdjacentExpressionValues(expressionTokens))
        {
            return [word];
        }

        if (expressionTokens.Count < 2
            || !IsNameToken(last)
            || HasTrailingWhitespace(position, last)
            || IsPunctuation(expressionTokens[^2], ".")
            || !IsCompletedExpressionToken(expressionTokens[^2])
            || !word.StartsWith(last.Text, StringComparison.OrdinalIgnoreCase)
            || word.Equals(last.Text, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [word];
    }

    private static bool HasAdjacentExpressionValues(IReadOnlyList<VbaToken> tokens)
    {
        if (tokens.Count < 2 || !IsExpressionValueAtom(tokens[^1]))
        {
            return false;
        }

        return IsCompletedExpressionToken(tokens[^2]);
    }

    private static bool IsExpressionValueAtom(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier
                or VbaTokenKind.StringLiteral
                or VbaTokenKind.DateLiteral
                or VbaTokenKind.NumericLiteral
            || (token.Kind == VbaTokenKind.Keyword
                && !IsKeywordOperator(token)
                && !IsWord(token, "New"));

    private IReadOnlyList<string> GetTypeOfSyntaxWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCallSiteSyntax? callSite)
    {
        var scopedPrefix = GetActiveCompletionScopePrefix(prefix, position, callSite);
        var typeOfIndex = FindLastToken(
            scopedPrefix,
            token => IsWord(token, "TypeOf"));
        if (typeOfIndex < 0
            || scopedPrefix.Skip(typeOfIndex + 1).Any(token => IsWord(token, "Is")))
        {
            return [];
        }

        var operandTokens = scopedPrefix.Skip(typeOfIndex + 1).ToArray();
        if (operandTokens.Length == 0)
        {
            return [];
        }

        var last = operandTokens[^1];
        if (HasTrailingWhitespace(position, last) && IsCompletedExpressionToken(last))
        {
            return ["Is"];
        }

        if (operandTokens.Length < 2
            || !IsNameToken(last)
            || HasTrailingWhitespace(position, last)
            || IsPunctuation(operandTokens[^2], ".")
            || operandTokens[^2].Kind == VbaTokenKind.Operator
            || !"Is".StartsWith(last.Text, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ["Is"];
    }

    private IReadOnlyList<string> GetSupplementalSyntaxWords(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();
        var typeAnnotationWords = GetTypeAnnotationAlternativeWords(prefix, position);
        if (typeAnnotationWords.Count > 0)
        {
            return typeAnnotationWords;
        }

        if (GetDefaultExpectation(statement, position)
            != VbaCompletionExpectation.ProcedureStatement)
        {
            return [];
        }

        if (prefix.Length == 0)
        {
            return [];
        }

        if (IsWord(prefix[0], "Case"))
        {
            return GetAlternativeWord(prefix, position, "Else");
        }

        if (IsWord(prefix[0], "For"))
        {
            return GetAlternativeWord(prefix, position, "Each");
        }

        return IsWord(prefix[0], "On")
            ? GetAlternativeWord(prefix, position, "Error")
            : [];
    }

    private IReadOnlyList<string> GetTypeAnnotationAlternativeWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        var asIndex = FindLastToken(prefix, token => IsWord(token, "As"));
        if (asIndex < 1
            || !IsVariableDeclarationTypeAnnotation(prefix, asIndex))
        {
            return [];
        }

        var typeTokens = prefix.Skip(asIndex + 1).ToArray();
        if (typeTokens.Length == 0)
        {
            return HasTrailingWhitespace(position, prefix[asIndex])
                ? ["New"]
                : [];
        }

        if (typeTokens.Length != 1
            || !IsNameToken(typeTokens[0])
            || HasTrailingWhitespace(position, typeTokens[0])
            || IsWord(typeTokens[0], "New")
            || !"New".StartsWith(typeTokens[0].Text, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ["New"];
    }

    private static bool IsVariableDeclarationTypeAnnotation(
        IReadOnlyList<VbaToken> prefix,
        int asIndex)
    {
        if (asIndex < 2
            || !IsNameToken(prefix[asIndex - 1])
            || !new[] { "Dim", "Static", "Public", "Private", "Global" }
                .Any(word => IsWord(prefix[0], word)))
        {
            return false;
        }

        return !prefix
            .Take(asIndex)
            .Any(token => new[]
            {
                "Const", "Declare", "Event", "Function", "Property", "Sub", "WithEvents"
            }.Any(word => IsWord(token, word)));
    }

    private IReadOnlyList<string> GetAlternativeWord(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        string word)
    {
        if (prefix.Count == 1 && HasTrailingWhitespace(position, prefix[0]))
        {
            return [word];
        }

        if (prefix.Count != 2
            || !IsNameToken(prefix[1])
            || HasTrailingWhitespace(position, prefix[1])
            || IsWord(prefix[1], word)
            || !word.StartsWith(prefix[1].Text, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return [word];
    }

    private IReadOnlyList<string> GetOnDispatchSyntaxWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count < 2
            || prefix.Any(token => IsWord(token, "GoTo") || IsWord(token, "GoSub")))
        {
            return [];
        }

        var last = prefix[^1];
        if (HasTrailingWhitespace(position, last) && IsCompletedExpressionToken(last))
        {
            return ["GoTo", "GoSub"];
        }

        if (prefix.Count < 3
            || !IsNameToken(last)
            || HasTrailingWhitespace(position, last)
            || prefix[^2].Kind == VbaTokenKind.Operator
            || IsPunctuation(prefix[^2], "."))
        {
            return [];
        }

        return new[] { "GoTo", "GoSub" }
            .Where(word => word.StartsWith(last.Text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<string> GetExitSyntaxWords(
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks)
    {
        var words = new List<string>();
        foreach (var enclosing in enclosingBlocks.Reverse())
        {
            var word = enclosing.Block.Kind switch
            {
                VbaBlockKind.For => "For",
                VbaBlockKind.Do => "Do",
                VbaBlockKind.Procedure => GetProcedureExitWord(enclosing.Block.ExpectedTerminator),
                _ => null
            };
            if (word is not null
                && !words.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                words.Add(word);
            }
        }

        return words;
    }

    private static string? GetProcedureExitWord(string terminator)
        => terminator.Equals("End Sub", StringComparison.OrdinalIgnoreCase)
            ? "Sub"
            : terminator.Equals("End Function", StringComparison.OrdinalIgnoreCase)
                ? "Function"
                : terminator.Equals("End Property", StringComparison.OrdinalIgnoreCase)
                    ? "Property"
                    : null;

    private static bool IsCompletedExpressionToken(VbaToken token)
        => !IsWord(token, "TypeOf")
            && !IsWord(token, "AddressOf")
            && (token.Kind is VbaTokenKind.Identifier
                    or VbaTokenKind.StringLiteral
                    or VbaTokenKind.DateLiteral
                    or VbaTokenKind.NumericLiteral
                || (token.Kind == VbaTokenKind.Keyword
                    && !IsKeywordOperator(token)
                    && !IsWord(token, "New"))
                || IsPunctuation(token, ")"));

    private IReadOnlyList<string> GetFollowingSyntaxWords(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        int completedTokenCount,
        IReadOnlyList<string> candidates)
    {
        if (prefix.Count == completedTokenCount
            && HasTrailingWhitespace(position, prefix[^1]))
        {
            return candidates;
        }

        if (prefix.Count != completedTokenCount + 1
            || !IsNameToken(prefix[^1])
            || HasTrailingWhitespace(position, prefix[^1]))
        {
            return [];
        }

        return candidates
            .Where(candidate => candidate.StartsWith(
                prefix[^1].Text,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Equals(
                prefix[^1].Text,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private bool IsCompletedGrammarMarkerAwaitingSeparator(
        StatementSpan statement,
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        IReadOnlyList<string> contextualStatements)
    {
        if (prefix.Count == 0 || HasTrailingWhitespace(position, prefix[^1]))
        {
            return false;
        }

        if (prefix.Count >= 2 && IsPunctuation(prefix[^2], "."))
        {
            return false;
        }

        if (IsCompletedModuleDeclarationWord(statement, position))
        {
            return true;
        }

        var last = prefix[^1];
        if (prefix.Count == 1
            && VbaLanguageVocabulary.ProcedureStatementWords.Contains(
                last.Text,
                StringComparer.OrdinalIgnoreCase)
            && !contextualStatements.Any(candidate =>
                candidate.Length > last.Text.Length
                && candidate.StartsWith(last.Text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return IsKeywordOperator(last)
            || IsWord(last, "As")
            || IsWord(last, "New")
            || IsWord(last, "To")
            || IsWord(last, "Step")
            || IsWord(last, "TypeOf")
            || IsWord(last, "AddressOf")
            || prefix.Count == 2
                && IsWord(prefix[0], "For")
                && IsWord(last, "Each")
            || prefix.Count == 2
                && (IsWord(prefix[0], "Do") || IsWord(prefix[0], "Loop"))
                && (IsWord(last, "While") || IsWord(last, "Until"))
            || prefix.Count == 2
                && IsWord(prefix[0], "Select")
                && IsWord(last, "Case")
            || prefix.Count >= 4
                && IsWord(prefix[0], "For")
                && IsWord(prefix[1], "Each")
                && IsWord(last, "In")
            || (IsWord(prefix[0], "If") || IsWord(prefix[0], "ElseIf"))
                && IsWord(last, "Then")
            || prefix.Count == 2
                && IsWord(prefix[0], "Exit")
                && (IsWord(last, "Do")
                    || IsWord(last, "For")
                    || IsWord(last, "Function")
                    || IsWord(last, "Property")
                    || IsWord(last, "Sub"));
    }

    private bool IsCompletedModuleDeclarationWord(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var statementPrefix = GetStatementPrefix(statement, position);
        if (statementPrefix is null || statementPrefix.Value.HasTrailingWhitespace)
        {
            return false;
        }

        var words = GetModuleStarterWords();
        if (words.Contains(
                statementPrefix.Value.Text,
                StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var continuations = moduleKind == VbaModuleKind.StandardModule
            ? StandardDeclarationContinuationWords
            : ObjectDeclarationContinuationWords;
        return continuations.Any(continuation => continuation.Value.Any(word =>
            statementPrefix.Value.Text.Equals(
                $"{continuation.Key} {word}",
                StringComparison.OrdinalIgnoreCase)));
    }

    private IReadOnlyList<string> GetModuleStarterWords()
        => moduleKind switch
        {
            VbaModuleKind.StandardModule => StandardModuleStarterWords,
            VbaModuleKind.ClassModule => ClassModuleStarterWords,
            _ => ObjectModuleStarterWords
        };

    private static bool HasUnsupportedSpecialOperandContext(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCallSiteSyntax? callSite)
    {
        var scopedPrefix = GetActiveCompletionScopePrefix(prefix, position, callSite);
        var addressOfIndex = FindLastToken(
            scopedPrefix,
            token => IsWord(token, "AddressOf"));
        if (addressOfIndex >= 0
            && scopedPrefix
                .Skip(addressOfIndex + 1)
                .All(token => IsNameToken(token) || IsPunctuation(token, ".")))
        {
            return true;
        }

        var typeOfIndex = FindLastToken(
            scopedPrefix,
            token => IsWord(token, "TypeOf"));
        return typeOfIndex >= 0
            && !scopedPrefix
                .Skip(typeOfIndex + 1)
                .Any(token => IsWord(token, "Is"));
    }

    private static IReadOnlyList<VbaToken> GetActiveCompletionScopePrefix(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCallSiteSyntax? callSite)
    {
        var scopeStart = 0;
        if (callSite is not null
            && callSite.ActiveArgumentIndex >= 0
            && callSite.ActiveArgumentIndex < callSite.Arguments.Count)
        {
            var activeRange = callSite.Arguments[callSite.ActiveArgumentIndex].Range;
            if (position.Offset >= activeRange.Start.Offset
                && position.Offset <= activeRange.End.Offset)
            {
                scopeStart = activeRange.Start.Offset;
            }
        }

        return prefix
            .Where(token => token.Range.Start.Offset >= scopeStart)
            .ToArray();
    }

    private VbaCompletionExpectation? GetStatementContinuationExpectation(
        StatementSpan statement,
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaMemberAccessSyntax? memberAccess)
    {
        if (GetDefaultExpectation(statement, position)
            != VbaCompletionExpectation.ProcedureStatement
            || prefix.Count == 0)
        {
            return null;
        }

        if (IsWord(prefix[0], "Call"))
        {
            if (memberAccess is not null
                && memberAccess.Range.Start.Offset > prefix[0].Range.End.Offset
                && !memberAccess.HasTrailingWhitespace)
            {
                return VbaCompletionExpectation.CallableName;
            }

            if ((prefix.Count == 1 && HasTrailingWhitespace(position, prefix[0]))
                || (prefix.Count == 2
                    && IsNameToken(prefix[1])
                    && !HasTrailingWhitespace(position, prefix[1])))
            {
                return VbaCompletionExpectation.CallableName;
            }

            return null;
        }

        if (prefix.Count == 3
            && IsWord(prefix[0], "Debug")
            && IsPunctuation(prefix[1], ".")
            && (IsWord(prefix[2], "Print") || IsWord(prefix[2], "Assert"))
            && HasTrailingWhitespace(position, prefix[2]))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        if (prefix.Count == 4
            && IsWord(prefix[0], "Debug")
            && IsPunctuation(prefix[1], ".")
            && (IsWord(prefix[2], "Print") || IsWord(prefix[2], "Assert"))
            && IsPunctuation(prefix[3], "("))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        if (IsTopLevelDebugPrintSeparator(prefix))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        if (IsWord(prefix[0], "For")
            && (IsWord(prefix[^1], "To") || IsWord(prefix[^1], "Step"))
            && (prefix.Count < 2 || !IsPunctuation(prefix[^2], "."))
            && HasTrailingWhitespace(position, prefix[^1]))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        var typeOfIndex = FindToken(prefix, token => IsWord(token, "TypeOf"));
        var isIndex = FindLastToken(prefix, token => IsWord(token, "Is"));
        if (typeOfIndex >= 0 && isIndex > typeOfIndex)
        {
            var typeTokens = prefix.Skip(isIndex + 1).ToArray();
            if ((typeTokens.Length == 0 && HasTrailingWhitespace(position, prefix[isIndex]))
                || (typeTokens.Length == 1
                    && IsNameToken(typeTokens[0])
                    && !HasTrailingWhitespace(position, typeTokens[0])))
            {
                return VbaCompletionExpectation.TypeName;
            }
        }

        return null;
    }

    private static bool IsTopLevelDebugPrintSeparator(IReadOnlyList<VbaToken> prefix)
    {
        if (prefix.Count < 4
            || !IsWord(prefix[0], "Debug")
            || !IsPunctuation(prefix[1], ".")
            || !IsWord(prefix[2], "Print"))
        {
            return false;
        }

        var depth = 0;
        for (var index = 3; index < prefix.Count - 1; index++)
        {
            if (IsPunctuation(prefix[index], "("))
            {
                depth++;
            }
            else if (IsPunctuation(prefix[index], ")"))
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        var last = prefix[^1];
        return depth == 0
            && (IsPunctuation(last, ",") || IsPunctuation(last, ";"));
    }

    private IReadOnlyList<string> GetContextualStatements(
        StatementSpan statement,
        VbaSyntaxPosition position,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks)
    {
        var innermost = enclosingBlocks.LastOrDefault();
        if (innermost is null || innermost.Block.IsMalformedBarrier)
        {
            return [];
        }

        IReadOnlyList<string> candidates = innermost.Block.Kind switch
        {
            VbaBlockKind.If when innermost.ActiveBranch == VbaBlockBranchKind.Else =>
                [innermost.Block.ExpectedTerminator],
            VbaBlockKind.If =>
                ["Else", "ElseIf", innermost.Block.ExpectedTerminator],
            VbaBlockKind.Select when innermost.ActiveBranch == VbaBlockBranchKind.CaseElse =>
                [innermost.Block.ExpectedTerminator],
            VbaBlockKind.Select =>
                ["Case", "Case Else", innermost.Block.ExpectedTerminator],
            _ when !string.IsNullOrWhiteSpace(innermost.Block.ExpectedTerminator) =>
                [innermost.Block.ExpectedTerminator],
            _ => []
        };
        var prefix = GetStatementPrefix(statement, position);
        if (prefix is null)
        {
            return candidates;
        }

        if (IsCaseExpressionPrefix(prefix.Value))
        {
            return [];
        }

        var matching = candidates
            .Where(candidate => candidate.StartsWith(
                prefix.Value.Text,
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => !candidate.Equals(
                prefix.Value.Text,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matching.Length > 0 || IsContextualStarter(prefix.Value.Text, candidates)
            ? matching
            : candidates;
    }

    private bool IsContextualStatementPosition(
        StatementSpan statement,
        VbaSyntaxPosition position,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks,
        IReadOnlyList<string> contextualStatements)
    {
        if (contextualStatements.Count == 0)
        {
            return false;
        }

        var prefix = GetStatementPrefix(statement, position);
        if (prefix is not null)
        {
            return contextualStatements.Any(candidate => candidate.StartsWith(
                prefix.Value.Text,
                StringComparison.OrdinalIgnoreCase));
        }

        var innermost = enclosingBlocks.LastOrDefault();
        return innermost?.Block.Kind == VbaBlockKind.Select
            && innermost.ActiveBranch == VbaBlockBranchKind.Body;
    }

    private StatementPrefix? GetStatementPrefix(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();
        if (prefix.Length == 0)
        {
            return null;
        }

        return new StatementPrefix(
            string.Join(' ', prefix.Select(token => token.Text)),
            HasTrailingWhitespace(position, prefix[^1]));
    }

    private static bool IsContextualStarter(
        string prefix,
        IReadOnlyList<string> candidates)
    {
        var prefixFirstWord = FirstWord(prefix);
        return candidates.Any(candidate =>
        {
            var candidateFirstWord = FirstWord(candidate);
            return candidateFirstWord.StartsWith(
                    prefixFirstWord,
                    StringComparison.OrdinalIgnoreCase)
                || prefixFirstWord.Equals(
                    candidateFirstWord,
                    StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsCaseExpressionPrefix(StatementPrefix prefix)
    {
        if (prefix.Text.Equals("Case", StringComparison.OrdinalIgnoreCase))
        {
            return prefix.HasTrailingWhitespace;
        }

        const string casePrefix = "Case ";
        if (!prefix.Text.StartsWith(casePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private VbaCompletionExpectation? GetCaseExpressionExpectation(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count == 0 || !IsWord(prefix[0], "Case"))
        {
            return null;
        }

        if (prefix.Count == 1 && HasTrailingWhitespace(position, prefix[0]))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        var last = prefix[^1];
        if (prefix.Count == 2 && IsWord(last, "Else"))
        {
            return VbaCompletionExpectation.None;
        }

        if (IsPunctuation(last, ",")
            || (IsWord(last, "To") && HasTrailingWhitespace(position, last)))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        if (prefix.Count == 2
            && (IsNameToken(last) || last.Kind == VbaTokenKind.NumericLiteral)
            && !IsWord(last, "Else")
            && !HasTrailingWhitespace(position, last))
        {
            return VbaCompletionExpectation.ExpressionValue;
        }

        return null;
    }

    private VbaCompletionExpectation? GetOnSelectorExpressionExpectation(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count == 0
            || !IsWord(prefix[0], "On")
            || (prefix.Count >= 2 && IsWord(prefix[1], "Error"))
            || prefix.Any(token => IsWord(token, "GoTo") || IsWord(token, "GoSub"))
            || prefix.Skip(1).Any(token => IsPunctuation(token, ".")))
        {
            return null;
        }

        return ClassifyExpressionTail(
            prefix.Skip(1).ToArray(),
            position,
            VbaCompletionExpectation.ExpressionValue);
    }

    private VbaCompletionExpectation? GetConditionExpressionExpectation(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCallSiteSyntax callSite)
    {
        if (prefix.Count < 2
            || (!IsWord(prefix[0], "If") && !IsWord(prefix[0], "While"))
            || (IsWord(prefix[0], "If") && prefix.Any(token => IsWord(token, "Then"))))
        {
            return null;
        }

        var callee = callSite.Callee.Target;
        if (callee is not null
            && (!callee.IsKeyword
                || !callee.Name.Equals(prefix[0].Text, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return ClassifyExpressionTail(
            prefix.Skip(1).ToArray(),
            position,
            VbaCompletionExpectation.ExpressionValue);
    }

    private IReadOnlyList<VbaEnclosingBlockSyntax> GetEnclosingBlocks(int offset)
        => blocks
            .Where(block => block.IsMalformedBarrier
                ? offset >= block.Range.Start.Offset && offset <= block.Range.End.Offset
                : offset >= block.OpenerRange.End.Offset
                    && offset <= block.Range.End.Offset
                    && (block.CloserRange is null || offset <= block.CloserRange.Start.Offset))
            .OrderBy(block => block.OpenerRange.Start.Offset)
            .ThenByDescending(block => block.Range.End.Offset)
            .Select(block => new VbaEnclosingBlockSyntax(block, GetActiveBranch(block, offset)))
            .ToArray();

    private static VbaBlockBranchKind GetActiveBranch(VbaBlockSyntax block, int offset)
        => block.Branches
            .Where(branch => branch.HeaderRange.End.Offset <= offset)
            .OrderBy(branch => branch.HeaderRange.Start.Offset)
            .Select(branch => branch.Kind)
            .LastOrDefault(VbaBlockBranchKind.Body);

    private VbaLabelReferenceSyntax? TryGetLabelReference(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var owner = callableDeclarations
            .Where(declaration => !declaration.IsExternal)
            .Where(declaration => declaration.BlockRange.Start.Offset <= position.Offset
                && position.Offset <= declaration.BlockRange.End.Offset)
            .OrderBy(declaration => declaration.BlockRange.End.Offset - declaration.BlockRange.Start.Offset)
            .FirstOrDefault();
        if (owner is null || position.Line <= owner.LineIndex)
        {
            return null;
        }

        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();
        if (!TryFindLabelMarker(
                prefix,
                out var markerIndex,
                out var kind,
                out var allowsLabels,
                out var syntaxCandidates))
        {
            return null;
        }

        var destinationTokens = prefix.Skip(markerIndex + 1).ToArray();
        var destinationIndex = 0;
        var destinationStart = 0;
        if (kind is VbaLabelReferenceKind.OnGoTo or VbaLabelReferenceKind.OnGoSub)
        {
            for (var index = 0; index < destinationTokens.Length; index++)
            {
                if (IsPunctuation(destinationTokens[index], ","))
                {
                    destinationIndex++;
                    destinationStart = index + 1;
                }
            }
        }

        var activeTokens = destinationTokens.Skip(destinationStart).ToArray();
        if (activeTokens.Length == 0
            && !HasTrailingWhitespace(position, prefix[markerIndex]))
        {
            return new VbaLabelReferenceSyntax(
                kind,
                destinationIndex,
                IsIncomplete: false,
                allowsLabels,
                syntaxCandidates,
                owner.Name,
                owner.BlockRange,
                new VbaSyntaxRange(position, position));
        }

        var isValidSlot = activeTokens.Length == 0
            || (activeTokens.Length == 1
                && (IsNameToken(activeTokens[0])
                    || activeTokens[0].Kind == VbaTokenKind.NumericLiteral));
        if (!isValidSlot)
        {
            return new VbaLabelReferenceSyntax(
                kind,
                destinationIndex,
                IsIncomplete: false,
                allowsLabels,
                syntaxCandidates,
                owner.Name,
                owner.BlockRange,
                new VbaSyntaxRange(position, position));
        }

        var activeToken = activeTokens.SingleOrDefault();
        var hasTrailingWhitespace = activeToken is not null && HasTrailingWhitespace(position, activeToken);
        var replacementRange = activeToken?.Range ?? new VbaSyntaxRange(position, position);
        return new VbaLabelReferenceSyntax(
            kind,
            destinationIndex,
            IsIncomplete: !hasTrailingWhitespace,
            allowsLabels,
            syntaxCandidates,
            owner.Name,
            owner.BlockRange,
            replacementRange);
    }

    private static bool TryFindLabelMarker(
        IReadOnlyList<VbaToken> prefix,
        out int markerIndex,
        out VbaLabelReferenceKind kind,
        out bool allowsLabels,
        out IReadOnlyList<string> syntaxCandidates)
    {
        markerIndex = -1;
        kind = default;
        allowsLabels = true;
        syntaxCandidates = [];
        var onIndex = FindToken(prefix, token => IsWord(token, "On"));
        if (onIndex >= 0 && onIndex + 2 < prefix.Count && IsWord(prefix[onIndex + 1], "Error"))
        {
            if (IsWord(prefix[onIndex + 2], "GoTo"))
            {
                markerIndex = onIndex + 2;
                kind = VbaLabelReferenceKind.OnErrorGoTo;
                syntaxCandidates = ["0"];
                return true;
            }

            if (IsWord(prefix[onIndex + 2], "Resume"))
            {
                markerIndex = onIndex + 2;
                kind = VbaLabelReferenceKind.OnErrorResume;
                allowsLabels = false;
                syntaxCandidates = ["Next"];
                return true;
            }

            return false;
        }

        var goToIndex = FindLastToken(prefix, token => IsWord(token, "GoTo"));
        var goSubIndex = FindLastToken(prefix, token => IsWord(token, "GoSub"));
        markerIndex = Math.Max(goToIndex, goSubIndex);
        if (markerIndex >= 0)
        {
            var isOnList = onIndex >= 0 && onIndex < markerIndex;
            kind = markerIndex == goToIndex
                ? isOnList ? VbaLabelReferenceKind.OnGoTo : VbaLabelReferenceKind.GoTo
                : isOnList ? VbaLabelReferenceKind.OnGoSub : VbaLabelReferenceKind.GoSub;
            return true;
        }

        markerIndex = FindLastToken(prefix, token => IsWord(token, "Resume"));
        if (markerIndex < 0)
        {
            return false;
        }

        kind = VbaLabelReferenceKind.Resume;
        syntaxCandidates = ["Next"];
        return true;
    }

    private VbaSyntaxRange? GetCompletionReplacementRange(
        StatementSpan statement,
        VbaSyntaxPosition position,
        VbaPositionIdentifierSyntax? identifier,
        VbaLabelReferenceSyntax? labelReference,
        VbaCompletionExpectation expectation)
    {
        if (labelReference is not null)
        {
            return labelReference.ReplacementRange;
        }

        var prefix = statement.SignificantTokens
            .Where(token => token.Range.Start.Offset < position.Offset)
            .ToArray();
        if (expectation == VbaCompletionExpectation.ContextualStatement
            && prefix.Length > 0)
        {
            return new VbaSyntaxRange(prefix[0].Range.Start, position);
        }

        if (prefix.Length == 1
            && IsWord(prefix[0], "End")
            && position.Offset >= prefix[0].Range.End.Offset)
        {
            return new VbaSyntaxRange(prefix[0].Range.Start, position);
        }

        return identifier?.Range;
    }

    private bool IsRecognizedCompletionStarter(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position,
        VbaCompletionExpectation expectation,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks,
        IReadOnlyList<string> starterWords)
    {
        if (prefix.Count != 1 || !IsNameToken(prefix[0]))
        {
            return false;
        }

        var token = prefix[0];
        var hasTrailingWhitespace = HasTrailingWhitespace(position, token);
        var contextualPrefix = GetContextualStarterPrefix(token.Text, enclosingBlocks);
        if (contextualPrefix)
        {
            return !hasTrailingWhitespace
                || token.Text.Equals("End", StringComparison.OrdinalIgnoreCase)
                || token.Text.Equals("Else", StringComparison.OrdinalIgnoreCase)
                || token.Text.Equals("ElseIf", StringComparison.OrdinalIgnoreCase);
        }

        var words = expectation == VbaCompletionExpectation.ModuleDeclaration
            ? starterWords
            : VbaLanguageVocabulary.ProcedureStatementWords;
        var isVocabularyPrefix = words.Any(word =>
            word.StartsWith(token.Text, StringComparison.OrdinalIgnoreCase));
        if (!hasTrailingWhitespace)
        {
            return isVocabularyPrefix
                || (expectation == VbaCompletionExpectation.ProcedureStatement
                    && token.Kind == VbaTokenKind.Identifier);
        }

        return false;
    }

    private static bool GetContextualStarterPrefix(
        string prefix,
        IReadOnlyList<VbaEnclosingBlockSyntax> enclosingBlocks)
    {
        var innermost = enclosingBlocks.LastOrDefault();
        if (innermost is null || innermost.Block.IsMalformedBarrier)
        {
            return false;
        }

        var block = innermost.Block;
        if (FirstWord(block.ExpectedTerminator).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return block.Kind switch
        {
            VbaBlockKind.If when innermost.ActiveBranch != VbaBlockBranchKind.Else
                => "Else".StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || "ElseIf".StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            VbaBlockKind.Select
                => "Case".StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string FirstWord(string value)
    {
        var separator = value.IndexOf(' ');
        return separator < 0 ? value : value[..separator];
    }

    private VbaCompletionExpectation? GetEventNameExpectation(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        var raiseEventIndex = FindToken(prefix, token => IsWord(token, "RaiseEvent"));
        if (raiseEventIndex < 0)
        {
            return null;
        }

        var slotTokens = prefix.Skip(raiseEventIndex + 1).ToArray();
        if (slotTokens.Length >= 2
            && IsNameToken(slotTokens[0])
            && IsPunctuation(slotTokens[1], "("))
        {
            if (slotTokens.Any(token =>
                token.Kind == VbaTokenKind.Operator && token.Text == ":="))
            {
                return VbaCompletionExpectation.None;
            }

            return null;
        }

        return ClassifyNameSlot(slotTokens, position, VbaCompletionExpectation.EventName);
    }

    private VbaCompletionExpectation ClassifyNameSlot(
        IReadOnlyList<VbaToken> slotTokens,
        VbaSyntaxPosition position,
        VbaCompletionExpectation expectation)
    {
        if (slotTokens.Count == 0)
        {
            return expectation;
        }

        return slotTokens.Count == 1
            && IsNameToken(slotTokens[0])
            && !HasTrailingWhitespace(position, slotTokens[0])
                ? expectation
                : VbaCompletionExpectation.None;
    }

    private VbaCompletionExpectation ClassifyExpressionTail(
        IReadOnlyList<VbaToken> expressionTokens,
        VbaSyntaxPosition position,
        VbaCompletionExpectation expectation)
    {
        if (expressionTokens.Count == 0)
        {
            return expectation;
        }

        var last = expressionTokens[^1];
        if (last.Kind == VbaTokenKind.Operator && last.Text != ":=")
        {
            return expectation == VbaCompletionExpectation.NamedArgumentValue
                ? expectation
                : VbaCompletionExpectation.ExpressionValue;
        }

        if (IsKeywordOperator(last))
        {
            return expectation == VbaCompletionExpectation.NamedArgumentValue
                ? expectation
                : VbaCompletionExpectation.ExpressionValue;
        }

        if (IsKeyword(last, "New"))
        {
            return VbaCompletionExpectation.CreatableType;
        }

        if (last.Kind == VbaTokenKind.Punctuation && last.Text == "(")
        {
            return expectation;
        }

        if (IsNameToken(last))
        {
            if (HasTrailingWhitespace(position, last))
            {
                return VbaCompletionExpectation.None;
            }

            return expressionTokens.Count == 1
                    || CanStartExpressionOperandAfter(expressionTokens[^2])
                ? expectation
                : VbaCompletionExpectation.None;
        }

        return VbaCompletionExpectation.None;
    }

    private static bool CanStartExpressionOperandAfter(VbaToken token)
        => (token.Kind == VbaTokenKind.Operator && token.Text != ":=")
            || IsKeywordOperator(token)
            || IsPunctuation(token, "(")
            || IsPunctuation(token, ",")
            || IsWord(token, "To")
            || IsWord(token, "Step")
            || IsWord(token, "In");

    private static VbaToken? FindAssignmentOperator(IReadOnlyList<VbaToken> tokens)
    {
        if (tokens.Count == 0 || IsComparisonStatement(tokens))
        {
            return null;
        }

        var depth = 0;
        foreach (var token in tokens)
        {
            if (IsPunctuation(token, "("))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(token, ")"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && token.Kind == VbaTokenKind.Operator && token.Text == "=")
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsComparisonStatement(IReadOnlyList<VbaToken> tokens)
    {
        var first = tokens[0].Text;
        return first.Equals("If", StringComparison.OrdinalIgnoreCase)
            || first.Equals("ElseIf", StringComparison.OrdinalIgnoreCase)
            || first.Equals("While", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Do", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Loop", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Select", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Case", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExplicitAssignmentTarget(
        IReadOnlyList<VbaToken> prefix,
        VbaSyntaxPosition position)
    {
        if (prefix.Count == 0)
        {
            return false;
        }

        if (IsWord(prefix[0], "Set") || IsWord(prefix[0], "Let"))
        {
            return prefix.Count == 1
                || (prefix.Count == 2
                    && IsNameToken(prefix[1])
                    && !HasTrailingWhitespace(position, prefix[1]));
        }

        if (!IsWord(prefix[0], "For"))
        {
            return false;
        }

        return prefix.Count == 1
            || (IsWord(prefix[1], "Each")
                && !prefix.Any(token => IsWord(token, "In")));
    }

    private static int FindExpressionStart(IReadOnlyList<VbaToken> prefix)
    {
        if (prefix.Count == 0)
        {
            return -1;
        }

        if (IsWord(prefix[0], "If")
            || IsWord(prefix[0], "ElseIf")
            || IsWord(prefix[0], "While")
            || IsWord(prefix[0], "With")
            || IsWord(prefix[0], "Case"))
        {
            return 1;
        }

        if (IsWord(prefix[0], "On")
            && (prefix.Count < 2 || !IsWord(prefix[1], "Error")))
        {
            return 1;
        }

        if (prefix.Count >= 2
            && IsWord(prefix[0], "Select")
            && IsWord(prefix[1], "Case"))
        {
            return 2;
        }

        if (prefix.Count >= 2
            && (IsWord(prefix[0], "Do") || IsWord(prefix[0], "Loop"))
            && (IsWord(prefix[1], "While") || IsWord(prefix[1], "Until")))
        {
            return 2;
        }

        if (IsWord(prefix[0], "For"))
        {
            var inIndex = FindToken(prefix, token => IsWord(token, "In"));
            return inIndex >= 0 ? inIndex + 1 : -1;
        }

        return -1;
    }

    private bool HasTrailingWhitespace(VbaSyntaxPosition position, VbaToken token)
    {
        if (position.Offset <= token.Range.End.Offset)
        {
            return false;
        }

        var end = Math.Min(position.Offset, sourceText.Text.Length);
        return sourceText.Text[token.Range.End.Offset..end].Any(char.IsWhiteSpace);
    }

    private static bool IsKeywordOperator(VbaToken token)
        => IsWord(token, "And")
            || IsWord(token, "Or")
            || IsWord(token, "Xor")
            || IsWord(token, "Eqv")
            || IsWord(token, "Imp")
            || IsWord(token, "Mod")
            || IsWord(token, "Like")
            || IsWord(token, "Is")
            || IsWord(token, "Not");

    private static int FindToken(
        IReadOnlyList<VbaToken> tokens,
        Func<VbaToken, bool> predicate)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (predicate(tokens[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindLastToken(
        IReadOnlyList<VbaToken> tokens,
        Func<VbaToken, bool> predicate)
    {
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            if (predicate(tokens[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsAfterLineContinuation(VbaSyntaxPosition position)
    {
        var lineTokens = nonWhitespaceTokensByLine[position.Line];
        var low = 0;
        var high = lineTokens.Count - 1;
        var previousIndex = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lineTokens[middle].Range.Start.Offset < position.Offset)
            {
                previousIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return previousIndex >= 0
            && lineTokens[previousIndex].Kind == VbaTokenKind.LineContinuation;
    }

    private IReadOnlyList<VbaWithScopeSyntax> GetEnclosingWithScopes(int offset)
    {
        var low = 0;
        var high = withScopes.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (withScopes[middle].StartOffset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var enclosing = new List<WithScopeSpan>();
        for (var index = candidate;
             index >= 0 && withScopePrefixMaximumEnds[index] > offset;
             index--)
        {
            if (offset < withScopes[index].EndOffset)
            {
                enclosing.Add(withScopes[index]);
            }
        }

        return enclosing
            .OrderBy(scope => scope.Depth)
            .Select(scope => new VbaWithScopeSyntax(scope.Receiver))
            .ToArray();
    }

    private static IReadOnlyList<int> BuildPrefixMaximumEnds(
        IReadOnlyList<WithScopeSpan> scopes)
    {
        var maximumEnds = new int[scopes.Count];
        var maximum = 0;
        for (var index = 0; index < scopes.Count; index++)
        {
            maximum = Math.Max(maximum, scopes[index].EndOffset);
            maximumEnds[index] = maximum;
        }

        return maximumEnds;
    }

    private int FindTokenIndex(int offset)
    {
        var low = 0;
        var high = tokens.Count - 1;
        var candidate = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (tokens[middle].Range.Start.Offset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return candidate >= 0 && offset <= tokens[candidate].Range.End.Offset
            ? candidate
            : -1;
    }

    private StatementSpan FindStatement(int offset)
    {
        var low = 0;
        var high = statements.Count - 1;
        var candidate = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (statements[middle].StartOffset <= offset)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return statements[candidate];
    }

    private static StatementSpan GetActiveCompletionStatement(
        StatementSpan statement,
        VbaSyntaxPosition position)
    {
        var significant = statement.SignificantTokens;
        if (significant.Count == 0
            || (!IsWord(significant[0], "If") && !IsWord(significant[0], "ElseIf")))
        {
            return statement;
        }

        var depth = 0;
        var branchStartIndex = 0;
        VbaSyntaxPosition? branchStart = null;
        var hasThen = false;
        for (var index = 1; index < significant.Count; index++)
        {
            var token = significant[index];
            if (token.Range.Start.Offset >= position.Offset)
            {
                break;
            }

            if (IsPunctuation(token, "("))
            {
                depth++;
                continue;
            }

            if (IsPunctuation(token, ")"))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth != 0 || (index > 0 && IsPunctuation(significant[index - 1], ".")))
            {
                continue;
            }

            if (IsWord(token, "Then"))
            {
                hasThen = true;
                branchStartIndex = index + 1;
                branchStart = token.Range.End;
            }
            else if (hasThen && IsWord(token, "Else"))
            {
                branchStartIndex = index + 1;
                branchStart = token.Range.End;
            }
        }

        if (branchStart is null || branchStart.Offset >= position.Offset)
        {
            return statement;
        }

        return new StatementSpan(
            branchStart.Offset,
            statement.EndOffset,
            statement.NextOffset,
            significant.Skip(branchStartIndex).ToArray());
    }

    private static IReadOnlyList<StatementSpan> BuildStatements(
        int textLength,
        IReadOnlyList<VbaToken> tokens)
        => VbaLogicalStatementSpan.Build(textLength, tokens)
            .Select(statement => new StatementSpan(
                statement.StartOffset,
                statement.EndOffset,
                statement.NextOffset,
                statement.SignificantTokens))
            .ToArray();

    private static IReadOnlyList<IReadOnlyList<VbaToken>> BuildNonWhitespaceTokensByLine(
        int lineCount,
        IReadOnlyList<VbaToken> tokens)
    {
        var lineTokens = Enumerable.Range(0, lineCount)
            .Select(_ => new List<VbaToken>())
            .ToArray();
        foreach (var token in tokens)
        {
            if (token.Range.Start.Line >= 0
                && token.Range.Start.Line < lineTokens.Length
                && token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.NewLine)
            {
                lineTokens[token.Range.Start.Line].Add(token);
            }
        }

        return lineTokens
            .Select(tokensOnLine => (IReadOnlyList<VbaToken>)tokensOnLine.ToArray())
            .ToArray();
    }

    private static IReadOnlyList<WithScopeSpan> BuildWithScopes(
        int textLength,
        IReadOnlyList<StatementSpan> statements)
    {
        var completed = new List<WithScopeSpan>();
        var stack = new Stack<OpenWithScope>();
        foreach (var statement in statements)
        {
            var significant = statement.SignificantTokens;
            if (significant.Count >= 2
                && IsKeyword(significant[0], "End")
                && IsKeyword(significant[1], "With"))
            {
                if (stack.Count > 0)
                {
                    var scope = stack.Pop();
                    completed.Add(new WithScopeSpan(
                        scope.StartOffset,
                        statement.StartOffset,
                        scope.Depth,
                        scope.Receiver));
                }

                continue;
            }

            if (significant.Count > 0 && IsKeyword(significant[0], "With"))
            {
                var receiver = significant.Count > 1
                    ? CreateChain(significant, 1, significant.Count - 1, significant.Count - 1)
                    : null;
                stack.Push(new OpenWithScope(
                    statement.NextOffset,
                    stack.Count,
                    receiver));
            }
        }

        while (stack.Count > 0)
        {
            var scope = stack.Pop();
            completed.Add(new WithScopeSpan(
                scope.StartOffset,
                textLength + 1,
                scope.Depth,
                scope.Receiver));
        }

        return completed;
    }

    private static VbaMemberAccessSyntax? CreateChain(
        IReadOnlyList<VbaToken> tokens,
        int start,
        int end,
        int targetTokenIndex)
    {
        if (start < 0 || end < start || end >= tokens.Count)
        {
            return null;
        }

        var index = start;
        var leadingDot = IsDot(tokens[index]);
        if (leadingDot)
        {
            index++;
        }

        var segments = new List<VbaPositionIdentifierSyntax>();
        var targetSegmentIndex = -1;
        var expectName = true;
        for (; index <= end; index++)
        {
            var token = tokens[index];
            if (expectName)
            {
                if (!IsNameToken(token))
                {
                    return null;
                }

                if (index == targetTokenIndex)
                {
                    targetSegmentIndex = segments.Count;
                }

                segments.Add(ToIdentifier(token));
            }
            else if (!IsDot(token))
            {
                return null;
            }

            expectName = !expectName;
        }

        if (segments.Count == 0 || expectName || targetSegmentIndex < 0)
        {
            return null;
        }

        return new VbaMemberAccessSyntax(
            segments,
            targetSegmentIndex,
            leadingDot,
            false,
            false,
            new VbaSyntaxRange(tokens[start].Range.Start, tokens[end].Range.End));
    }

    private static bool IsNameToken(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.Keyword;

    private static bool IsDot(VbaToken token)
        => IsPunctuation(token, ".");

    private static bool IsPunctuation(VbaToken token, string text)
        => token.Kind == VbaTokenKind.Punctuation && token.Text == text;

    private static bool IsKeyword(VbaToken token, string text)
        => token.Kind == VbaTokenKind.Keyword
            && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool IsWord(VbaToken token, string text)
        => IsNameToken(token)
            && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static VbaPositionIdentifierSyntax ToIdentifier(VbaToken token)
        => new(token.Text, token.Range, token.Kind == VbaTokenKind.Keyword);

    private sealed record StatementSpan(
        int StartOffset,
        int EndOffset,
        int NextOffset,
        IReadOnlyList<VbaToken> SignificantTokens);

    private sealed record OpenWithScope(
        int StartOffset,
        int Depth,
        VbaMemberAccessSyntax? Receiver);

    private readonly record struct StatementPrefix(
        string Text,
        bool HasTrailingWhitespace);

    private sealed record WithScopeSpan(
        int StartOffset,
        int EndOffset,
        int Depth,
        VbaMemberAccessSyntax? Receiver);
}
