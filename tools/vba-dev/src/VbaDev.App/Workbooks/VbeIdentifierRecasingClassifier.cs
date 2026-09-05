using VbaTools.Syntax;

namespace VbaDev.App.Workbooks;

/// <summary>
/// Describes one source-to-VBE identifier casing change.
/// </summary>
/// <param name="SourceIdentifier">The identifier spelling in caller-owned source.</param>
/// <param name="VbeIdentifier">The identifier spelling projected by the VBE.</param>
public sealed record VbeIdentifierRecasingPair(
    string SourceIdentifier,
    string VbeIdentifier);

/// <summary>
/// Reports one imported component whose only projected-code difference is
/// accepted VBE identifier recasing.
/// </summary>
/// <param name="ComponentName">The imported component name.</param>
/// <param name="DistinctPairs">Distinct source-to-VBE pairs in first-occurrence order.</param>
public sealed record VbeIdentifierRecasingWarning
{
    /// <summary>
    /// Creates one component-scoped recasing warning.
    /// </summary>
    public VbeIdentifierRecasingWarning(
        string componentName,
        IReadOnlyList<VbeIdentifierRecasingPair> distinctPairs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentNullException.ThrowIfNull(distinctPairs);
        var pairSnapshot = distinctPairs.ToArray();
        if (pairSnapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one identifier recasing pair is required.",
                nameof(distinctPairs));
        }

        var seenPairs = new HashSet<VbeIdentifierRecasingPair>();
        foreach (var pair in pairSnapshot)
        {
            if (pair is null
                || string.IsNullOrWhiteSpace(pair.SourceIdentifier)
                || string.IsNullOrWhiteSpace(pair.VbeIdentifier)
                || string.Equals(
                    pair.SourceIdentifier,
                    pair.VbeIdentifier,
                    StringComparison.Ordinal)
                || !string.Equals(
                    pair.SourceIdentifier,
                    pair.VbeIdentifier,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Every warning pair must describe a non-exact, case-insensitive identifier match.",
                    nameof(distinctPairs));
            }

            if (!seenPairs.Add(pair))
            {
                throw new ArgumentException(
                    "Identifier recasing warning pairs must be distinct.",
                    nameof(distinctPairs));
            }
        }

        ComponentName = componentName;
        DistinctPairs = Array.AsReadOnly(pairSnapshot);
    }

    /// <summary>
    /// Gets the stable warning code exposed by command surfaces.
    /// </summary>
    public string Code => WarningCode;

    /// <summary>
    /// Gets the imported component name.
    /// </summary>
    public string ComponentName { get; }

    /// <summary>
    /// Gets distinct source-to-VBE pairs in first-occurrence order.
    /// </summary>
    public IReadOnlyList<VbeIdentifierRecasingPair> DistinctPairs { get; }

    /// <summary>
    /// Identifies accepted VBE identifier recasing on command surfaces.
    /// </summary>
    public const string WarningCode = "vbeIdentifierRecased";
}

/// <summary>
/// Contains ordered non-fatal warnings produced by one complete import verification.
/// </summary>
public sealed class VbeImportVerificationReport
{
    /// <summary>
    /// Gets an empty exact-verification report.
    /// </summary>
    public static VbeImportVerificationReport Empty { get; } = new([]);

    /// <summary>
    /// Creates an immutable snapshot of component warnings in import order.
    /// </summary>
    public VbeImportVerificationReport(
        IReadOnlyList<VbeIdentifierRecasingWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var warningSnapshot = warnings.ToArray();
        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var warning in warningSnapshot)
        {
            if (warning is null)
            {
                throw new ArgumentException(
                    "Verification warnings cannot contain null entries.",
                    nameof(warnings));
            }

            if (!seenComponents.Add(warning.ComponentName))
            {
                throw new ArgumentException(
                    "Verification reports can contain at most one warning per component.",
                    nameof(warnings));
            }
        }

        Warnings = Array.AsReadOnly(warningSnapshot);
    }

    /// <summary>
    /// Gets component warnings in import order.
    /// </summary>
    public IReadOnlyList<VbeIdentifierRecasingWarning> Warnings { get; }
}

/// <summary>
/// Classifies a complete imported-component difference as identifier-only VBE recasing.
/// </summary>
internal static class VbeIdentifierRecasingClassifier
{
    /// <summary>
    /// Returns distinct source-to-VBE identifier casing pairs in first-occurrence order
    /// only when component identity, kind, structure, comments, numeric-literal fragments,
    /// and every non-identifier token remain exact.
    /// </summary>
    internal static bool TryClassify(
        VbeImportVerification expected,
        VbeImportedComponent actual,
        out IReadOnlyList<VbeIdentifierRecasingPair> distinctPairs)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        distinctPairs = Array.Empty<VbeIdentifierRecasingPair>();
        if (!expected.ComponentName.Equals(actual.ComponentName, StringComparison.Ordinal)
            || expected.ComponentKind != actual.ComponentKind
            || expected.CodeModuleLines.Count != actual.CodeModuleLines.Count)
        {
            return false;
        }

