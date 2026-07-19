namespace VbaLanguageServer.Syntax;

/// <summary>
/// Tokenizes VBA source text into source-range-preserving lexical tokens.
/// </summary>
internal static class VbaLexer
{
    /// <summary>
    /// Produces the lexical token stream for a VBA source document.
    /// </summary>
    /// <param name="source">The source text to tokenize.</param>
    /// <returns>The token stream in source order.</returns>
    public static VbaTokenStream Tokenize(string source)
        => Tokenize(VbaSourceText.From(source));

    /// <summary>
    /// Produces the lexical token stream from an indexed source snapshot.
    /// </summary>
    /// <param name="sourceText">The indexed source snapshot to tokenize.</param>
    /// <returns>The token stream in source order.</returns>
    public static VbaTokenStream Tokenize(VbaSourceText sourceText)
    {
        var state = new LexerState(sourceText);
        var estimatedTokenCapacity = (int)Math.Max(
            4L,
            Math.Min(
                sourceText.Text.Length,
                ((long)sourceText.Lines.Count * 2) + 64));
        var tokens = new List<VbaToken>(estimatedTokenCapacity);

        while (!state.IsAtEnd)
        {
            if (state.Current is '\r' or '\n')
            {
                tokens.Add(ReadNewLine(state));
                continue;
            }

            if (char.IsWhiteSpace(state.Current))
            {
                tokens.Add(ReadWhitespace(state));
                continue;
            }

            if (IsPreprocessorDirectiveStart(state))
            {
                tokens.Add(ReadPreprocessorDirective(state));
                continue;
            }

            if (state.Current == '\'')
            {
                tokens.Add(ReadUntilLineEnd(state, VbaTokenKind.Comment));
                continue;
            }

            if (state.Current == '"')
            {
                tokens.Add(ReadStringLiteral(state));
                continue;
            }

            if (state.Current == '#' && TryReadDateLiteral(state, out var dateLiteral))
            {
                tokens.Add(dateLiteral);
                continue;
            }

            if (char.IsAsciiDigit(state.Current))
            {
                tokens.Add(ReadNumericLiteral(state));
                continue;
            }

            if (state.Current == '_' && IsLineContinuation(state))
            {
                tokens.Add(ReadFixedLength(state, VbaTokenKind.LineContinuation, 1));
                continue;
            }

            if (IsIdentifierStart(state.Current))
            {
                tokens.Add(ReadIdentifierOrKeyword(state));
                continue;
            }

            if (TryReadOperator(state, out var operatorToken))
            {
                tokens.Add(operatorToken);
                continue;
            }

            tokens.Add(ReadFixedLength(state, VbaTokenKind.Punctuation, 1));
        }

        return new VbaTokenStream(tokens);
    }

    private static VbaToken ReadNewLine(LexerState state)
    {
        var start = state.Position;
        if (state.Current == '\r' && state.Peek(1) == '\n')
        {
            state.Advance();
            var end = state.Position;
            return new VbaToken(VbaTokenKind.NewLine, "\r\n", new VbaSyntaxRange(start, end));
        }

        var text = state.Current == '\r' ? "\r" : "\n";
        state.Advance();
        var singleCharacterEnd = state.Position;
        return new VbaToken(
            VbaTokenKind.NewLine,
            text,
            new VbaSyntaxRange(start, singleCharacterEnd));
    }

    private static VbaToken ReadWhitespace(LexerState state)
    {
        var start = state.Position;
        var containsOnlySpaces = true;
        while (!state.IsAtEnd && char.IsWhiteSpace(state.Current) && state.Current is not '\r' and not '\n')
        {
            if (state.Current == ' ')
            {
                state.AdvanceSpaces();
                continue;
            }

            containsOnlySpaces = false;
            state.Advance();
        }

        var end = state.Position;
        return new VbaToken(
            VbaTokenKind.Whitespace,
            state.SliceWhitespace(start.Offset, end.Offset, containsOnlySpaces),
            new VbaSyntaxRange(start, end));
    }

    private static VbaToken ReadUntilLineEnd(LexerState state, VbaTokenKind kind)
    {
        var start = state.Position;
        while (!state.IsAtEnd && state.Current is not '\r' and not '\n')
        {
            state.Advance();
        }

        return CreateToken(state, kind, start);
    }

    private static VbaToken ReadPreprocessorDirective(LexerState state)
    {
        var start = state.Position;
        while (!state.IsAtEnd)
        {
            var physicalLineStart = state.Position.Offset;
            while (!state.IsAtEnd && state.Current is not '\r' and not '\n')
            {
                state.Advance();
            }

            if (!HasDirectiveLineContinuation(
                    state.Source,
                    physicalLineStart,
                    state.Position.Offset)
                || state.IsAtEnd)
            {
                break;
            }

            state.Advance();
        }

        return CreateToken(state, VbaTokenKind.PreprocessorDirective, start);
    }

