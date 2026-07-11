namespace VbaLanguageServer.Syntax;

internal static class VbaLexer
{
    public static VbaTokenStream Tokenize(string source)
    {
        var state = new LexerState(source);
        var tokens = new List<VbaToken>();

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
                tokens.Add(ReadUntilLineEnd(state, VbaTokenKind.PreprocessorDirective));
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
            state.Advance();
            return new VbaToken(VbaTokenKind.NewLine, "\r\n", new VbaSyntaxRange(start, state.Position));
        }

        var text = state.Current.ToString();
        state.Advance();
        return new VbaToken(VbaTokenKind.NewLine, text, new VbaSyntaxRange(start, state.Position));
    }

    private static VbaToken ReadWhitespace(LexerState state)
    {
        var start = state.Position;
        while (!state.IsAtEnd && char.IsWhiteSpace(state.Current) && state.Current is not '\r' and not '\n')
        {
            state.Advance();
        }

        return CreateToken(state, VbaTokenKind.Whitespace, start);
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
        return new VbaToken(kind, text, new VbaSyntaxRange(start, state.Position));
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
        => new(kind, state.Slice(start.Offset, state.Position.Offset), new VbaSyntaxRange(start, state.Position));

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
        public LexerState(string source)
        {
            Source = source;
            Position = new VbaSyntaxPosition(0, 0, 0);
        }

        public string Source { get; }

        public VbaSyntaxPosition Position { get; private set; }

        public bool IsAtEnd => Position.Offset >= Source.Length;

        public char Current => IsAtEnd ? '\0' : Source[Position.Offset];

        public char Peek(int distance)
        {
            var offset = Position.Offset + distance;
            return offset >= Source.Length ? '\0' : Source[offset];
        }

        public string Slice(int startOffset, int endOffset)
            => Source[startOffset..endOffset];

        public void Advance()
        {
            if (IsAtEnd)
            {
                return;
            }

            var current = Source[Position.Offset];
            if (current == '\r')
            {
                if (Peek(1) == '\n')
                {
                    Position = Position with { Offset = Position.Offset + 1 };
                }

                Position = new VbaSyntaxPosition(Position.Line + 1, 0, Position.Offset + 1);
                return;
            }

            if (current == '\n')
            {
                Position = new VbaSyntaxPosition(Position.Line + 1, 0, Position.Offset + 1);
                return;
            }

            Position = new VbaSyntaxPosition(Position.Line, Position.Character + 1, Position.Offset + 1);
        }

        public void Rewind(VbaSyntaxPosition position)
        {
            Position = position;
        }
    }
}