        for (var lineIndex = 0; lineIndex < expected.CodeModuleLines.Count; lineIndex++)
        {
            var expectedParts = VbaLexicalFacts.SplitCodeAndComment(
                expected.CodeModuleLines[lineIndex]);
            var actualParts = VbaLexicalFacts.SplitCodeAndComment(
                actual.CodeModuleLines[lineIndex]);
            if (!expectedParts.CommentPart.Equals(
                actualParts.CommentPart,
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        var expectedTokens = VbaTokenStream.FromText(
            string.Join('\n', expected.CodeModuleLines)).Tokens;
        var actualTokens = VbaTokenStream.FromText(
            string.Join('\n', actual.CodeModuleLines)).Tokens;
        if (expectedTokens.Count != actualTokens.Count)
        {
            return false;
        }

        var pairs = new List<VbeIdentifierRecasingPair>();
        var seenPairs = new HashSet<VbeIdentifierRecasingPair>();
        for (var tokenIndex = 0; tokenIndex < expectedTokens.Count; tokenIndex++)
        {
            var expectedToken = expectedTokens[tokenIndex];
            var actualToken = actualTokens[tokenIndex];
            if (expectedToken.Kind != actualToken.Kind
                || expectedToken.Range != actualToken.Range)
            {
                return false;
            }

            if (expectedToken.Text.Equals(actualToken.Text, StringComparison.Ordinal))
            {
                continue;
            }

            if (expectedToken.Kind != VbaTokenKind.Identifier
                || !expectedToken.Text.Equals(
                    actualToken.Text,
                    StringComparison.OrdinalIgnoreCase)
                || IsNumericLiteralIdentifierFragment(expectedTokens, tokenIndex)
                || IsNumericLiteralIdentifierFragment(actualTokens, tokenIndex))
            {
                return false;
            }

            var pair = new VbeIdentifierRecasingPair(
                expectedToken.Text,
                actualToken.Text);
            if (seenPairs.Add(pair))
            {
                pairs.Add(pair);
            }
        }

        if (pairs.Count == 0)
        {
            return false;
        }

        distinctPairs = pairs.AsReadOnly();
        return true;
    }

    private static bool IsNumericLiteralIdentifierFragment(
        IReadOnlyList<VbaToken> tokens,
        int tokenIndex)
    {
        var token = tokens[tokenIndex];
        if (token.Kind != VbaTokenKind.Identifier
            || token.Text.Length == 0
            || tokenIndex == 0
            || tokens[tokenIndex - 1].Range.End.Offset != token.Range.Start.Offset)
        {
            return false;
        }

        var previous = tokens[tokenIndex - 1];
        if (previous.Text == "&")
        {
            var digits = token.Text.AsSpan(1);
            return (token.Text[0] is 'H' or 'h')
                    && digits.Length > 0
                    && ContainsOnlyHexDigits(digits)
                || (token.Text[0] is 'O' or 'o')
                    && digits.Length > 0
                    && ContainsOnlyOctalDigits(digits);
        }

        if (token.Text[0] is not ('D' or 'd' or 'E' or 'e')
            || !PreviousTokenEndsDecimalMantissa(tokens, tokenIndex - 1))
        {
            return false;
        }

        if (token.Text.Length > 1)
        {
            return ContainsOnlyAsciiDigits(token.Text.AsSpan(1));
        }

        var exponentDigitsIndex = tokenIndex + 1;
        if (exponentDigitsIndex < tokens.Count
            && tokens[exponentDigitsIndex].Text is "+" or "-"
            && token.Range.End.Offset
                == tokens[exponentDigitsIndex].Range.Start.Offset)
        {
            exponentDigitsIndex++;
        }

        return exponentDigitsIndex < tokens.Count
            && tokens[exponentDigitsIndex].Kind == VbaTokenKind.NumericLiteral
            && tokens[exponentDigitsIndex - 1].Range.End.Offset
                == tokens[exponentDigitsIndex].Range.Start.Offset
            && ContainsOnlyAsciiDigits(tokens[exponentDigitsIndex].Text);
    }

    private static bool PreviousTokenEndsDecimalMantissa(
        IReadOnlyList<VbaToken> tokens,
        int previousIndex)
    {
        if (tokens[previousIndex].Kind == VbaTokenKind.NumericLiteral)
        {
            return true;
        }

        return tokens[previousIndex].Text == "."
            && previousIndex > 0
            && tokens[previousIndex - 1].Kind == VbaTokenKind.NumericLiteral
            && tokens[previousIndex - 1].Range.End.Offset
                == tokens[previousIndex].Range.Start.Offset;
    }

    private static bool ContainsOnlyAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool ContainsOnlyHexDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool ContainsOnlyOctalDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '7')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
