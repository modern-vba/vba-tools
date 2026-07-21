using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies one enabled ordinary source breakpoint captured for a debug launch.
/// </summary>
/// <param name="SourcePath">The absolute exported-source path.</param>
/// <param name="EditorLine">The zero-based physical editor line.</param>
public sealed record DebugSourceBreakpoint(string SourcePath, int EditorLine);

/// <summary>
/// Identifies one source breakpoint mapped to an exact generated VBIDE line.
/// </summary>
/// <param name="Source">The captured source breakpoint.</param>
/// <param name="ModuleName">The generated VBA module identity.</param>
/// <param name="VbideLine">The one-based VBIDE code-module line.</param>
/// <param name="ExpectedCodeLine">The exact code line expected in the generated module.</param>
public sealed record VbeBreakpoint(
    DebugSourceBreakpoint Source,
    string ModuleName,
    int VbideLine,
    string ExpectedCodeLine);

/// <summary>
/// Maps saved exported-source breakpoint positions to exact VBIDE code-module positions.
/// </summary>
public interface IBreakpointSourceMapper
{
    /// <summary>
    /// Maps one captured source breakpoint without relocating it to another source line.
    /// </summary>
    VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint);
}

/// <summary>
/// Maps the initial supported standard-module breakpoint path.
/// </summary>
public sealed class BreakpointSourceMapper : IBreakpointSourceMapper
{
    /// <inheritdoc />
    public VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint)
    {
        if (!Path.IsPathFullyQualified(breakpoint.SourcePath))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source path must be absolute: '{breakpoint.SourcePath}'.");
        }

        var sourcePath = Path.GetFullPath(breakpoint.SourcePath);
        var sourceMatches = snapshot.Sources
            .Where(source => Path.GetFullPath(source.Path).Equals(
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Debug breakpoint source '{breakpoint.SourcePath}' is not present in the saved source snapshot."
                : $"Debug breakpoint source '{breakpoint.SourcePath}' is ambiguous in the saved source snapshot.");
        }

        var source = sourceMatches[0];
        if (!Path.GetExtension(source.Path).Equals(".bas", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"Initial breakpoint transfer supports standard .bas sources only: '{source.Path}'.");
        }

        var syntaxTree = VbaSyntaxTree.ParseModule(new Uri(source.Path).AbsoluteUri, source.Text);
        if (syntaxTree.Module.Kind != VbaModuleKind.StandardModule)
        {
            throw new DebugSetupException(
                $"Initial breakpoint transfer supports standard .bas modules only: '{source.Path}'.");
        }

        if (breakpoint.EditorLine < 0 || breakpoint.EditorLine >= syntaxTree.SourceText.Lines.Count)
        {
            throw new DebugSetupException(
                $"Debug breakpoint line {breakpoint.EditorLine} is outside '{source.Path}'.");
        }

        var statementMatches = syntaxTree.Module.Statements
            .Where(statement =>
                statement.Range.Start.Line == breakpoint.EditorLine
                && statement.Range.End.Line == breakpoint.EditorLine
                && !statement.IsMalformed
                && statement.Kind is VbaStatementKind.Assignment or VbaStatementKind.Call)
            .ToArray();
        if (statementMatches.Length != 1)
        {
            throw new DebugSetupException(
                $"Debug breakpoint at '{source.Path}:{breakpoint.EditorLine}' does not map exactly to " +
                "one supported executable statement.");
        }

        var recognizedAttributeLines = syntaxTree.Module.Attributes
            .Select(attribute => attribute.Range.Start.Line)
            .Where(line => line < breakpoint.EditorLine)
            .Distinct()
            .ToHashSet();
        var hasUnrecognizedAttribute = syntaxTree.TokenStream.Tokens
            .Where(token =>
                token.Range.Start.Line < breakpoint.EditorLine &&
                token.Kind is not VbaTokenKind.Whitespace and not VbaTokenKind.NewLine)
            .GroupBy(token => token.Range.Start.Line)
            .Any(lineTokens =>
                lineTokens.First().Text.Equals("Attribute", StringComparison.OrdinalIgnoreCase) &&
                !recognizedAttributeLines.Contains(lineTokens.Key));
        if (hasUnrecognizedAttribute)
        {
            throw new DebugSetupException(
                $"Debug breakpoint at '{source.Path}:{breakpoint.EditorLine}' cannot map exactly because " +
                "an export-only Attribute before it is not represented by the initial .bas source map.");
        }

        var removedAttributeLines = recognizedAttributeLines
            .Count();
        var vbideLine = breakpoint.EditorLine + 1 - removedAttributeLines;
        var expectedCodeLine = syntaxTree.SourceText.Lines[breakpoint.EditorLine].Text;
        return new VbeBreakpoint(
            breakpoint,
            syntaxTree.Module.Identity.Name,
            vbideLine,
            expectedCodeLine);
    }
}
