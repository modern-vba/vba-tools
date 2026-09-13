namespace VbaTools.Syntax;

/// <summary>
/// Detects indentation from code-module text while excluding export-only source records.
/// </summary>
public static class VbaIndentationDetection
{
    /// <summary>
    /// Resolves indentation evidence against configured defaults without changing source text.
    /// </summary>
    public static VbaIndentationStyle Detect(
        VbaSyntaxTree tree,
        VbaIndentationStyle configuredStyle)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(configuredStyle);
        var projection = VbaCodeModuleProjection.Create(tree);
        var formatting = VbaFormattingInput.FromSyntaxTree(tree);
        if (!formatting.CanApplyIndentation)
        {
            return configuredStyle;
        }

        var widths = new List<int>();
        var hasTabs = false;
        foreach (var line in projection.Lines)
        {
            var facts = formatting.Lines[line.SourceLine];
            if (line.Role != VbaCodeModuleLineRole.Code
                || facts.IndentationDepth <= 0
                || line.ExecutionKind is VbaPhysicalLineExecutionKind.Blank
                    or VbaPhysicalLineExecutionKind.Comment
                    or VbaPhysicalLineExecutionKind.Continuation
                    or VbaPhysicalLineExecutionKind.LabelOnly
                    or VbaPhysicalLineExecutionKind.Directive)
            {
                continue;
            }

            var prefix = line.Text[..(line.Text.Length - VbaIdentifier.TrimStartWhitespace(line.Text).Length)];
            if (prefix.Length == 0)
            {
                continue;
            }

            var spaces = prefix.Count(character => character == ' ');
            var tabs = prefix.Count(character => character == '\t');
            if ((spaces > 0 && tabs > 0)
                || spaces + tabs != prefix.Length
                || (spaces > 0 && spaces % facts.IndentationDepth != 0))
            {
                return configuredStyle;
            }

            if (spaces > 0)
            {
                widths.Add(spaces / facts.IndentationDepth);
            }

            hasTabs |= tabs > 0;
        }

        if (hasTabs)
        {
            return widths.Count > 0
                ? configuredStyle
                : VbaIndentationStyle.FromEditorOptions(false, configuredStyle.IndentSize);
        }

        return widths.Count == 0 || widths.Distinct().Count() != 1
            ? configuredStyle
            : VbaIndentationStyle.FromEditorOptions(true, widths[0]);
    }
}