    private static bool HasDirectiveLineContinuation(
        string source,
        int startOffset,
        int endOffset)
    {
        var inString = false;
        for (var offset = startOffset; offset < endOffset; offset++)
        {
            if (source[offset] == '"')
            {
                if (inString
                    && offset + 1 < endOffset
                    && source[offset + 1] == '"')
                {
                    offset++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (source[offset] == '\'' && !inString)
            {
                return false;
            }
        }

        if (inString)
        {
            return false;
        }

        var continuationOffset = endOffset - 1;
        while (continuationOffset >= startOffset
            && source[continuationOffset] is ' ' or '\t')
        {
            continuationOffset--;
        }

        return continuationOffset > startOffset
            && source[continuationOffset] == '_'
            && source[continuationOffset - 1] is ' ' or '\t';
    }

    private static VbaToken ReadStringLiteral(LexerState state)
    {
        var start = state.Position;
        state.Advance();
        while (!state.IsAtEnd && state.Current is not '\r' and not '\n')
        {
            if (state.Current != '"')
            {
                state.Advance();
                continue;
            }

            state.Advance();
            if (!state.IsAtEnd && state.Current == '"')
            {
                state.Advance();
                continue;
            }

            break;
        }

        return CreateToken(state, VbaTokenKind.StringLiteral, start);
    }

    private static bool TryReadDateLiteral(LexerState state, out VbaToken token)
    {
        token = default!;
        var start = state.Position;
        state.Advance();
        while (!state.IsAtEnd && state.Current is not '\r' and not '\n')
        {
            if (state.Current == '#')
            {
                state.Advance();
                token = CreateToken(state, VbaTokenKind.DateLiteral, start);
                return true;
            }

            state.Advance();
        }

        state.Rewind(start);
        return false;
    }

    private static VbaToken ReadNumericLiteral(LexerState state)
    {
        var start = state.Position;
        while (!state.IsAtEnd && char.IsAsciiDigit(state.Current))
        {
            state.Advance();
        }

        if (!state.IsAtEnd && state.Current == '.' && char.IsAsciiDigit(state.Peek(1)))
        {
            state.Advance();
            while (!state.IsAtEnd && char.IsAsciiDigit(state.Current))
            {
                state.Advance();
            }
        }

        return CreateToken(state, VbaTokenKind.NumericLiteral, start);
    }

    private static VbaToken ReadIdentifierOrKeyword(LexerState state)
    {
        var start = state.Position;
        state.Advance();
        while (!state.IsAtEnd && IsIdentifierCharacter(state.Current))
        {
            state.Advance();
        }

        var text = state.Slice(start.Offset, state.Position.Offset);
        var kind = VbaLanguageVocabulary.IsKeyword(text)
            ? VbaTokenKind.Keyword
            : VbaTokenKind.Identifier;
        var end = state.Position;
        return new VbaToken(kind, text, new VbaSyntaxRange(start, end));
    }

    private static bool TryReadOperator(LexerState state, out VbaToken token)
    {
        token = default!;
        var twoCharacters = state.IsAtEnd || state.Peek(1) == '\0'
            ? ""
            : state.Source.Substring(state.Position.Offset, 2);
        if (twoCharacters is "<=" or ">=" or "<>" or ":=")
        {
            token = ReadFixedLength(state, VbaTokenKind.Operator, 2);
            return true;
        }

        if (state.Current is '+' or '-' or '*' or '/' or '\\' or '^' or '&' or '=' or '<' or '>')
        {
            token = ReadFixedLength(state, VbaTokenKind.Operator, 1);
            return true;
        }

        return false;
    }

    private static VbaToken ReadFixedLength(LexerState state, VbaTokenKind kind, int length)
    {
        var start = state.Position;
        for (var index = 0; index < length; index++)
        {
            state.Advance();
        }

        return CreateToken(state, kind, start);
    }

    private static VbaToken CreateToken(LexerState state, VbaTokenKind kind, VbaSyntaxPosition start)
    {
        var end = state.Position;
        return new VbaToken(
            kind,
            state.Slice(start.Offset, end.Offset),
            new VbaSyntaxRange(start, end));
    }

    private static bool IsPreprocessorDirectiveStart(LexerState state)
        => state.Current == '#' && IsFirstNonWhitespaceOnLine(state);

    private static bool IsFirstNonWhitespaceOnLine(LexerState state)
    {
        for (var index = state.Position.Offset - 1; index >= 0; index--)
        {
            var current = state.Source[index];
            if (current is '\r' or '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLineContinuation(LexerState state)
    {
        if (state.Position.Offset == 0 || !char.IsWhiteSpace(state.Source[state.Position.Offset - 1]))
        {
            return false;
        }

        for (var offset = state.Position.Offset + 1; offset < state.Source.Length; offset++)
        {
            var current = state.Source[offset];
            if (current is '\r' or '\n')
            {
                return true;
            }

            if (current == '\'')
            {
                return true;
            }

            if (!char.IsWhiteSpace(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifierStart(char value)
        => char.IsAsciiLetter(value) || value == '_';

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value == '_';

    private sealed class LexerState
    {
        private const int MaximumCachedSpaceCount = 64;
        private readonly string?[] spacesByLength =
            new string?[MaximumCachedSpaceCount + 1];

        /// <summary>
        /// Initializes a lexer cursor at the beginning of the supplied source.
        /// </summary>
        /// <param name="source">The source text to tokenize.</param>
        public LexerState(VbaSourceText sourceText)
        {
            SourceText = sourceText;
            line = sourceText.StartPosition.Line;
            character = sourceText.StartPosition.Character;
            offset = sourceText.StartPosition.Offset;
        }

        private VbaSourceText SourceText { get; }
        private int line;
        private int character;
        private int offset;
        private VbaSyntaxPosition? cachedPosition;

        /// <summary>
        /// Gets the source text being tokenized.
        /// </summary>
        public string Source => SourceText.Text;

        /// <summary>
        /// Gets the current line, character, and offset of the lexer cursor.
        /// </summary>
        public VbaSyntaxPosition Position
            => cachedPosition ??= new VbaSyntaxPosition(line, character, offset);

        /// <summary>
        /// Gets whether the lexer cursor has reached the end of the source text.
        /// </summary>
        public bool IsAtEnd => offset >= Source.Length;

        /// <summary>
        /// Gets the current source character, or a null character sentinel at end of source.
        /// </summary>
        public char Current => IsAtEnd ? '\0' : Source[offset];

        /// <summary>
        /// Returns a character relative to the current cursor position.
        /// </summary>
        /// <param name="distance">The number of characters to look ahead from the current offset.</param>
        /// <returns>The requested character, or a null character sentinel beyond the end of source.</returns>
        public char Peek(int distance)
        {
            var requestedOffset = offset + distance;
            return requestedOffset >= Source.Length
                ? '\0'
                : Source[requestedOffset];
        }

        /// <summary>
        /// Returns a source substring by absolute offsets.
        /// </summary>
        /// <param name="startOffset">The inclusive zero-based start offset.</param>
        /// <param name="endOffset">The exclusive zero-based end offset.</param>
        /// <returns>The requested source slice.</returns>
        public string Slice(int startOffset, int endOffset)
            => Source[startOffset..endOffset];

        public string SliceWhitespace(
            int startOffset,
            int endOffset,
            bool containsOnlySpaces)
        {
            var length = endOffset - startOffset;
            if (!containsOnlySpaces || length > MaximumCachedSpaceCount)
            {
                return Slice(startOffset, endOffset);
            }

            return spacesByLength[length] ??= new string(' ', length);
        }

        /// <summary>
        /// Advances the cursor by one source character, normalizing CRLF to a single line break.
        /// </summary>
        public void Advance()
        {
            if (IsAtEnd)
            {
                return;
            }

            if (Source[offset] == '\r')
            {
                cachedPosition = null;
                line++;
                character = 0;
                offset += Peek(1) == '\n' ? 2 : 1;
                return;
            }

            if (Source[offset] == '\n')
            {
                cachedPosition = null;
                line++;
                character = 0;
                offset++;
                return;
            }

            cachedPosition = null;
            character++;
            offset++;
        }

        /// <summary>
        /// Advances over a contiguous run of ordinary spaces without changing lines.
        /// </summary>
        public void AdvanceSpaces()
        {
            var remaining = Source.AsSpan(offset);
            var nonSpaceOffset = remaining.IndexOfAnyExcept(' ');
            var length = nonSpaceOffset < 0
                ? remaining.Length
                : nonSpaceOffset;
            cachedPosition = null;
            character += length;
            offset += length;
        }

        /// <summary>
        /// Restores the lexer cursor to a previously captured position.
        /// </summary>
        /// <param name="position">The position to restore.</param>
        public void Rewind(VbaSyntaxPosition position)
        {
            line = position.Line;
            character = position.Character;
            offset = position.Offset;
            cachedPosition = position;
        }
    }
}
