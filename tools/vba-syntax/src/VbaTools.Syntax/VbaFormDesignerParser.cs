using System.Text.RegularExpressions;

namespace VbaTools.Syntax;

internal static partial class VbaFormDesignerParser
{
    private const string DesignerPropertyPathPattern =
        "[A-Za-z_][A-Za-z0-9_]*(?:\\([0-9]+\\))?"
        + "(?:\\.[A-Za-z_][A-Za-z0-9_]*(?:\\([0-9]+\\))?)*";

    private enum BlockKind
    {
        Component,
        Property
    }

    [GeneratedRegex(
        "^" + VbaIdentifier.RegexWhitespace + "*Begin"
        + VbaIdentifier.RegexWhitespace + "+(?<class>[^"
        + VbaIdentifier.RegexWhitespaceCharacters + "]+)"
        + VbaIdentifier.RegexWhitespace + "+(?<name>"
        + VbaIdentifier.RegexIdentifierCandidate + ")"
        + VbaIdentifier.RegexWhitespace + "*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComponentBeginPattern();

    [GeneratedRegex(
        "^[ \\t]*BeginProperty[ \\t]+[^ \\t].*[ \\t]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PropertyBeginPattern();

    [GeneratedRegex(
        "^[ \\t]*(?<property>" + DesignerPropertyPathPattern
        + ")[ \\t]*=[ \\t]*\"(?<file>[^\"]+)\"[ \\t]*:[ \\t]*"
        + "(?<offset>[0-9A-Fa-f]+)[ \\t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReferencePattern();

    [GeneratedRegex(
        "=[ \\t]*\"[^\"]+\\.frx\"[ \\t]*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReferenceCandidatePattern();

    [GeneratedRegex(
        "^[ \\t]*" + DesignerPropertyPathPattern + "[ \\t]*=",
        RegexOptions.CultureInvariant)]
    private static partial Regex PropertyAssignmentCandidatePattern();

    internal static VbaFormDesignerBlock Parse(
        VbaSourceText sourceText,
        int boundaryStartOffset)
    {
        var boundaryStart = sourceText.PositionAt(boundaryStartOffset);
        var rawText = sourceText.Text[..boundaryStartOffset];
        var range = new VbaSyntaxRange(sourceText.StartPosition, boundaryStart);
        var problems = new List<VbaFormDesignerEvidenceProblem>();
        var references = new List<VbaFormDesignerResourceReference>();
        var blocks = new Stack<BlockKind>();
        VbaFormDesignerRoot? root = null;

        foreach (var line in sourceText.Lines.Where(line =>
                     line.StartOffset < boundaryStartOffset))
        {
            var componentMatch = ComponentBeginPattern().Match(line.Text);
            if (componentMatch.Success)
            {
                if (blocks.Count == 0)
                {
                    var name = componentMatch.Groups["name"];
                    var candidate = new VbaFormDesignerRoot(
                        componentMatch.Groups["class"].Value,
                        name.Value,
                        sourceText.RangeForLine(
                            line,
                            name.Index,
                            name.Index + name.Length));
                    if (root is null)
                    {
                        root = candidate;
                    }
                    else
                    {
                        problems.Add(new VbaFormDesignerEvidenceProblem(
                            VbaFormDesignerEvidenceProblemKind.RootAmbiguous,
                            candidate.NameRange,
                            candidate.Name));
                    }
                }

                blocks.Push(BlockKind.Component);
                continue;
            }

            if (PropertyBeginPattern().IsMatch(line.Text))
            {
                if (blocks.Count == 0)
                {
                    problems.Add(new VbaFormDesignerEvidenceProblem(
                        VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                        sourceText.RangeForLine(line, 0, line.Text.Length)));
                }

                blocks.Push(BlockKind.Property);
                continue;
            }

            if (StartsWithStructuralKeyword(line.Text, "Begin")
                || StartsWithStructuralKeyword(line.Text, "BeginProperty"))
            {
                problems.Add(new VbaFormDesignerEvidenceProblem(
                    VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                    sourceText.RangeForLine(line, 0, line.Text.Length)));
                continue;
            }

            var trimmed = VbaIdentifier.TrimWhitespace(line.Text);
            var closingKind = trimmed.Equals(
                "End",
                StringComparison.OrdinalIgnoreCase)
                    ? BlockKind.Component
                    : trimmed.Equals(
                        "EndProperty",
                        StringComparison.OrdinalIgnoreCase)
                            ? BlockKind.Property
                            : (BlockKind?)null;
            if (closingKind is not null)
            {
                if (blocks.Count == 0 || blocks.Peek() != closingKind)
                {
                    problems.Add(new VbaFormDesignerEvidenceProblem(
                        VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                        sourceText.RangeForLine(line, 0, line.Text.Length)));
                }
                else
                {
                    blocks.Pop();
                }

                continue;
            }

            if (blocks.Count == 0
                && root is not null
                && PropertyAssignmentCandidatePattern().IsMatch(line.Text))
            {
                problems.Add(new VbaFormDesignerEvidenceProblem(
                    VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                    sourceText.RangeForLine(line, 0, line.Text.Length)));
                continue;
            }

            var referenceMatch = ResourceReferencePattern().Match(line.Text);
            if (referenceMatch.Success)
            {
                if (blocks.Count == 0)
                {
                    problems.Add(new VbaFormDesignerEvidenceProblem(
                        VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                        sourceText.RangeForLine(line, 0, line.Text.Length)));
                    continue;
                }

                var file = referenceMatch.Groups["file"];
                if (!file.Value.EndsWith(
                        ".frx",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsSafeSidecarFileName(file.Value))
                {
                    problems.Add(new VbaFormDesignerEvidenceProblem(
                        VbaFormDesignerEvidenceProblemKind.ResourceReferenceUnsafe,
                        sourceText.RangeForLine(
                            line,
                            file.Index,
                            file.Index + file.Length),
                        file.Value));
                    continue;
                }

                references.Add(new VbaFormDesignerResourceReference(
                    referenceMatch.Groups["property"].Value,
                    file.Value,
                    referenceMatch.Groups["offset"].Value,
                    sourceText.RangeForLine(
                        line,
                        file.Index,
                        file.Index + file.Length)));
                continue;
            }

            if (ResourceReferenceCandidatePattern().IsMatch(line.Text))
            {
                problems.Add(new VbaFormDesignerEvidenceProblem(
                    VbaFormDesignerEvidenceProblemKind.ResourceReferenceMalformed,
                    sourceText.RangeForLine(line, 0, line.Text.Length),
                    line.Text.Trim()));
            }
        }

        if (blocks.Count > 0)
        {
            problems.Add(new VbaFormDesignerEvidenceProblem(
                VbaFormDesignerEvidenceProblemKind.StructureMalformed,
                range));
        }

        if (root is null)
        {
            problems.Add(new VbaFormDesignerEvidenceProblem(
                VbaFormDesignerEvidenceProblemKind.RootMissing,
                range));
        }

        return new VbaFormDesignerBlock(rawText, range)
        {
            Root = root,
            ResourceReferences = references.ToArray(),
            EvidenceProblems = problems.ToArray()
        };
    }

    private static bool IsSafeSidecarFileName(string value)
        => value.Length > ".frx".Length
            && !Path.IsPathRooted(value)
            && !value.Contains('/')
            && !value.Contains('\\')
            && !value.Contains(':')
            && !value.Equals("..", StringComparison.Ordinal)
            && Path.GetFileName(value).Equals(value, StringComparison.Ordinal);

    private static bool StartsWithStructuralKeyword(
        string value,
        string keyword)
    {
        var trimmed = VbaIdentifier.TrimStartWhitespace(value);
        return trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == keyword.Length
                || VbaIdentifier.IsWhitespace(trimmed[keyword.Length]));
    }
}
