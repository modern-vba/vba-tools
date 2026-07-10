using System.Text.RegularExpressions;

namespace VbaLanguageServer.Syntax;

internal static class VbaSyntaxTreeParser
{
    private static readonly Regex AttributePattern = new(
        "^\\s*Attribute\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?<value>.+?)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OptionPattern = new(
        "^\\s*Option\\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static VbaSyntaxTree ParseModule(string uri, string source)
    {
        var tokenStream = VbaTokenStream.FromText(source);
        var sourceText = SourceText.From(source);
        var kind = GetModuleKind(uri);
        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var codeStartLine = 0;
        VbaFormDesignerBlock? designerBlock = null;

        if (kind == VbaModuleKind.FormModule)
        {
            var boundaryLine = FindAttributeNameLine(sourceText);
            if (boundaryLine is null)
            {
                designerBlock = new VbaFormDesignerBlock(source, sourceText.FullRange);
                diagnostics.Add(new VbaSyntaxDiagnostic(
                    "syntax.formCodeSectionBoundaryMissing",
                    "Form module is missing an Attribute VB_Name code-section boundary.",
                    sourceText.FullRange));
                codeStartLine = sourceText.Lines.Count;
            }
            else
            {
                codeStartLine = boundaryLine.LineNumber;
                var boundaryStart = sourceText.PositionAt(boundaryLine.StartOffset);
                designerBlock = new VbaFormDesignerBlock(
                    source[..boundaryLine.StartOffset],
                    new VbaSyntaxRange(sourceText.StartPosition, boundaryStart));
            }
        }

        var attributes = ParseAttributes(sourceText, codeStartLine);
        var options = ParseOptions(sourceText, codeStartLine);
        var identity = CreateIdentity(uri, sourceText, kind, attributes);
        var module = new VbaModuleSyntax(
            kind,
            identity,
            attributes,
            options,
            designerBlock,
            sourceText.FullRange);
        return new VbaSyntaxTree(uri, source, tokenStream, module, diagnostics);
    }

    private static IReadOnlyList<VbaModuleAttributeSyntax> ParseAttributes(SourceText sourceText, int startLine)
    {
        var attributes = new List<VbaModuleAttributeSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            var line = sourceText.Lines[index];
            var match = AttributePattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var nameGroup = match.Groups["name"];
            var valueGroup = match.Groups["value"];
            var rawValue = valueGroup.Value.Trim();
            var value = UnquoteAttributeValue(rawValue);
            var valueOffsetInGroup = valueGroup.Value.IndexOf(value, StringComparison.Ordinal);
            var valueStartCharacter = valueGroup.Index + Math.Max(0, valueOffsetInGroup);
            attributes.Add(new VbaModuleAttributeSyntax(
                nameGroup.Value,
                value,
                sourceText.RangeForLine(line, match.Index, match.Index + match.Length),
                sourceText.RangeForLine(line, nameGroup.Index, nameGroup.Index + nameGroup.Length),
                sourceText.RangeForLine(line, valueStartCharacter, valueStartCharacter + value.Length)));
        }

        return attributes;
    }

    private static IReadOnlyList<VbaModuleOptionSyntax> ParseOptions(SourceText sourceText, int startLine)
    {
        var options = new List<VbaModuleOptionSyntax>();
        for (var index = startLine; index < sourceText.Lines.Count; index++)
        {
            var line = sourceText.Lines[index];
            var match = OptionPattern.Match(line.Text);
            if (!match.Success)
            {
                continue;
            }

            var text = match.Value.Trim();
            var startCharacter = line.Text.IndexOf(text, StringComparison.Ordinal);
            options.Add(new VbaModuleOptionSyntax(
                text,
                sourceText.RangeForLine(line, startCharacter, startCharacter + text.Length)));
        }

        return options;
    }

