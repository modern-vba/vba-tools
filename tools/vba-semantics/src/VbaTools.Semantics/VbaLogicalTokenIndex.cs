using VbaTools.Syntax;

namespace VbaTools.Semantics;

/// <summary>Positional lexical evidence for one captured module, not semantic answers.</summary>
internal sealed class VbaLogicalTokenIndex
{
    private readonly IReadOnlyList<VbaToken> tokens;
    private readonly int[] prefixStarts;

    internal VbaLogicalTokenIndex(VbaSyntaxTree syntaxTree, CancellationToken cancellationToken)
    {
        tokens = syntaxTree.TokenStream.Tokens;
        prefixStarts = new int[tokens.Count + 1];
        var start = 0;
        var continuesOnNextLine = false;
        var parenthesisDepth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            VbaSemanticWorkObservation.RecordTokenIndexPreparationToken();
            var token = tokens[index];
            switch (token.Kind)
            {
                case VbaTokenKind.Whitespace:
                    break;
                case VbaTokenKind.LineContinuation:
                    continuesOnNextLine = true;
                    break;
                case VbaTokenKind.NewLine:
                    if (continuesOnNextLine) continuesOnNextLine = false;
                    else { start = index + 1; parenthesisDepth = 0; }
                    break;
                case VbaTokenKind.Comment:
                    // A comment resets the prefix/depth, but not continuation evidence.
                    start = index + 1;
                    parenthesisDepth = 0;
                    break;
                default:
                    continuesOnNextLine = false;
                    if (token.Kind == VbaTokenKind.Punctuation)
                    {
                        if (token.Text == ":" && parenthesisDepth == 0) start = index + 1;
                        else if (token.Text == "(") parenthesisDepth++;
                        else if (token.Text == ")" && parenthesisDepth > 0) parenthesisDepth--;
                    }
                    if (parenthesisDepth == 0 && token.Kind == VbaTokenKind.Keyword
                        && (token.Text.Equals("Then", StringComparison.OrdinalIgnoreCase)
                            || token.Text.Equals("Else", StringComparison.OrdinalIgnoreCase)))
                        start = index + 1;
                    break;
            }
            prefixStarts[index + 1] = start;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal IReadOnlyList<VbaToken> Before(int offset)
    {
        var end = LowerBound(offset, useEnd: false);
        var result = new List<VbaToken>();
        for (var index = prefixStarts[end]; index < end; index++)
        {
            VbaSemanticWorkObservation.RecordLogicalContextToken();
            if (!IsTrivia(tokens[index])) result.Add(tokens[index]);
        }
        return result;
    }

    internal IReadOnlyList<VbaToken> After(int offset)
    {
        var result = new List<VbaToken>();
        var continuesOnNextLine = false;
        for (var index = LowerBound(offset, useEnd: true); index < tokens.Count; index++)
        {
            VbaSemanticWorkObservation.RecordLogicalContextToken();
            var token = tokens[index];
            if (token.Kind == VbaTokenKind.Whitespace) continue;
            if (token.Kind == VbaTokenKind.LineContinuation)
            {
                continuesOnNextLine = true;
                continue;
            }
            if (token.Kind == VbaTokenKind.NewLine)
            {
                if (continuesOnNextLine) { continuesOnNextLine = false; continue; }
                break;
            }
            if (token.Kind == VbaTokenKind.Comment) break;
            continuesOnNextLine = false;
            // Unlike prefix checkpoints, suffixes include colon/Then/Else tokens.
            result.Add(token);
        }
        return result;
    }

    internal VbaToken[] Within(VbaSyntaxRange range)
    {
        var result = new List<VbaToken>();
        for (var index = LowerBound(range.Start.Offset, useEnd: false, argumentRange: true);
             index < tokens.Count; index++)
        {
            VbaSemanticWorkObservation.RecordArgumentRangeToken();
            var token = tokens[index];
            if (token.Range.End.Offset > range.End.Offset) break;
            if (!IsTrivia(token)) result.Add(token);
        }
        return result.ToArray();
    }

    private int LowerBound(int offset, bool useEnd, bool argumentRange = false)
    {
        var low = 0;
        var high = tokens.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (argumentRange) VbaSemanticWorkObservation.RecordArgumentRangeToken();
            else VbaSemanticWorkObservation.RecordLogicalContextToken();
            var before = useEnd ? tokens[middle].Range.End.Offset <= offset
                : tokens[middle].Range.Start.Offset < offset;
            if (before) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static bool IsTrivia(VbaToken token)
        => token.Kind is VbaTokenKind.Whitespace or VbaTokenKind.Comment
            or VbaTokenKind.NewLine or VbaTokenKind.LineContinuation;
}

/// <summary>Retryable, atomic lazy publication owned by one admitted source inventory.</summary>
internal sealed class VbaLogicalTokenIndexOwner(VbaSourceDocument document)
{
    private VbaLogicalTokenIndex? published;

    internal VbaLogicalTokenIndex Get(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref published) is { } existing) return existing;
        var syntaxTree = document.SyntaxTree ?? VbaSyntaxTree.ParseModule(document.Uri, document.Text);
        var prepared = new VbaLogicalTokenIndex(syntaxTree, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Interlocked.CompareExchange(ref published, prepared, null) ?? prepared;
    }

    internal long EstimateRetainedBytes()
        => document.SyntaxTree is { } tree ? 256L + (tree.TokenStream.Tokens.Count + 1L) * sizeof(int)
            : long.MaxValue;
}
