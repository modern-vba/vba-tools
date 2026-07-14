using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SyntaxDiagnosticsTests
{
    [Fact]
    public void Diagnostics_match_existing_typescript_invalid_trailing_comment_continuation_case()
    {
        const string invalidLine = "        \"needle\", _ ' comment";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    ReadValue( _",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.invalidTrailingCommentContinuation", diagnostic.Code);
        Assert.Equal("Code line-continuation marker cannot be followed by a comment.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(new VbaRange(new VbaPosition(5, invalidLine.IndexOf('_')), new VbaPosition(5, invalidLine.Length)), diagnostic.Range);
    }

    [Fact]
    public void Diagnostics_accept_block_if_condition_split_by_explicit_line_continuation()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    For Each line_str In lines_arr",
            "        If (Not has_msg_str And LenB(StrConv(line_str, vbFromUnicode)) <= PageSize) _",
            "                Or (has_msg_str And LenB(StrConv(msg_str, vbFromUnicode)) + 2 + LenB(StrConv(line_str, vbFromUnicode)) <= PageSize) Then",
            "            If Not has_msg_str Then",
            "                msg_str = line_str",
            "                has_msg_str = True",
            "            Else",
            "                msg_str = msg_str & vbCrLf & line_str",
            "            End If",
            "        Else",
            "            If has_msg_str Then",
            "                Call msgs_list.Add(msg_str)",
            "            End If",
            "        End If",
            "    Next line_str",
            "End Sub"
        ]);

        Assert.Empty(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));
    }

    [Fact]
    public void Diagnostics_match_existing_typescript_unterminated_string_case()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.unterminatedStringLiteral", diagnostic.Code);
        Assert.Equal("String literal is missing a closing double quote.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(new VbaRange(new VbaPosition(4, invalidLine.IndexOf('"')), new VbaPosition(4, invalidLine.Length)), diagnostic.Range);
    }

    [Fact]
    public void Document_diagnostics_publish_existing_syntax_diagnostics_after_collector_split()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.unterminatedStringLiteral", diagnostic.Code);
        Assert.Equal("String literal is missing a closing double quote.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(new VbaRange(new VbaPosition(2, invalidLine.IndexOf('"')), new VbaPosition(2, invalidLine.Length)), diagnostic.Range);
    }

    [Fact]
    public void Document_validation_collector_accepts_syntax_tree_and_document_uri()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("Worker.bas", source);

        Assert.Empty(VbaDocumentValidationDiagnosticCollector.Collect(tree, "Worker.bas"));
    }

    [Fact]
    public void Diagnostic_pipeline_preserves_syntax_and_document_validation_categories()
    {
        const string invalidLine = "    value = \"unterminated";
        const string declarationLine = "Public Sub Run(ByVal name As String, ByVal name As Long)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declarationLine,
            invalidLine,
            "End Sub"
        ]);
        var tree = VbaSyntaxTree.ParseModule("Worker.bas", source);

        var result = VbaDiagnosticPipeline.CollectDocument(tree, "Worker.bas");

        var syntaxDiagnostic = Assert.Single(result.SyntaxDiagnostics);
        Assert.Equal("syntax.unterminatedStringLiteral", syntaxDiagnostic.Code);
        var validationDiagnostic = Assert.Single(result.DocumentValidationDiagnostics);
        Assert.Equal("validation.duplicateCallableParameterName", validationDiagnostic.Code);
        Assert.Empty(result.ProjectValidationDiagnostics);
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public void Document_diagnostics_report_duplicate_callable_parameter_names()
    {
        const string declarationLine = "Public Sub Run(ByVal name As String, ByVal name As Long)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declarationLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("validation.duplicateCallableParameterName", diagnostic.Code);
        Assert.Equal("Duplicate callable parameter name 'name'.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(1, declarationLine.LastIndexOf("name", StringComparison.Ordinal)),
                new VbaPosition(1, declarationLine.LastIndexOf("name", StringComparison.Ordinal) + "name".Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Document_diagnostics_report_duplicate_array_callable_parameter_names()
    {
        const string declarationLine = "Public Sub Run(ByVal Name() As String, ByVal name As Long)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            declarationLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("validation.duplicateCallableParameterName", diagnostic.Code);
        Assert.Equal("Duplicate callable parameter name 'name'.", diagnostic.Message);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(1, declarationLine.LastIndexOf("name", StringComparison.Ordinal)),
                new VbaPosition(1, declarationLine.LastIndexOf("name", StringComparison.Ordinal) + "name".Length)),
            diagnostic.Range);
    }

    [Theory]
    [InlineData("Public Function Build(ByVal value As String, ByVal VALUE As Long) As String", "End Function")]
    [InlineData("Public Property Get DisplayName(ByVal value As String, ByVal VALUE As Long) As String", "End Property")]
    [InlineData("Public Property Let DisplayName(ByVal value As String, ByVal VALUE As Long)", "End Property")]
    [InlineData("Public Property Set DisplayName(ByVal value As Object, ByVal VALUE As Object)", "End Property")]
    [InlineData("Public Event Saved(ByVal value As String, ByVal VALUE As Long)", null)]
    [InlineData("Public Declare PtrSafe Function GetTickCount Lib \"kernel32\" (ByVal value As String, ByVal VALUE As Long) As Long", null)]
    public void Document_diagnostics_report_case_insensitive_duplicate_callable_parameter_names(
        string declarationLine,
        string? terminatorLine)
    {
        var sourceLines = new List<string>
        {
            "Attribute VB_Name = \"Worker\"",
            declarationLine
        };
        if (terminatorLine is not null)
        {
            sourceLines.Add(terminatorLine);
        }

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(string.Join('\n', sourceLines), "Worker.bas"));

        Assert.Equal("validation.duplicateCallableParameterName", diagnostic.Code);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(1, declarationLine.LastIndexOf("VALUE", StringComparison.Ordinal)),
                new VbaPosition(1, declarationLine.LastIndexOf("VALUE", StringComparison.Ordinal) + "VALUE".Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Document_diagnostics_do_not_report_unique_callable_parameter_names()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run(ByVal name As String, ByVal count As Long)",
            "End Sub"
        ]);

        Assert.DoesNotContain(
            VbaDocumentDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
    }

    [Fact]
    public void Document_diagnostics_publish_validation_diagnostics_with_syntax_diagnostics()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run(ByVal name As String, ByVal name As Long)",
            invalidLine,
            "End Sub"
        ]);

        var diagnostics = VbaDocumentDiagnostics.Collect(source, "Worker.bas");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "validation.duplicateCallableParameterName");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }

    [Fact]
    public void Document_diagnostics_report_duplicate_named_call_arguments()
    {
        const string callLine = "    Example(Arg1:=1, ARG1:=2)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("validation.duplicateNamedCallArgument", diagnostic.Code);
        Assert.Equal("Duplicate named call argument 'ARG1'.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(2, callLine.IndexOf("ARG1", StringComparison.Ordinal)),
                new VbaPosition(2, callLine.IndexOf("ARG1", StringComparison.Ordinal) + "ARG1".Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Document_diagnostics_validate_nested_named_call_argument_lists_independently()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(Arg1:=Nested(Arg1:=1, ARG1:=2), Arg2:=3)",
            "End Sub"
        ]);

        var diagnostic = Assert.Single(
            VbaDocumentDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "validation.duplicateNamedCallArgument");

        Assert.Equal("ARG1", SourceTextAtRange(source, diagnostic.Range));
    }

    [Fact]
    public void Document_diagnostics_do_not_report_unique_named_call_arguments()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, Arg2:=Nested(Arg1:=2, Arg2:=3))",
            "End Sub"
        ]);

        Assert.DoesNotContain(
            VbaDocumentDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "validation.duplicateNamedCallArgument");
    }

    [Fact]
    public void Document_diagnostics_publish_duplicate_named_call_argument_with_syntax_diagnostics()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, ARG1:=2)",
            invalidLine,
            "End Sub"
        ]);

        var diagnostics = VbaDocumentDiagnostics.Collect(source, "Worker.bas");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "validation.duplicateNamedCallArgument");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }

    [Fact]
    public void Document_diagnostics_report_positional_call_argument_after_named_argument()
    {
        const string callLine = "    Example(Arg1:=1, 2)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("validation.positionalCallArgumentAfterNamed", diagnostic.Code);
        Assert.Equal("Positional call argument cannot appear after a named argument.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(2, callLine.IndexOf("2", StringComparison.Ordinal)),
                new VbaPosition(2, callLine.IndexOf("2", StringComparison.Ordinal) + "2".Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Document_diagnostics_report_omitted_call_argument_after_named_argument()
    {
        const string callLine = "    Example(1, Arg2:=2,)";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaDocumentDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("validation.positionalCallArgumentAfterNamed", diagnostic.Code);
        Assert.Equal(2, diagnostic.Range.Start.Line);
        Assert.Equal(callLine.LastIndexOf(','), diagnostic.Range.Start.Character);
        Assert.True(diagnostic.Range.End.Character > diagnostic.Range.Start.Character);
    }

    [Fact]
    public void Document_diagnostics_validate_nested_argument_order_independently()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(Arg1:=Nested(Arg1:=1, 2), Arg2:=3)",
            "End Sub"
        ]);

        var diagnostic = Assert.Single(
            VbaDocumentDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "validation.positionalCallArgumentAfterNamed");

        Assert.Equal("2", SourceTextAtRange(source, diagnostic.Range));
    }

    [Theory]
    [InlineData("    Example(1, 2, 3)")]
    [InlineData("    Example(Arg1:=1, Arg2:=2)")]
    [InlineData("    Example(1, Arg2:=2)")]
    [InlineData("    Example(, Arg2:=2)")]
    public void Document_diagnostics_do_not_report_valid_named_argument_order(string callLine)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            callLine,
            "End Sub"
        ]);

        Assert.DoesNotContain(
            VbaDocumentDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "validation.positionalCallArgumentAfterNamed");
    }

    [Fact]
    public void Document_diagnostics_publish_positional_after_named_with_syntax_diagnostics()
    {
        const string invalidLine = "    value = \"unterminated";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Example(Arg1:=1, 2)",
            invalidLine,
            "End Sub"
        ]);

        var diagnostics = VbaDocumentDiagnostics.Collect(source, "Worker.bas");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "validation.positionalCallArgumentAfterNamed");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }

    [Fact]
    public void Diagnostics_cover_cls_and_frm_code_while_ignoring_frm_designer_text()
    {
        const string classInvalidLine = "    value = \"unterminated";
        const string formInvalidLine = "    value = \"unterminated";
        var classSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            classInvalidLine,
            "End Sub"
        ]);
        var formSource = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"designer text is not code",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            formInvalidLine,
            "End Sub"
        ]);

        var classDiagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(classSource, "Worker.cls"));
        var formDiagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(formSource, "Dialog.frm"));

        Assert.Equal(new VbaRange(new VbaPosition(5, classInvalidLine.IndexOf('"')), new VbaPosition(5, classInvalidLine.Length)), classDiagnostic.Range);
        Assert.Equal(new VbaRange(new VbaPosition(8, formInvalidLine.IndexOf('"')), new VbaPosition(8, formInvalidLine.Length)), formDiagnostic.Range);
    }

    [Fact]
    public void Diagnostics_reject_parenthesis_free_raise_event_arguments()
    {
        const string invalidLine = "    RaiseEvent Saved \"ok\"";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String)",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.raiseEventArgumentListRequiresParentheses", diagnostic.Code);
        Assert.Equal("RaiseEvent arguments must be enclosed in parentheses.", diagnostic.Message);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal("vba-language-server", diagnostic.Source);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(3, invalidLine.IndexOf("\"ok\"", StringComparison.Ordinal)),
                new VbaPosition(3, invalidLine.Length)),
            diagnostic.Range);
    }

    [Fact]
    public void Diagnostics_reject_parenthesis_free_raise_event_multiple_arguments()
    {
        const string invalidLine = "    RaiseEvent Saved \"ok\", 1";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String, ByVal count As Long)",
            "Public Sub Run()",
            invalidLine,
            "End Sub"
        ]);

        var diagnostic = Assert.Single(VbaSyntaxDiagnostics.Collect(source, "Worker.bas"));

        Assert.Equal("syntax.raiseEventArgumentListRequiresParentheses", diagnostic.Code);
        Assert.Equal(
            new VbaRange(
                new VbaPosition(3, invalidLine.IndexOf("\"ok\", 1", StringComparison.Ordinal)),
                new VbaPosition(3, invalidLine.Length)),
            diagnostic.Range);
    }

    [Theory]
    [InlineData("    RaiseEvent Saved")]
    [InlineData("    RaiseEvent Saved()")]
    [InlineData("    RaiseEvent Saved(\"ok\")")]
    [InlineData("    Example \"ok\", 1")]
    public void Diagnostics_do_not_reject_valid_raise_event_or_ordinary_call_shapes(string statementLine)
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved(ByVal message As String)",
            "Public Sub Run()",
            statementLine,
            "End Sub"
        ]);

        Assert.DoesNotContain(
            VbaSyntaxDiagnostics.Collect(source, "Worker.bas"),
            diagnostic => diagnostic.Code == "syntax.raiseEventArgumentListRequiresParentheses");
    }

    [Fact]
    public void Valid_representative_sources_return_no_syntax_diagnostics()
    {
        var basSource = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Dim ordinary_identifier As String",
            "    ordinary_identifier = \"a \"\"quoted\"\" value\"",
            "    value = 1 ' #not-a-date# \"unterminated `",
            "    Rem #not-a-date# \"unterminated `",
            "End Sub"
        ]);
        var clsSource = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Debug.Print \"ok\"",
            "End Sub"
        ]);
        var frmSource = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "  Caption = \"designer text is not code",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Option Explicit",
            "",
            "Public Sub Run()",
            "    Debug.Print \"ok\"",
            "End Sub"
        ]);

        Assert.Empty(VbaSyntaxDiagnostics.Collect(basSource, "Worker.bas"));
        Assert.Empty(VbaSyntaxDiagnostics.Collect(clsSource, "Worker.cls"));
        Assert.Empty(VbaSyntaxDiagnostics.Collect(frmSource, "Dialog.frm"));
    }

    [Fact]
    public void Diagnostics_include_parser_recovery_without_semantic_name_checks()
    {
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function () As String",
            "    value = \"unterminated",
            "    ReadValue _ ' bad continuation",
            "    @",
            "Public Sub Run()",
            "    If ready Then",
            "        MissingIdentifier"
        ]);

        var diagnostics = VbaSyntaxDiagnostics.Collect(source, "Worker.bas");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.malformedDeclarationHeader");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.invalidTrailingCommentContinuation");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "syntax.unexpectedStatementBoundaryToken");
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End If", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "syntax.missingBlockTerminator"
            && diagnostic.Message.Contains("End Sub", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
    }

    private static string SourceTextAtRange(string source, VbaRange range)
    {
        var line = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')[range.Start.Line];
        return line[range.Start.Character..range.End.Character];
    }
}