    private static VbaModuleIdentitySyntax CreateIdentity(
        string uri,
        SourceText sourceText,
        VbaModuleKind kind,
        IReadOnlyList<VbaModuleAttributeSyntax> attributes)
    {
        var nameAttribute = attributes.FirstOrDefault(attribute =>
            attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));
        if (nameAttribute is not null)
        {
            return new VbaModuleIdentitySyntax(nameAttribute.Value, nameAttribute.ValueRange);
        }

        var fallbackName = GetFileBaseName(uri);
        return new VbaModuleIdentitySyntax(
            fallbackName,
            new VbaSyntaxRange(sourceText.StartPosition, sourceText.StartPosition));
    }

    private static SourceLine? FindAttributeNameLine(SourceText sourceText)
        => sourceText.Lines.FirstOrDefault(line =>
            AttributePattern.Match(line.Text) is { Success: true } match
            && match.Groups["name"].Value.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));

    private static string UnquoteAttributeValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static VbaModuleKind GetModuleKind(string uri)
    {
        if (uri.EndsWith(".cls", StringComparison.OrdinalIgnoreCase))
        {
            return VbaModuleKind.ClassModule;
        }

        if (uri.EndsWith(".frm", StringComparison.OrdinalIgnoreCase))
        {
            return VbaModuleKind.FormModule;
        }

        return VbaModuleKind.StandardModule;
    }

    private static string GetFileBaseName(string uri)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(new Uri(uri).LocalPath);
        }
        catch (UriFormatException)
        {
            var separator = Math.Max(uri.LastIndexOf('/'), uri.LastIndexOf('\\'));
            var fileName = separator < 0 ? uri : uri[(separator + 1)..];
            var extension = fileName.LastIndexOf('.');
            return extension <= 0 ? fileName : fileName[..extension];
        }
    }

    private sealed record SourceText(
        string Text,
        IReadOnlyList<SourceLine> Lines,
        VbaSyntaxPosition StartPosition,
        VbaSyntaxRange FullRange)
    {
        public bool IsEmpty => Text.Length == 0;

        public static SourceText From(string source)
        {
            var lines = new List<SourceLine>();
            var line = 0;
            var offset = 0;
            while (offset <= source.Length)
            {
                var startOffset = offset;
                while (offset < source.Length && source[offset] is not '\r' and not '\n')
                {
                    offset++;
                }

                lines.Add(new SourceLine(line, source[startOffset..offset], startOffset, offset));
                if (offset >= source.Length)
                {
                    break;
                }

                if (source[offset] == '\r' && offset + 1 < source.Length && source[offset + 1] == '\n')
                {
                    offset += 2;
                }
                else
                {
                    offset++;
                }

                line++;
            }

            var startPosition = new VbaSyntaxPosition(0, 0, 0);
            var endPosition = PositionAt(source, source.Length);
            return new SourceText(source, lines, startPosition, new VbaSyntaxRange(startPosition, endPosition));
        }

        public VbaSyntaxPosition PositionAt(int offset)
            => PositionAt(Text, offset);

        public VbaSyntaxRange RangeForLine(SourceLine line, int startCharacter, int endCharacter)
            => new(
                new VbaSyntaxPosition(line.LineNumber, startCharacter, line.StartOffset + startCharacter),
                new VbaSyntaxPosition(line.LineNumber, endCharacter, line.StartOffset + endCharacter));

        private static VbaSyntaxPosition PositionAt(string source, int offset)
        {
            var line = 0;
            var character = 0;
            for (var index = 0; index < offset; index++)
            {
                if (source[index] == '\r')
                {
                    if (index + 1 < source.Length && source[index + 1] == '\n')
                    {
                        index++;
                    }

                    line++;
                    character = 0;
                    continue;
                }

                if (source[index] == '\n')
                {
                    line++;
                    character = 0;
                    continue;
                }

                character++;
            }

            return new VbaSyntaxPosition(line, character, offset);
        }
    }
}

internal sealed record SourceLine(
    int LineNumber,
    string Text,
    int StartOffset,
    int EndOffset);
