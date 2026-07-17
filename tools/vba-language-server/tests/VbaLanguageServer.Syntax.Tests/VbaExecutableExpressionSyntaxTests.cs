using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaExecutableExpressionSyntaxTests
{
    [Fact]
    public void Complete_identifier_expression_is_accepted()
    {
        Assert.True(IsComplete("ready"));
    }

    [Fact]
    public void Complete_unary_binary_literal_and_grouping_expression_is_accepted()
    {
        Assert.True(IsComplete("Not ready And (count >= 1 Or name Like \"A*\")"));
    }

    [Fact]
    public void Complete_nested_call_with_named_argument_is_accepted()
    {
        Assert.True(IsComplete("IsReady(value, Transform(items(0)), Flag:=True)"));
    }

    [Theory]
    [InlineData("Ready(, 1)")]
    [InlineData("Ready(, Flag:=True)")]
    public void Non_trailing_omitted_positional_arguments_are_accepted(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Fact]
    public void Complete_typeof_is_expression_is_accepted()
    {
        Assert.True(IsComplete("TypeOf target.Parent Is Excel.Workbook"));
    }

    [Theory]
    [InlineData("New Class1")]
    [InlineData("New Project1.Class1")]
    [InlineData("New Project1. Class1")]
    [InlineData("New Project1 _\n.Class1")]
    public void Complete_new_class_expressions_are_accepted(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData("New")]
    [InlineData("New Long")]
    [InlineData("New Object")]
    [InlineData("New Project1.")]
    [InlineData("New Project1 .Class1")]
    [InlineData("New Project1 . Class1")]
    [InlineData("New Class1()")]
    public void Incomplete_or_non_class_new_expressions_are_rejected(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    [Fact]
    public void Callable_keyword_expressions_are_accepted()
    {
        Assert.True(IsComplete("Date >= #1/1/2020# And Len(String(2, \"x\")) > 0"));
    }

    [Theory]
    [InlineData("String")]
    [InlineData("String()")]
    [InlineData("String(1)")]
    [InlineData("String(1, 2, 3)")]
    [InlineData("String$")]
    [InlineData("String$()")]
    [InlineData("String$(1)")]
    [InlineData("String$(1, 2, 3)")]
    public void String_intrinsic_requires_exactly_two_arguments(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    [Fact]
    public void String_intrinsic_accepts_exactly_two_arguments()
    {
        Assert.True(IsComplete("String(2, \"x\")"));
        Assert.True(IsComplete("String(number:=2, character:=\"x\")"));
    }

    [Theory]
    [InlineData("Date(1)")]
    [InlineData("Date$(1)")]
    public void Date_intrinsic_rejects_arguments(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    [Theory]
    [InlineData("Date")]
    [InlineData("Date()")]
    public void Date_intrinsic_accepts_bare_or_empty_call_form(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData("String(2, \"x\").Member")]
    [InlineData("String(2, \"x\")!Member")]
    [InlineData("String(2, \"x\")(0)")]
    [InlineData("Date.Member")]
    [InlineData("Date!Member")]
    [InlineData("Date()(0)")]
    public void Variant_intrinsics_accept_postfix_chains(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData("String$(2, \"x\").Member")]
    [InlineData("Date$.Member")]
    [InlineData("Date$!Member")]
    public void Fixed_string_intrinsics_reject_postfix_chains(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    [Theory]
    [InlineData("String$(2, \"x\")")]
    [InlineData("Date$")]
    [InlineData("Date$()")]
    public void Adjacent_string_suffix_preserves_intrinsic_call_shapes(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData("text$.Length", false)]
    [InlineData("count%.Foo", false)]
    [InlineData("GetText$(1).Foo", false)]
    [InlineData(".Text$.Foo", true)]
    public void Scalar_type_character_rejects_member_postfix(
        string expression,
        bool allowLeadingMemberAccess)
    {
        Assert.False(IsComplete(expression, allowLeadingMemberAccess: allowLeadingMemberAccess));
    }

    [Theory]
    [InlineData("TypeOf count% Is Widget")]
    [InlineData("TypeOf GetLong%(1) Is Widget")]
    [InlineData("TypeOf obj.Value% Is Widget")]
    public void Typeof_rejects_scalar_type_character_operands(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    [Theory]
    [InlineData("text$", false)]
    [InlineData("count% + 1", false)]
    [InlineData("GetText$(1)", false)]
    [InlineData("obj.Value%", false)]
    [InlineData("obj.Value%(1)", false)]
    [InlineData(".Text$", true)]
    [InlineData(".Text$(1)", true)]
    public void Scalar_type_character_accepts_bare_or_single_invocation(
        string expression,
        bool allowLeadingMemberAccess)
    {
        Assert.True(IsComplete(expression, allowLeadingMemberAccess: allowLeadingMemberAccess));
    }

    [Theory]
    [InlineData("GetText$(1)(2)", false)]
    [InlineData("obj.Value%(1)(2)", false)]
    [InlineData(".Text$(1)(2)", true)]
    public void Scalar_type_character_rejects_second_invocation(
        string expression,
        bool allowLeadingMemberAccess)
    {
        Assert.False(IsComplete(expression, allowLeadingMemberAccess: allowLeadingMemberAccess));
    }

    [Theory]
    [InlineData("target.Member", false)]
    [InlineData("target.Member(1).Next", false)]
    [InlineData("target.Mod(1)", false)]
    [InlineData("target. Parent", false)]
    [InlineData("target _\n.Parent", false)]
    [InlineData("target!Field", false)]
    [InlineData("target _\n!Field", false)]
    [InlineData("target _\n! _\nField", false)]
    [InlineData("target.Mod", false)]
    [InlineData("target!Mod", false)]
    [InlineData("Factory()(1).Member", false)]
    [InlineData(".Item(1).Member", true)]
    public void Ordinary_identifier_postfix_chains_remain_accepted(
        string expression,
        bool allowLeadingMemberAccess)
    {
        Assert.True(IsComplete(expression, allowLeadingMemberAccess: allowLeadingMemberAccess));
    }

    [Fact]
    public void Identifier_type_characters_are_accepted()
    {
        Assert.True(IsComplete("text$ <> \"\" And count& > 0"));
    }

    [Fact]
    public void Leading_member_access_requires_an_enclosing_with_context()
    {
        Assert.False(IsComplete(".Enabled"));
        Assert.True(IsComplete(".Enabled", allowLeadingMemberAccess: true));
        Assert.False(IsComplete("!Child"));
        Assert.True(IsComplete("!Child", allowLeadingMemberAccess: true));
        Assert.True(IsComplete("! Child", allowLeadingMemberAccess: true));
        Assert.True(IsComplete("! _\nChild", allowLeadingMemberAccess: true));
        Assert.True(IsComplete(".5", allowLeadingMemberAccess: true));
    }

    [Theory]
    [InlineData(".5")]
    [InlineData("1E-3")]
    [InlineData("&HFF&")]
    [InlineData("#1/1/2020#")]
    [InlineData("32767%")]
    [InlineData("2147483647&")]
    [InlineData("&HFFFFFFFF&")]
    [InlineData("1E308")]
    public void Complete_numeric_and_date_literals_are_accepted(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData("left < > right")]
    [InlineData("left > < right")]
    [InlineData("left > = right")]
    [InlineData("left = > right")]
    [InlineData("left < = right")]
    [InlineData("left = < right")]
    [InlineData("left = _\n > right")]
    public void Complete_two_token_comparison_operators_are_accepted(string expression)
    {
        Assert.True(IsComplete(expression));
    }

    [Theory]
    [InlineData(VbaModuleKind.StandardModule, false)]
    [InlineData(VbaModuleKind.ClassModule, true)]
    [InlineData(VbaModuleKind.FormModule, true)]
    public void Me_obeys_module_kind_legality(VbaModuleKind moduleKind, bool expected)
    {
        Assert.Equal(expected, IsComplete("Me.Parent Is Nothing", moduleKind));
    }

    [Theory]
    [InlineData("ready +")]
    [InlineData("ready + * other")]
    [InlineData("(ready")]
    [InlineData("ready)")]
    [InlineData("Ready(value")]
    [InlineData("Ready(value, Flag:=)")]
    [InlineData("Ready(Flag:=True, value)")]
    [InlineData("TypeOf target Is")]
    [InlineData("TypeOf target Is Excel.")]
    [InlineData("TypeOf target Is Long")]
    [InlineData("TypeOf target + 1 Is Widget")]
    [InlineData("&H")]
    [InlineData("1..Member")]
    [InlineData("True()")]
    [InlineData("1(2)")]
    [InlineData("\"value\".Length")]
    [InlineData("Nothing.Member")]
    [InlineData("(target).Member")]
    [InlineData("Ready(,)")]
    [InlineData("Ready(1,)")]
    [InlineData("1.5%")]
    [InlineData("1E3&")]
    [InlineData("32768%")]
    [InlineData("2147483648&")]
    [InlineData("&H100000000&")]
    [InlineData("1E309")]
    [InlineData("+1")]
    [InlineData("+Ready()")]
    [InlineData("TypeOf target Is Object.Member")]
    [InlineData("target! Field")]
    [InlineData("target !Field")]
    [InlineData("target ! Field")]
    [InlineData("target! _\nField")]
    [InlineData("target _\n! Field")]
    [InlineData("target .Parent")]
    public void Malformed_dangling_and_unbalanced_expressions_are_rejected(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    private static bool IsComplete(
        string expression,
        VbaModuleKind moduleKind = VbaModuleKind.StandardModule,
        bool allowLeadingMemberAccess = false)
    {
        var tokens = VbaTokenStream.FromText($"If {expression} Then").Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        return VbaExecutableExpressionSyntax.IsComplete(
            tokens,
            1,
            tokens.Length - 1,
            moduleKind,
            allowLeadingMemberAccess);
    }
}
