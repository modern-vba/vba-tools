using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaBlockHeaderSyntaxTests
{
    [Theory]
    [InlineData("file:///C:/work/Module1.bas", "Enum State", VbaBlockHeaderKind.Enum, "End Enum")]
    [InlineData("file:///C:/work/Module1.bas", "Public Enum State   ' keep", VbaBlockHeaderKind.Enum, "End Enum")]
    [InlineData("file:///C:/work/Module1.bas", "Type Record", VbaBlockHeaderKind.Type, "End Type")]
    [InlineData("file:///C:/work/Module1.bas", "Public Type Record", VbaBlockHeaderKind.Type, "End Type")]
    [InlineData("file:///C:/work/Module1.bas", "  pRiVaTe tYpE Record", VbaBlockHeaderKind.Type, "End Type")]
    [InlineData("file:///C:/work/Worker.cls", "Enum State", VbaBlockHeaderKind.Enum, "End Enum")]
    [InlineData("file:///C:/work/Worker.cls", "Public Enum State", VbaBlockHeaderKind.Enum, "End Enum")]
    [InlineData("file:///C:/work/Worker.cls", "Private Enum State", VbaBlockHeaderKind.Enum, "End Enum")]
    [InlineData("file:///C:/work/Worker.cls", "Private Type Record", VbaBlockHeaderKind.Type, "End Type")]
    public void Complete_eligible_module_declaration_headers_are_accepted(
        string uri,
        string headerLine,
        VbaBlockHeaderKind expectedKind,
        string expectedTerminator)
    {
        var tree = VbaSyntaxTree.ParseModule(uri, headerLine);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 0, headerLine.Length);

        Assert.NotNull(header);
        Assert.Equal(expectedKind, header.Kind);
        Assert.Equal(expectedTerminator, header.ExpectedTerminator);
        Assert.Equal(
            headerLine[..headerLine.TakeWhile(value => value is ' ' or '\t').Count()],
            header.LeadingWhitespace);
    }

    [Theory]
    [InlineData("Enum", "End Enum")]
    [InlineData("Type", "End Type")]
    public void Complete_private_form_module_declaration_headers_are_accepted(
        string declarationKeyword,
        string expectedTerminator)
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            $"Private {declarationKeyword} State"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 4, lines[4].Length);

        Assert.NotNull(header);
        Assert.Equal(expectedTerminator, header.ExpectedTerminator);
    }

    [Theory]
    [InlineData("Enum State")]
    [InlineData("Public Enum State")]
    public void Public_and_default_form_module_enum_headers_are_accepted(string headerLine)
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            headerLine
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 4, headerLine.Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.Enum, header.Kind);
        Assert.Equal("End Enum", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_continued_module_declaration_preserves_first_line_indentation_and_trivia()
    {
        var lines = new[]
        {
            "\tPrivate _",
            "        Type _",
            "    Record   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Record.cls",
            string.Join('\n', lines));

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 0, lines[0].Length));
        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 1, lines[1].Length));
        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.Type, header.Kind);
        Assert.Equal(0, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("\t", header.LeadingWhitespace);
        Assert.Equal("End Type", header.ExpectedTerminator);
    }

    [Theory]
    [InlineData("file:///C:/work/Module1.bas", "Public Enum", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Enum State Extra", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Static Enum State", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Friend Enum State", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Global Enum State", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Friend Type Record", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Global Type Record", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Type Record", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Public Type Record", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Private Type Record:", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Sub Main()\n    Private Enum State\nEnd Sub", 1)]
    [InlineData("file:///C:/work/Module1.bas", "Private Type Outer\n    Private Enum Inner\nEnd Type", 1)]
    [InlineData("file:///C:/work/Module1.bas", "Public Sub Main()\nEnd Sub\nPublic Enum State", 2)]
    [InlineData("file:///C:/work/Module1.bas", "Public Function Value() As Long\nEnd Function\nPrivate Type Record", 2)]
    [InlineData("file:///C:/work/Module1.bas", "#If VBA7 Then\nPublic Enum State\n#End If", 1)]
    [InlineData("file:///C:/work/Module1.bas", "Public Enum State _\n", 0)]
    [InlineData("file:///C:/work/Dialog.frm", "Private Type Record", 0)]
    public void Incomplete_illegal_and_structurally_ambiguous_module_declarations_are_rejected(
        string uri,
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule(uri, source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Form_designer_text_before_the_code_section_is_not_a_module_declaration_header()
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "    Private Type Record",
            "End",
            "Attribute VB_Name = \"Dialog\""
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length);

        Assert.Null(header);
    }

    [Theory]
    [InlineData("Type Record")]
    [InlineData("Public Type Record")]
    public void Non_private_form_module_type_declarations_are_rejected(string headerLine)
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            headerLine
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 4, headerLine.Length);

        Assert.Null(header);
    }

    [Theory]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Function Build() As String",
        VbaBlockHeaderKind.Function,
        "End Function")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Global Static Function Build(Optional ByVal prefix As String = \"x\") As String",
        VbaBlockHeaderKind.Function,
        "End Function")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Friend Property Get Value() As Long",
        VbaBlockHeaderKind.PropertyGet,
        "End Property")]
    [InlineData(
        "file:///C:/work/Module1.bas",
        "Public Property Get Value() As Long",
        VbaBlockHeaderKind.PropertyGet,
        "End Property")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Private Property Let Value(ByVal assignedValue As Long)",
        VbaBlockHeaderKind.PropertyLet,
        "End Property")]
    [InlineData(
        "file:///C:/work/Worker.cls",
        "Public Property Set Value(ByVal assignedValue As Object)",
        VbaBlockHeaderKind.PropertySet,
        "End Property")]
    public void Complete_eligible_function_and_property_headers_are_accepted(
        string uri,
        string headerLine,
        VbaBlockHeaderKind expectedKind,
        string expectedTerminator)
    {
        var tree = VbaSyntaxTree.ParseModule(uri, headerLine);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 0, headerLine.Length);

        Assert.NotNull(header);
        Assert.Equal(expectedKind, header.Kind);
        Assert.Equal(expectedTerminator, header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_form_property_header_after_the_designer_section_is_accepted()
    {
        var lines = new[]
        {
            "VERSION 5.00",
            "Begin VB.Form Dialog",
            "End",
            "Attribute VB_Name = \"Dialog\"",
            "Public Property Get CaptionText() As String"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Dialog.frm",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 4, lines[4].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.PropertyGet, header.Kind);
        Assert.Equal("End Property", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_continued_function_is_eligible_only_on_its_final_physical_line()
    {
        var lines = new[]
        {
            "\tPrivate Static Function Build( _",
            "        ByVal value As Long _",
            "    ) As String   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 0, lines[0].Length));
        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 1, lines[1].Length));
        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.Function, header.Kind);
        Assert.Equal(0, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("\t", header.LeadingWhitespace);
        Assert.Equal("End Function", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_continued_property_let_is_eligible_only_on_its_final_physical_line()
    {
        var lines = new[]
        {
            "\tPublic Property Let Value( _",
            "        ByVal index As Long, _",
            "        ByVal assignedValue As Long)   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.cls",
            string.Join('\n', lines));

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 0, lines[0].Length));
        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 1, lines[1].Length));
        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.PropertyLet, header.Kind);
        Assert.Equal(0, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("\t", header.LeadingWhitespace);
        Assert.Equal("End Property", header.ExpectedTerminator);
    }

    [Theory]
    [InlineData(
        "Public Sub Existing()\nEnd Sub\nPublic Function Build() As Long",
        2,
        VbaBlockHeaderKind.Function)]
    [InlineData(
        "Public Property Get Existing() As Long\nEnd Property\nPublic Property Let Existing(ByVal assignedValue As Long)",
        2,
        VbaBlockHeaderKind.PropertyLet)]
    public void Complete_callable_headers_after_a_finished_callable_are_accepted(
        string source,
        int line,
        VbaBlockHeaderKind expectedKind)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.cls", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.NotNull(header);
        Assert.Equal(expectedKind, header.Kind);
    }

    [Theory]
    [InlineData("file:///C:/work/Worker.cls", "Public Event Changed(ByVal value As Long)", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Declare Sub Run Lib \"library\" ()", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Private Declare PtrSafe Function Read Lib \"library\" () As Long", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Friend Function Build() As String", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Global Function Build() As String", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Friend Property Get Value() As Long", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Global Property Get Value() As Long", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Function Build() As", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Public Property Value() As Long", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Public Property Let Value()", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Public Property Set Value(ByVal assignedValue As Long)", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Function Build() As Long _", 0)]
    [InlineData("file:///C:/work/Worker.cls", "Public Property Get Value() As Long _", 0)]
    [InlineData("file:///C:/work/Module1.bas", "Public Sub Outer()\n    Public Function Inner() As Long", 1)]
    [InlineData("file:///C:/work/Module1.bas", "Public Function Build() As String:", 0)]
    public void Excluded_illegal_incomplete_and_nested_callable_headers_are_rejected(
        string uri,
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule(uri, source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Complete_with_expression_inside_a_callable_body_is_accepted()
    {
        const string source = "Public Sub Main()\n    With target.Parent";
        var line = source.Split('\n')[1];
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, line.Length);

        Assert.NotNull(header);
        Assert.Equal("End With", header.ExpectedTerminator);
        Assert.Equal("    ", header.LeadingWhitespace);
    }

    [Theory]
    [InlineData("    With New Class1")]
    [InlineData("    With New Project1.Class1")]
    public void Complete_with_new_class_expression_is_accepted(string headerLine)
    {
        var source = $"Public Sub Main()\n{headerLine}";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, headerLine.Length);

        Assert.NotNull(header);
        Assert.Equal("End With", header.ExpectedTerminator);
    }

    [Theory]
    [InlineData("    With Date")]
    [InlineData("    With Date.Value")]
    [InlineData("    With String(2, \"x\")")]
    [InlineData("    With String(2, \"x\")!Value")]
    [InlineData("    With Len(value)")]
    [InlineData("    With Strings.Len(value)")]
    [InlineData("    With VBA.Strings.Len(value)")]
    [InlineData("    With VBA.Date.Value")]
    [InlineData("    With VBA.Array(1)")]
    [InlineData("    With VBA.String(2, \"x\")")]
    [InlineData("    With VBA.Information.Err")]
    public void Complete_statically_eligible_intrinsic_with_expression_is_accepted(string headerLine)
    {
        var source = $"Public Sub Main()\n{headerLine}";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, headerLine.Length);

        Assert.NotNull(header);
        Assert.Equal("End With", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_continued_with_preserves_first_line_indentation_and_trivia()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "    With Worksheets( _",
            "        \"Sheet1\")   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length);

        Assert.NotNull(header);
        Assert.Equal(1, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("    ", header.LeadingWhitespace);
        Assert.Equal("End With", header.ExpectedTerminator);
    }

    [Fact]
    public void Continued_with_is_eligible_only_at_its_final_physical_line()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "    With Worksheets( _",
            "        \"Sheet1\")"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 1, lines[1].Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length));
    }

    [Fact]
    public void Leading_member_with_expression_requires_an_enclosing_with_block()
    {
        const string outside = "Public Sub Main()\n    With .Font";
        const string outsideDictionary = "Public Sub Main()\n    With !Child";
        const string inside = "Public Sub Main()\n    With target\n        With ! Child";
        var outsideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", outside);
        var outsideDictionaryTree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            outsideDictionary);
        var insideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", inside);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(
            outsideTree,
            1,
            outside.Split('\n')[1].Length));
        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(
            outsideDictionaryTree,
            1,
            outsideDictionary.Split('\n')[1].Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(
            insideTree,
            2,
            inside.Split('\n')[2].Length));
    }

    [Theory]
    [InlineData("With target", 0)]
    [InlineData("Public Sub Main()\n    With", 1)]
    [InlineData("Public Sub Main()\n    With target +", 1)]
    [InlineData("Public Sub Main()\n    With True()", 1)]
    [InlineData("Public Sub Main()\n    With 1", 1)]
    [InlineData("Public Sub Main()\n    With \"text\"", 1)]
    [InlineData("Public Sub Main()\n    With #1/1/2020#", 1)]
    [InlineData("Public Sub Main()\n    With True", 1)]
    [InlineData("Public Sub Main()\n    With TypeOf target Is Class1", 1)]
    [InlineData("Public Sub Main()\n    With Not target", 1)]
    [InlineData("Public Sub Main()\n    With left + right", 1)]
    [InlineData("Public Sub Main()\n    With count%", 1)]
    [InlineData("Public Sub Main()\n    With GetText$(1)", 1)]
    [InlineData("Public Sub Main()\n    With CStr(value)", 1)]
    [InlineData("Public Sub Main()\n    With target! Field", 1)]
    [InlineData("Public Sub Main()\n    With target !Field", 1)]
    [InlineData("Public Sub Main()\n    With target! _\n        Field", 2)]
    [InlineData("Public Sub Main()\n    With target _\n        ! Field", 2)]
    [InlineData("Public Sub Main()\n    With Strings.Asc(\"A\")", 1)]
    [InlineData("Public Sub Main()\n    With VBA.Conversion.CStr(value)", 1)]
    [InlineData("Public Sub Main()\n    With Strings.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With VBA.Strings.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With VbDayOfWeek.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With Err.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With VBA.Err.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With Global.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With VBA.Unknown", 1)]
    [InlineData("Public Sub Main()\n    With VBA.String(1)", 1)]
    [InlineData("Public Sub Main()\n    With VBA.CVar()", 1)]
    [InlineData("Public Sub Main()\n    With Information.Err.Number", 1)]
    [InlineData("Public Sub Main()\n    With LBound(values)", 1)]
    [InlineData("Public Sub Main()\n    With Circle", 1)]
    [InlineData("Public Sub Main()\n    With VBA.Choose(Index:=1)", 1)]
    [InlineData("Public Sub Main()\n    With target:", 1)]
    [InlineData("Public Sub Main()\n    With target\n    End With", 2)]
    [InlineData("#If VBA7 Then\nPublic Sub Main()\n    With target\n#End If", 2)]
    public void Incomplete_invalid_module_level_and_boundary_with_forms_are_rejected(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Theory]
    [InlineData("Public Sub Main()\n    If Ready() Then", 1)]
    [InlineData("Public Function Main() As Boolean\n    If IsReady(value, Flag:=True) Then", 1)]
    [InlineData("Public Property Get Ready() As Boolean\n    If TypeOf target.Parent Is Excel.Workbook Then", 1)]
    public void Complete_runtime_block_if_expressions_inside_callable_bodies_are_accepted(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.If, header.Kind);
    }

    [Theory]
    [InlineData(
        "Public Sub Main()\n    For index = 1 To 10",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index% = 1 To 10",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index! = 1 To 10",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index = lower! To upper",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index = lower To upper! Step increment!",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index = 1. To 3",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For index = 1.To 3",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For Each item In items",
        VbaBlockHeaderKind.ForEach)]
    [InlineData(
        "Public Sub Main()\n    For Each item In VBA.Array(1, 2)",
        VbaBlockHeaderKind.ForEach)]
    [InlineData(
        "Public Sub Main()\n    For Each item In values(0)",
        VbaBlockHeaderKind.ForEach)]
    [InlineData(
        "Public Sub Main()\n    For Each item In GetItems()",
        VbaBlockHeaderKind.ForEach)]
    [InlineData(
        "Public Sub Main()\n    For Each item In vbCrLf.Value",
        VbaBlockHeaderKind.ForEach)]
    [InlineData(
        "Public Sub Main()\n    For counters(GetSlot()) = limits.To To maximum.Step Step config.StepValue",
        VbaBlockHeaderKind.For)]
    [InlineData(
        "Public Sub Main()\n    For Each state.In In items",
        VbaBlockHeaderKind.ForEach)]
    public void Complete_for_headers_inside_callable_bodies_are_accepted(
        string source,
        VbaBlockHeaderKind expectedKind)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, lines[1].Length);

        Assert.NotNull(header);
        Assert.Equal(expectedKind, header.Kind);
        Assert.Equal("Next", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_continued_for_each_preserves_physical_lines_trivia_and_first_line_indentation()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "    Dim source As Object",
            "\tFor Each item _",
            "        In source.Items   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(tree, 2, lines[2].Length));
        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 3, lines[3].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.ForEach, header.Kind);
        Assert.Equal(2, header.FirstPhysicalLine);
        Assert.Equal(3, header.FinalPhysicalLine);
        Assert.Equal("\t", header.LeadingWhitespace);
        Assert.Equal("Next", header.ExpectedTerminator);
    }

    [Fact]
    public void Leading_member_for_expressions_require_and_use_an_enclosing_with_block()
    {
        const string outside = "Public Sub Main()\n    For .Step = .Start To .End";
        const string inside = "Public Sub Main()\n"
            + "    With state\n"
            + "        For .Step = .Start To .End";
        var outsideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", outside);
        var insideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", inside);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(
            outsideTree,
            1,
            outside.Split('\n')[1].Length));
        var header = VbaBlockHeaderSyntax.FindAtPosition(
            insideTree,
            2,
            inside.Split('\n')[2].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.For, header.Kind);
    }

    [Theory]
    [InlineData("For index = 1 To 3", 0)]
    [InlineData("Public Sub Main()\n    For", 1)]
    [InlineData("Public Sub Main()\n    For = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For 1 = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For left + right = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For (index) = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For GetCounter() = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For index$ = 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For index 1 To 3", 1)]
    [InlineData("Public Sub Main()\n    For index = To 3", 1)]
    [InlineData("Public Sub Main()\n    For index = 1 To", 1)]
    [InlineData("Public Sub Main()\n    For index = 1 To 3 Step", 1)]
    [InlineData("Public Sub Main()\n    For Each In items", 1)]
    [InlineData("Public Sub Main()\n    For Each item items", 1)]
    [InlineData("Public Sub Main()\n    For Each item + other In items", 1)]
    [InlineData("Public Sub Main()\n    For Each GetItem() In items", 1)]
    [InlineData("Public Sub Main()\n    For Each item$ In items", 1)]
    [InlineData("Public Sub Main()\n    For Each item% In items", 1)]
    [InlineData("Public Sub Main()\n    For Each item In", 1)]
    [InlineData("Public Sub Main()\n    For Each item In 1", 1)]
    [InlineData("Public Sub Main()\n    For Each item In \"text\"", 1)]
    [InlineData("Public Sub Main()\n    For Each item In Empty", 1)]
    [InlineData("Public Sub Main()\n    For Each item In Null", 1)]
    [InlineData("Public Sub Main()\n    For Each item In left + right", 1)]
    [InlineData("Public Sub Main()\n    For Each item In count%", 1)]
    [InlineData("Public Sub Main()\n    For Each item In source.Value + 1", 1)]
    [InlineData("Public Sub Main()\n    For Each item In source.Count&", 1)]
    [InlineData("Public Sub Main()\n    For index = 1 To 3:", 1)]
    [InlineData("Public Sub Main()\n    For index = 1 To 3 _\n", 1)]
    [InlineData("Public Sub Outer()\n    Public Sub Inner()\n        For index = 1 To 3", 2)]
    [InlineData("#If VBA7 Then\nPublic Sub Main()\n    For index = 1 To 3\n#End If", 2)]
    public void Incomplete_ineligible_and_structurally_ambiguous_for_headers_are_rejected(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Me_expression_is_accepted_only_in_an_object_module()
    {
        const string source = "Public Sub Main()\n    If Me.Enabled Then";
        var line = source.Split('\n')[1];

        var standard = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var objectModule = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(standard, 1, line.Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(objectModule, 1, line.Length));
    }

    [Fact]
    public void Leading_member_expression_requires_an_enclosing_with_block()
    {
        const string outside = "Public Sub Main()\n    If .Enabled Then";
        const string inside = "Public Sub Main()\n    With target\n        If .Enabled Then";

        var outsideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", outside);
        var insideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", inside);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(
            outsideTree,
            1,
            outside.Split('\n')[1].Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(
            insideTree,
            2,
            inside.Split('\n')[2].Length));
    }

    [Fact]
    public void Complete_select_case_header_inside_a_callable_body_is_accepted()
    {
        const string source = "Public Sub Main()\n    Select Case value";
        var line = source.Split('\n')[1];
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, line.Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.SelectCase, header.Kind);
        Assert.Equal("End Select", header.ExpectedTerminator);
    }

    [Fact]
    public void Complete_select_case_accepts_a_two_token_comparison_selector()
    {
        const string source =
            "Public Sub Main()\n    Select Case left = > right";
        var line = source.Split('\n')[1];
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, line.Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.SelectCase, header.Kind);
    }

    [Fact]
    public void Select_case_requires_whitespace_before_the_selector()
    {
        const string source = "Public Sub Main()\n    Select Case(value)";
        var line = source.Split('\n')[1];
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 1, line.Length);

        Assert.Null(header);
    }

    [Fact]
    public void Complete_continued_select_case_preserves_physical_lines_and_first_line_indentation()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "\tSelect Case firstValue _",
            "        + secondValue   ' keep"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var intermediate = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 1,
            character: lines[1].Length);
        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 2,
            character: lines[2].Length);

        Assert.Null(intermediate);
        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.SelectCase, header.Kind);
        Assert.Equal(1, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("\t", header.LeadingWhitespace);
        Assert.Equal("End Select", header.ExpectedTerminator);
    }

    [Fact]
    public void Leading_member_select_case_requires_an_enclosing_with_block()
    {
        const string outside = "Public Sub Main()\n    Select Case .Value";
        const string inside = "Public Sub Main()\n"
            + "    With target\n"
            + "        Select Case .Value";
        var outsideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", outside);
        var insideTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", inside);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(
            outsideTree,
            1,
            outside.Split('\n')[1].Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(
            insideTree,
            2,
            inside.Split('\n')[2].Length));
    }

    [Theory]
    [InlineData("Select Case value", 0)]
    [InlineData("Public Sub Main()\n    Select", 1)]
    [InlineData("Public Sub Main()\n    Select Case", 1)]
    [InlineData("Public Sub Main()\n    Select Case value +", 1)]
    [InlineData("Public Sub Main()\n    Select Case value:", 1)]
    [InlineData("Public Sub Main()\n    label: Select Case value", 1)]
    [InlineData("Public Sub Main()\n    Case 1", 1)]
    [InlineData("Public Sub Main()\n    Case Else", 1)]
    [InlineData("Public Sub Main()\n    End Select", 1)]
    [InlineData("#If VBA7 Then\nPublic Sub Main()\n    Select Case value\n#End If", 2)]
    public void Incomplete_module_level_branch_and_preprocessor_select_forms_are_rejected(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Complete_continued_block_if_preserves_physical_lines_trivia_and_first_line_indentation()
    {
        var lines = new[]
        {
            "Public Sub Main()",
            "    If firstValue > 0 _",
            "        And secondValue < 10 Then   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 2,
            character: lines[2].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.If, header.Kind);
        Assert.Equal(1, header.FirstPhysicalLine);
        Assert.Equal(2, header.FinalPhysicalLine);
        Assert.Equal("    ", header.LeadingWhitespace);
        Assert.Equal("End If", header.ExpectedTerminator);
        Assert.Equal(0, header.Range.Start.Character);
        Assert.Equal(lines[2].Length, header.Range.End.Character);
    }

    [Theory]
    [InlineData("If True Then", 0)]
    [InlineData("Public Sub Main()\n    If True Then Debug.Print 1", 1)]
    [InlineData("Public Sub Main()\n    If True Then:", 1)]
    [InlineData("Public Sub Main()\n    Debug.Print 1: If True Then", 1)]
    [InlineData("Public Sub Main()\n    label: If True Then", 1)]
    [InlineData("Public Sub Main()\n    If Then", 1)]
    [InlineData("Public Sub Main()\n    If True And Then", 1)]
    [InlineData("Public Sub Main()\n    If (True Then", 1)]
    [InlineData("Public Sub Main()\n    If True() Then", 1)]
    [InlineData("Public Sub Main()\n    If Ready(,) Then", 1)]
    [InlineData("Public Sub Main()\n    If 1.5% Then", 1)]
    [InlineData("Public Sub Main()\n    If 32768% Then", 1)]
    [InlineData("Public Sub Main()\n    If +1 Then", 1)]
    [InlineData("Public Sub Main()\n    If TypeOf target Is Object.Member Then", 1)]
    [InlineData("Public Sub Main()\n    ElseIf True Then", 1)]
    [InlineData("Public Sub Main()\n    Else", 1)]
    [InlineData("Public Sub Main()\n    End If", 1)]
    [InlineData("Public Sub Outer()\n    Public Sub Inner()\n        If True Then", 2)]
    [InlineData("Public Enum Mode\n    If True Then", 1)]
    [InlineData("Public Type Item\n    If True Then", 1)]
    [InlineData("#If VBA7 Then\nPublic Sub Main()\n    If True Then\n#End If", 2)]
    public void Module_level_single_line_incomplete_and_branch_if_forms_are_rejected(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Complete_continued_sub_preserves_physical_lines_trivia_and_first_line_indentation()
    {
        var lines = new[]
        {
            "    Private Static Sub Run( _",
            "        ByVal value As String)   ' keep: note"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 1,
            character: lines[1].Length);

        Assert.NotNull(header);
        Assert.Equal(VbaBlockHeaderKind.Sub, header.Kind);
        Assert.Equal(0, header.FirstPhysicalLine);
        Assert.Equal(1, header.FinalPhysicalLine);
        Assert.Equal("    ", header.LeadingWhitespace);
        Assert.Equal("End Sub", header.ExpectedTerminator);
        Assert.Equal(0, header.Range.Start.Character);
        Assert.Equal(lines[1].Length, header.Range.End.Character);
    }

    [Fact]
    public void Complete_type_qualifier_allows_an_explicit_continuation_before_member_access()
    {
        var lines = new[]
        {
            "Public Sub Run(ByVal target As Excel _",
            "    .Range)"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 1,
            character: lines[1].Length);

        Assert.NotNull(header);
    }

    [Fact]
    public void Constant_reference_allows_an_explicit_continuation_before_member_access()
    {
        var lines = new[]
        {
            "Public Sub Run(Optional value As Long = Constants _",
            "    .Foo)"
        };
        var tree = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Module1.bas",
            string.Join('\n', lines));

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 1,
            character: lines[1].Length);

        Assert.NotNull(header);
    }

    [Theory]
    [InlineData("Public Sub Run(", 0, 0)]
    [InlineData("Public Sub Run() _\n    ByVal value As Long)", 0, 0)]
    [InlineData("Public Sub Run():", 0, 0)]
    [InlineData("Public Sub Run()", 0, -1)]
    [InlineData("Public Declare Sub Run Lib \"library\" ()", 0, 0)]
    [InlineData("Public Sub Run() garbage", 0, 0)]
    [InlineData("Public Sub Run(ByVal)", 0, 0)]
    [InlineData("Public Sub Run(value As)", 0, 0)]
    [InlineData("Public Sub Run(Optional first As Long, second As Long)", 0, 0)]
    [InlineData("Public Sub Run(Optional first As Long, ParamArray rest() As Variant)", 0, 0)]
    [InlineData("Public Sub Run(ByVal ParamArray values() As Variant)", 0, 0)]
    [InlineData("Public Sub Run(ByVal values() As Long)", 0, 0)]
    [InlineData("Public Sub Run(Optional ByVal values() As Variant)", 0, 0)]
    [InlineData("Public Sub Run(Optional values() As Variant)", 0, 0)]
    [InlineData("Public Sub Run(Optional ByRef values() As Variant)", 0, 0)]
    [InlineData("Public Sub Run(Optional values() As Variant = Empty)", 0, 0)]
    [InlineData("Public Sub Run(ParamArray values() As Variant, tail As Variant)", 0, 0)]
    [InlineData("Public Sub Run(ParamArray values() As String)", 0, 0)]
    [InlineData("Public Sub Run(value$ As String)", 0, 0)]
    [InlineData("Public Sub Run(value As Long.Member)", 0, 0)]
    [InlineData("Public Sub Run(value As Long.Foo.Bar)", 0, 0)]
    [InlineData("Public Sub Run(value As Variant.Member)", 0, 0)]
    [InlineData("Public Sub Run(value As Any)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long =)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = 1 +)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = 1 + * 2)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = 1 And)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = Not)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = +1)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = (1 +))", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = 1 2)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Variant = True.Member)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Variant = Nothing.Member)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Variant = Empty.Member)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #1/1/2020#&)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #garbage#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = ##)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #13/32/2014#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #89:98#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #Jan Jan#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #1/1/2020 24:00#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Date = #4 PM garbage#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Double = 10.253&)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Variant = 1 &HFF)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = CInt(1, 2))", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = CInt)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = Constants . Foo)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Variant = _Constant)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = &HFF!)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = &HFF#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = &HFF@)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Integer = 32768%)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Long = 2147483648&)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As LongLong = 9223372036854775808^)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Double = 1E309)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Double = 1E309#)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Single = 3.5E38!)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Currency = 922337203685477.5808@)", 0, 0)]
    [InlineData("Public Sub Run(ByVal target As Excel .Range)", 0, 0)]
    [InlineData("Public Sub Run(value^ As LongLong)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Object = 1)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Object = Null)", 0, 0)]
    [InlineData("Public Sub Run(Optional value As Object = Nothing Or Nothing)", 0, 0)]
    [InlineData("Public Sub _Run()", 0, 0)]
    [InlineData("Public Sub Run(_value As Long)", 0, 0)]
    [InlineData("Public Sub Run(value As Long, VALUE As String)", 0, 0)]
    [InlineData("Public Sub Outer()\n    Public Sub Inner()", 1, 0)]
    [InlineData("Public Function Outer() As Long\nPublic Sub Run()", 1, 0)]
    [InlineData("Public Property Get Value() As Long\nPublic Sub Run()", 1, 0)]
    [InlineData("If True Then\nPublic Sub Run()", 1, 0)]
    [InlineData("Select Case 1\nPublic Sub Run()", 1, 0)]
    [InlineData("With Me\nPublic Sub Run()", 1, 0)]
    [InlineData("For index = 1 To 3\nPublic Sub Run()", 1, 0)]
    [InlineData("Do\nPublic Sub Run()", 1, 0)]
    [InlineData("While True\nPublic Sub Run()", 1, 0)]
    [InlineData("Public Enum Mode\nPublic Sub Run()", 1, 0)]
    [InlineData("Public Type Record\nPublic Sub Run()", 1, 0)]
    [InlineData("Static Sub Run() Static", 0, 0)]
    public void Incomplete_external_colon_and_non_line_end_candidates_are_rejected(
        string source,
        int line,
        int characterDelta)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line,
            lines[line].Length + characterDelta);

        Assert.Null(header);
    }

    [Theory]
    [InlineData("Public Sub Run")]
    [InlineData("Public Sub Run()   ")]
    [InlineData("Public Sub Run(ByRef values() As String, ByVal target As Excel.Range)")]
    [InlineData("Public Sub Run(ByVal target As Excel.Range, ParamArray values() As Variant)")]
    [InlineData("Public Sub Run(Optional first As Long, Optional second As Long = 2)")]
    [InlineData("Public Sub Run(Optional value$ = \"text\")")]
    [InlineData("Public Sub Run(Optional value As Long = -1)")]
    [InlineData("Public Sub Run(Optional value As Long = 1 + 2 * 3)")]
    [InlineData("Public Sub Run(Optional enabled As Boolean = Not False)")]
    [InlineData("Public Sub Run(Optional flags As Long = (FlagA Or Constants.FlagB))")]
    [InlineData("Public Sub Run(Optional value As Variant = Empty)")]
    [InlineData("Public Sub Run(Optional target As Object = Nothing)")]
    [InlineData("Public Sub Run(Optional value As Date = #1/1/2020#)")]
    [InlineData("Public Sub Run(Optional value As Date = #1-1-2020#)")]
    [InlineData("Public Sub Run(Optional value As Date = #2020/1/1#)")]
    [InlineData("Public Sub Run(Optional value As Date = #Jan 2020#)")]
    [InlineData("Public Sub Run(Optional value As Date = #January 1, 1993#)")]
    [InlineData("Public Sub Run(Optional value As Date = #1 Jan 1993#)")]
    [InlineData("Public Sub Run(Optional value As Date = #4:28:14 PM#)")]
    [InlineData("Public Sub Run(Optional value As Date = #4.28.14 PM#)")]
    [InlineData("Public Sub Run(Optional value As Date = #4 PM#)")]
    [InlineData("Public Sub Run(Optional value As Date = #1/1/2020 12:30 PM#)")]
    [InlineData("Public Sub Run(Optional value As Long = &HFF&)")]
    [InlineData("Public Sub Run(Optional value As Long = &O77)")]
    [InlineData("Public Sub Run(Optional value As Double = 1E3)")]
    [InlineData("Public Sub Run(Optional value As Double = 1E-3)")]
    [InlineData("Public Sub Run(Optional value As Double = .5)")]
    [InlineData("Public Sub Run(Optional value As Double = 1D3)")]
    [InlineData("Public Sub Run(Optional value As Double = 1.)")]
    [InlineData("Public Sub Run(Optional value As Double = 1.E3)")]
    [InlineData("Public Sub Run(Optional value As LongLong = &H100000000^)")]
    [InlineData("Public Sub Run(Optional value As Integer = 32767%)")]
    [InlineData("Public Sub Run(Optional value As Long = 2147483647&)")]
    [InlineData("Public Sub Run(Optional value As LongLong = 9223372036854775807^)")]
    [InlineData("Public Sub Run(Optional value As Double = 1E308)")]
    [InlineData("Public Sub Run(Optional value As Double = 1E308#)")]
    [InlineData("Public Sub Run(Optional value As Single = 3.4E38!)")]
    [InlineData("Public Sub Run(Optional value As Currency = 922337203685477.5807@)")]
    [InlineData("Public Sub Run(ByVal target As Excel. Range)")]
    [InlineData("Public Sub Run(Optional value As Long = &777)")]
    [InlineData("Public Sub Run(Optional value As String = 1 & HFF)")]
    [InlineData("Public Sub Run(Optional value As Long = Foo&)")]
    [InlineData("Public Sub Run(Optional value As Long = Constants.Foo&)")]
    [InlineData("Public Sub Run(Optional value As Long = CInt(1))")]
    [InlineData("Public Sub Run(Optional value As Boolean = Nothing Is Nothing)")]
    [InlineData("Public Sub Run(ByRef values() As Long)")]
    [InlineData("Public Sub Run(value_with_underscore As Long)")]
    [InlineData("Public Sub Run(value^)")]
    [InlineData("Public Sub Run(ByVal Optional value As Long = 1)")]
    [InlineData("Public Sub Run(ByRef Optional value As Long = 1)")]
    [InlineData("Public Sub Run(value As [Long])")]
    [InlineData("Public Sub Run(ParamArray values() As [Variant])")]
    [InlineData("Public Sub Run() Static")]
    [InlineData("Public Sub Run Static")]
    [InlineData("public sub run(byval value as long)")]
    [InlineData("Public Sub Run(Optional value As String = \":\")   ' note: ok")]
    [InlineData("Public Sub Run()   ' astral comment: 😀")]
    public void Optional_parentheses_and_non_structural_colons_remain_eligible(string source)
    {
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 0,
            character: source.Length);

        Assert.NotNull(header);
        Assert.Equal("End Sub", header.ExpectedTerminator);
    }

    [Fact]
    public void Candidate_after_a_complete_prior_block_remains_eligible()
    {
        const string source = "Public Sub First()\nEnd Sub\nPublic Sub Run()";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 2,
            character: "Public Sub Run()".Length);

        Assert.NotNull(header);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Identifiers_longer_than_255_characters_are_rejected(bool procedureName)
    {
        var overlongName = new string('A', 256);
        var source = procedureName
            ? $"Public Sub {overlongName}()"
            : $"Public Sub Run({overlongName} As Long)";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 0, source.Length);

        Assert.Null(header);
    }

    [Fact]
    public void Overlong_constant_reference_is_rejected()
    {
        var overlongName = new string('A', 256);
        var source = $"Public Sub Run(Optional value As Variant = {overlongName})";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, 0, source.Length);

        Assert.Null(header);
    }

    [Fact]
    public void Friend_sub_is_eligible_only_in_an_object_module()
    {
        const string source = "Friend Sub Run()";
        var standardTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var classTree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);

        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(standardTree, 0, source.Length));
        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(classTree, 0, source.Length));
    }

    [Fact]
    public void Global_sub_is_eligible_only_in_a_standard_module()
    {
        const string source = "Global Sub Run()";
        var standardTree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var classTree = VbaSyntaxTree.ParseModule("file:///C:/work/Class1.cls", source);

        Assert.NotNull(VbaBlockHeaderSyntax.FindAtPosition(standardTree, 0, source.Length));
        Assert.Null(VbaBlockHeaderSyntax.FindAtPosition(classTree, 0, source.Length));
    }

    [Theory]
    [InlineData("#If VBA7 Then\nPublic Sub Run()", 1)]
    [InlineData("#If VBA7 Then\n#End If\nPublic Sub Run()", 2)]
    public void Conditional_compilation_contexts_fail_closed_until_branch_locality_is_supported(
        string source,
        int line)
    {
        var lines = source.Split('\n');
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(tree, line, lines[line].Length);

        Assert.Null(header);
    }

    [Fact]
    public void Non_conditional_preprocessor_constants_do_not_hide_an_eligible_sub()
    {
        const string source = "#Const VBA7 = True\nPublic Sub Run()";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var header = VbaBlockHeaderSyntax.FindAtPosition(
            tree,
            line: 1,
            character: "Public Sub Run()".Length);

        Assert.NotNull(header);
    }
}
