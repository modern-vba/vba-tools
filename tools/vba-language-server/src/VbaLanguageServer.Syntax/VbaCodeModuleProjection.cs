namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies how one exported-source physical line participates in a generated VBIDE code module.
/// </summary>
public enum VbaCodeModuleLineRole
{
    /// <summary>
    /// A physical line retained in the generated code module.
    /// </summary>
    Code,

    /// <summary>
    /// An export-only Attribute line omitted by VBIDE import.
    /// </summary>
    ExportAttribute,

    /// <summary>
    /// An export-only class header omitted by VBIDE import.
    /// </summary>
    ClassMetadata,

    /// <summary>
    /// An export-only form designer line omitted by VBIDE import.
    /// </summary>
    FormDesigner,

    /// <summary>
    /// The synthetic empty source line created when the file ends with a newline.
    /// </summary>
    TerminalNewline
}

/// <summary>
/// Classifies whether one exact physical source line can conservatively represent executable code.
/// </summary>
public enum VbaPhysicalLineExecutionKind
{
    /// <summary>
    /// At least one statement on the physical line is an executable candidate.
    /// </summary>
    ExecutableCandidate,

    /// <summary>
    /// The physical line contains no source text other than whitespace.
    /// </summary>
    Blank,

    /// <summary>
    /// The physical line contains only a comment.
    /// </summary>
    Comment,

    /// <summary>
    /// Every statement on the physical line is declarative.
    /// </summary>
    DeclarationOnly,

    /// <summary>
    /// The physical line opens or closes a procedure and is not executable by itself.
    /// </summary>
    ProcedureBoundary,

    /// <summary>
    /// The physical line continues a logical statement that began on an earlier line.
    /// </summary>
    Continuation,

    /// <summary>
    /// The physical line contains only a line label.
    /// </summary>
    LabelOnly,

    /// <summary>
    /// The physical line is a conditional-compilation directive.
    /// </summary>
    Directive,

    /// <summary>
    /// The physical line is export-only metadata rather than code-module text.
    /// </summary>
    ExportMetadata,

    /// <summary>
    /// Parser diagnostics prevent the line from being classified safely.
    /// </summary>
    Malformed,

    /// <summary>
    /// The syntax core cannot prove that the physical line is executable.
    /// </summary>
    Unknown
}

/// <summary>
/// Maps one exported-source physical line to its generated code-module position and syntax facts.
/// </summary>
/// <param name="SourceLine">The zero-based exported-source physical line.</param>
/// <param name="CodeModuleLine">The one-based VBIDE code-module line, or null for export-only metadata.</param>
/// <param name="Text">The exact physical source text without a newline.</param>
/// <param name="Role">How the line participates in VBIDE import.</param>
/// <param name="ExecutionKind">The conservative physical-line execution classification.</param>
/// <param name="ConditionalCompilationPath">The structural conditional-compilation branch path, when it can be proven.</param>
public sealed record VbaCodeModuleLineProjection(
    int SourceLine,
    int? CodeModuleLine,
    string Text,
    VbaCodeModuleLineRole Role,
    VbaPhysicalLineExecutionKind ExecutionKind,
    VbaConditionalCompilationBranchPath? ConditionalCompilationPath);

/// <summary>
/// Projects exported VBA source lines onto the corresponding generated VBIDE code module.
/// </summary>
/// <param name="ModuleName">The exported module identity.</param>
/// <param name="ModuleKind">The exported module kind.</param>
/// <param name="Lines">All exported-source physical lines in source order.</param>
public sealed record VbaCodeModuleProjection(
    string ModuleName,
    VbaModuleKind ModuleKind,
    IReadOnlyList<VbaCodeModuleLineProjection> Lines)
{
    /// <summary>
    /// Gets every exact line produced in the imported VBIDE code module.
    /// </summary>
    /// <remarks>
    /// VBIDE inserts one leading empty code-module line when importing an exported UserForm.
    /// That synthetic line has no exported-source physical-line identity.
    /// </remarks>
    public IReadOnlyList<string> CodeModuleLines
        => ModuleKind == VbaModuleKind.FormModule
            ? new[] { string.Empty }
                .Concat(Lines
                    .Where(line => line.Role == VbaCodeModuleLineRole.Code)
                    .OrderBy(line => line.CodeModuleLine)
                    .Select(line => line.Text))
                .ToArray()
            : Lines
                .Where(line => line.Role == VbaCodeModuleLineRole.Code)
                .OrderBy(line => line.CodeModuleLine)
                .Select(line => line.Text)
                .ToArray();

    /// <summary>
    /// Creates a source-range-preserving code-module projection from a parsed syntax tree.
    /// </summary>
    public static VbaCodeModuleProjection Create(VbaSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var sourceText = tree.SourceText;
        var tokensByLine = BuildTokensByLine(sourceText.Lines.Count, tree.TokenStream.Tokens);
        var statementsByStartLine = VbaLogicalStatementSpan
            .Build(tree.Text.Length, tree.TokenStream.Tokens, includeEmptyStatements: false)
            .GroupBy(statement => statement.Range.Start.Line)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<VbaLogicalStatementSpan>)group.ToArray());
        var continuationLines = tree.TokenStream.Tokens
            .Where(token => token.Kind == VbaTokenKind.LineContinuation)
            .Select(token => token.Range.Start.Line + 1)
            .Where(line => line < sourceText.Lines.Count)
            .ToHashSet();
        var directiveLines = tree.TokenStream.Tokens
            .Where(token => token.Kind == VbaTokenKind.PreprocessorDirective)
            .SelectMany(token => Enumerable.Range(
                token.Range.Start.Line,
                token.Range.End.Line - token.Range.Start.Line + 1))
            .ToHashSet();
        var objectCodeStartLine = FindObjectCodeStartLine(tree);
        var projectedLines = new List<VbaCodeModuleLineProjection>(sourceText.Lines.Count);
        var codeModuleLine = tree.Module.Kind == VbaModuleKind.FormModule ? 1 : 0;

        foreach (var line in sourceText.Lines)
        {
            var role = GetLineRole(
                tree.Module.Kind,
                line,
                objectCodeStartLine,
                tokensByLine[line.LineNumber],
                IsTerminalNewline(sourceText, line));
            int? projectedLine = null;
            if (role == VbaCodeModuleLineRole.Code)
            {
                projectedLine = ++codeModuleLine;
            }

            var conditionalPath = TryGetConditionalCompilationPath(tree, line);
            var executionKind = ClassifyExecution(
                tree,
                line,
                role,
                tokensByLine[line.LineNumber],
                statementsByStartLine,
                continuationLines,
                directiveLines);
            projectedLines.Add(new VbaCodeModuleLineProjection(
                line.LineNumber,
                projectedLine,
                line.Text,
                role,
                executionKind,
                conditionalPath));
        }

        return new VbaCodeModuleProjection(
            tree.Module.Identity.Name,
            tree.Module.Kind,
            projectedLines.AsReadOnly());
    }

    private static IReadOnlyList<VbaToken>[] BuildTokensByLine(
        int lineCount,
        IReadOnlyList<VbaToken> tokens)
    {
        var result = Enumerable.Range(0, lineCount)
            .Select(_ => new List<VbaToken>())
            .ToArray();
        foreach (var token in tokens)
        {
            var finalLine = Math.Min(token.Range.End.Line, lineCount - 1);
            for (var line = Math.Max(0, token.Range.Start.Line); line <= finalLine; line++)
            {
                result[line].Add(token);
            }
        }

        return result
            .Select(lineTokens => (IReadOnlyList<VbaToken>)lineTokens.AsReadOnly())
            .ToArray();
    }

    private static int FindObjectCodeStartLine(VbaSyntaxTree tree)
    {
        if (tree.Module.Kind == VbaModuleKind.StandardModule)
        {
            return 0;
        }

        var nameAttribute = tree.Module.Attributes.FirstOrDefault(attribute =>
            attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase));
        return nameAttribute?.Range.Start.Line ?? tree.Module.CodeStartLine;
    }

    private static VbaCodeModuleLineRole GetLineRole(
        VbaModuleKind moduleKind,
        VbaSourceLine line,
        int objectCodeStartLine,
        IReadOnlyList<VbaToken> lineTokens,
        bool isTerminalNewline)
    {
        if (isTerminalNewline)
        {
            return VbaCodeModuleLineRole.TerminalNewline;
        }

        if (moduleKind != VbaModuleKind.StandardModule && line.LineNumber < objectCodeStartLine)
        {
            return moduleKind == VbaModuleKind.FormModule
                ? VbaCodeModuleLineRole.FormDesigner
                : VbaCodeModuleLineRole.ClassMetadata;
        }

        return StartsWithWord(SignificantTokens(lineTokens), "Attribute")
            ? VbaCodeModuleLineRole.ExportAttribute
            : VbaCodeModuleLineRole.Code;
    }

    private static bool IsTerminalNewline(VbaSourceText sourceText, VbaSourceLine line)
        => sourceText.Text.Length > 0
            && line.LineNumber == sourceText.Lines.Count - 1
            && line.StartOffset == sourceText.Text.Length
            && line.EndOffset == sourceText.Text.Length
            && sourceText.Text[^1] is '\r' or '\n';

    private static VbaConditionalCompilationBranchPath? TryGetConditionalCompilationPath(
        VbaSyntaxTree tree,
        VbaSourceLine line)
    {
        var range = tree.SourceText.RangeForLine(line, 0, line.Text.Length);
        return VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            range,
            requireCompleteStructure: true,
            out var path)
                ? path
                : null;
    }

    private static VbaPhysicalLineExecutionKind ClassifyExecution(
        VbaSyntaxTree tree,
        VbaSourceLine line,
        VbaCodeModuleLineRole role,
        IReadOnlyList<VbaToken> lineTokens,
        IReadOnlyDictionary<int, IReadOnlyList<VbaLogicalStatementSpan>> statementsByStartLine,
        IReadOnlySet<int> continuationLines,
        IReadOnlySet<int> directiveLines)
    {
        if (role == VbaCodeModuleLineRole.TerminalNewline)
        {
            return VbaPhysicalLineExecutionKind.Blank;
        }

        if (role != VbaCodeModuleLineRole.Code)
        {
            return VbaPhysicalLineExecutionKind.ExportMetadata;
        }

        if (directiveLines.Contains(line.LineNumber))
        {
            return VbaPhysicalLineExecutionKind.Directive;
        }

        if (continuationLines.Contains(line.LineNumber))
        {
            return VbaPhysicalLineExecutionKind.Continuation;
        }

        if (string.IsNullOrWhiteSpace(line.Text))
        {
            return VbaPhysicalLineExecutionKind.Blank;
        }

        var significantLineTokens = SignificantTokens(lineTokens);
        if (significantLineTokens.Count == 0 || IsCommentOnly(line.Text, significantLineTokens))
        {
            return VbaPhysicalLineExecutionKind.Comment;
        }

        if (tree.Diagnostics.Any(diagnostic =>
            diagnostic.Range.Start.Line <= line.LineNumber
            && line.LineNumber <= diagnostic.Range.End.Line))
        {
            return VbaPhysicalLineExecutionKind.Malformed;
        }

        if (!statementsByStartLine.TryGetValue(line.LineNumber, out var statements)
            || statements.Count == 0)
        {
            return VbaPhysicalLineExecutionKind.Unknown;
        }

        var segmentKinds = statements.Select(statement => ClassifyStatement(tree, statement)).ToArray();
        if (segmentKinds.Contains(VbaPhysicalLineExecutionKind.ExecutableCandidate))
        {
            return VbaPhysicalLineExecutionKind.ExecutableCandidate;
        }

        if (segmentKinds.Contains(VbaPhysicalLineExecutionKind.Malformed))
        {
            return VbaPhysicalLineExecutionKind.Malformed;
        }

        if (segmentKinds.Contains(VbaPhysicalLineExecutionKind.Unknown))
        {
            return VbaPhysicalLineExecutionKind.Unknown;
        }

        if (segmentKinds.All(kind => kind == VbaPhysicalLineExecutionKind.LabelOnly))
        {
            return VbaPhysicalLineExecutionKind.LabelOnly;
        }

        if (segmentKinds.Any(kind => kind == VbaPhysicalLineExecutionKind.DeclarationOnly))
        {
            return VbaPhysicalLineExecutionKind.DeclarationOnly;
        }

        return VbaPhysicalLineExecutionKind.ProcedureBoundary;
    }

    private static VbaPhysicalLineExecutionKind ClassifyStatement(
        VbaSyntaxTree tree,
        VbaLogicalStatementSpan statement)
    {
        IReadOnlyList<VbaToken> tokens = statement.SignificantTokens;
        if (tokens.Count == 0)
        {
            return VbaPhysicalLineExecutionKind.Unknown;
        }

        if (statement.EndsWithColon && tokens.Count == 1 && IsLabelToken(tokens[0]))
        {
            return VbaPhysicalLineExecutionKind.LabelOnly;
        }

        if (HasFirstColumnLineNumber(tokens))
        {
            tokens = tokens.Skip(1).ToArray();
            if (tokens.Count == 0)
            {
                return VbaPhysicalLineExecutionKind.LabelOnly;
            }

            if (statement.EndsWithColon && tokens.Count == 1 && IsLabelToken(tokens[0]))
            {
                return VbaPhysicalLineExecutionKind.LabelOnly;
            }
        }

        if (IsProcedureBoundary(tokens))
        {
            return VbaPhysicalLineExecutionKind.ProcedureBoundary;
        }

        if (IsDeclaration(tokens))
        {
            return VbaPhysicalLineExecutionKind.DeclarationOnly;
        }

        if (IsInsideDeclarationBlock(tree.Module.Members, statement.Range))
        {
            return VbaPhysicalLineExecutionKind.DeclarationOnly;
        }

        if (!IsInsideCallableBlock(tree.Module.CallableDeclarations, statement.Range))
        {
            return VbaPhysicalLineExecutionKind.Unknown;
        }

        return IsExecutableCandidate(tokens, tree.Module.Kind)
            ? VbaPhysicalLineExecutionKind.ExecutableCandidate
            : VbaPhysicalLineExecutionKind.Unknown;
    }

    private static bool IsInsideDeclarationBlock(
        IReadOnlyList<VbaModuleMemberSyntax> members,
        VbaSyntaxRange range)
        => members.Any(member =>
            member.Kind is VbaDeclarationKind.Enum or VbaDeclarationKind.Type
            && Contains(member.BlockRange, range));

    private static bool IsInsideCallableBlock(
        IReadOnlyList<VbaCallableDeclarationSyntax> declarations,
        VbaSyntaxRange range)
        => declarations.Any(declaration =>
            !declaration.IsExternal
            && Contains(declaration.BlockRange, range));

    private static bool Contains(VbaSyntaxRange outer, VbaSyntaxRange inner)
        => outer.Start.Offset <= inner.Start.Offset
            && inner.End.Offset <= outer.End.Offset;

    private static bool IsCommentOnly(string text, IReadOnlyList<VbaToken> tokens)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("'", StringComparison.Ordinal)
            || StartsWithWord(tokens, "Rem");
    }

    private static bool IsProcedureBoundary(IReadOnlyList<VbaToken> tokens)
    {
        var index = SkipVisibilityModifiers(tokens, 0);
        if (index < tokens.Count && IsWord(tokens[index], "Static"))
        {
            index++;
        }

        if (index < tokens.Count
            && (IsWord(tokens[index], "Sub")
                || IsWord(tokens[index], "Function")
                || IsWord(tokens[index], "Property")))
        {
            return true;
        }

        return tokens.Count >= 2
            && IsWord(tokens[0], "End")
            && (IsWord(tokens[1], "Sub")
                || IsWord(tokens[1], "Function")
                || IsWord(tokens[1], "Property"));
    }

    private static bool IsDeclaration(IReadOnlyList<VbaToken> tokens)
    {
        if (StartsWithAnyWord(tokens, "Option", "Dim", "Const", "Implements", "Event", "Declare"))
        {
            return true;
        }

        if (tokens.Count >= 2
            && IsWord(tokens[0], "End")
            && (IsWord(tokens[1], "Enum") || IsWord(tokens[1], "Type")))
        {
            return true;
        }

        var index = SkipVisibilityModifiers(tokens, 0);
        if (index >= tokens.Count)
        {
            return true;
        }

        if (IsWord(tokens[index], "Static"))
        {
            index++;
            if (index >= tokens.Count)
            {
                return true;
            }
        }

        return IsWord(tokens[index], "Const")
            || IsWord(tokens[index], "Declare")
            || IsWord(tokens[index], "Event")
            || IsWord(tokens[index], "Enum")
            || IsWord(tokens[index], "Type")
            || index > 0;
    }

    private static bool IsExecutableCandidate(
        IReadOnlyList<VbaToken> tokens,
        VbaModuleKind moduleKind)
    {
        if (tokens.Count == 0 || tokens[0].Kind == VbaTokenKind.PreprocessorDirective)
        {
            return false;
        }

        if (tokens.Any(token => token.Kind == VbaTokenKind.Operator && token.Text == "="))
        {
            return true;
        }

        return StartsWithAnyWord(
                tokens,
                "Call",
                "Case",
                "Debug",
                "Do",
                "Else",
                "ElseIf",
                "End",
                "Erase",
                "Exit",
                "For",
                "Get",
                "GoSub",
                "GoTo",
                "If",
                "Let",
                "Load",
                "Loop",
                "Next",
                "On",
                "RaiseEvent",
                "ReDim",
                "Resume",
                "Select",
                "Set",
                "Stop",
                "Unload",
                "Wend",
                "While",
                "With")
            || (moduleKind is VbaModuleKind.ClassModule or VbaModuleKind.FormModule
                && IsWord(tokens[0], "Me"))
            || tokens[0].Kind == VbaTokenKind.Identifier
            || IsWord(tokens[0], ".");
    }

    private static bool HasFirstColumnLineNumber(IReadOnlyList<VbaToken> tokens)
        => tokens.Count > 0
            && tokens[0].Kind == VbaTokenKind.NumericLiteral
            && tokens[0].Range.Start.Character == 0;

    private static int SkipVisibilityModifiers(IReadOnlyList<VbaToken> tokens, int index)
    {
        while (index < tokens.Count
            && (IsWord(tokens[index], "Public")
                || IsWord(tokens[index], "Private")
                || IsWord(tokens[index], "Friend")
                || IsWord(tokens[index], "Global")))
        {
            index++;
        }

        return index;
    }

    private static bool IsLabelToken(VbaToken token)
        => token.Kind is VbaTokenKind.Identifier or VbaTokenKind.NumericLiteral;

    private static IReadOnlyList<VbaToken> SignificantTokens(IReadOnlyList<VbaToken> tokens)
        => tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.Comment
                and not VbaTokenKind.LineContinuation)
            .ToArray();

    private static bool StartsWithAnyWord(
        IReadOnlyList<VbaToken> tokens,
        params string[] words)
        => tokens.Count > 0 && words.Any(word => IsWord(tokens[0], word));

    private static bool StartsWithWord(IReadOnlyList<VbaToken> tokens, string word)
        => tokens.Count > 0 && IsWord(tokens[0], word);

    private static bool IsWord(VbaToken token, string word)
        => token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}
