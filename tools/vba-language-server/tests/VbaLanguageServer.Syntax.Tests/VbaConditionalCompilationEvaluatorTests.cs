using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaConditionalCompilationEvaluatorTests
{
    [Fact]
    public void Evaluation_selects_active_branch_using_case_insensitive_global_constants()
    {
        const string source = "#If VBA7 Then\n"
            + "Public Sub Modern()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Legacy()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment(
            new Dictionary<string, VbaConditionalCompilationValue>
            {
                ["vba7"] = VbaConditionalCompilationValue.FromBoolean(true)
            },
            builtInConstantNames: ["VBA7"]);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(VbaConditionalCompilationBranchPath.Root));
        Assert.True(evaluation.IsActive(PathFor(tree, "Modern")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Legacy")));
    }

    [Fact]
    public void Module_local_const_shadows_a_project_global_constant()
    {
        const string source = "#Const Feature = True\n"
            + "#If FEATURE Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Disabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment(
            new Dictionary<string, VbaConditionalCompilationValue>
            {
                ["feature"] = VbaConditionalCompilationValue.FromBoolean(false)
            });

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(PathFor(tree, "Enabled")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Disabled")));
    }

    [Fact]
    public void Integer_const_can_select_a_conditional_compilation_branch()
    {
        const string source = "#Const Feature = 1\n"
            + "#If Feature Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Disabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment([]);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(PathFor(tree, "Enabled")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Disabled")));
    }

    [Theory]
    [InlineData("1^")]
    [InlineData("&H1^")]
    public void LongLong_literal_fails_closed_without_a_verified_capability(string literal)
    {
        var source = $"#If {literal} Then\n"
            + "Public Sub UnsupportedLongLong()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationUnsupportedExpression");
        Assert.False(evaluation.IsActive(PathFor(tree, "UnsupportedLongLong")));
    }

    [Fact]
    public void LongLong_global_constant_is_rejected_without_a_verified_capability()
    {
        var constants = new Dictionary<string, VbaConditionalCompilationValue>
        {
            ["Huge"] = VbaConditionalCompilationValue.FromInteger(long.MaxValue)
        };

        var error = Assert.Throws<ArgumentException>(() =>
            new VbaConditionalCompilationEnvironment(constants));

        Assert.Contains("LongLong", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capability", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("&HFFFF = -1")]
    [InlineData("&H8000 = -32768")]
    [InlineData("&H0000FFFF = -1")]
    [InlineData("&H00008000 = -32768")]
    [InlineData("&HFFFF% = -1")]
    [InlineData("&HFFFF& = 65535")]
    [InlineData("&H10000 = 65536")]
    [InlineData("&HFFFFFFFF = -1")]
    [InlineData("&HFFFFFFFF& = -1")]
    [InlineData("&HFFFFFFFFFFFFFFFF^ = -1")]
    [InlineData("&O177777 = -1")]
    [InlineData("&O100000 = -32768")]
    [InlineData("&O000177777 = -1")]
    [InlineData("&O000100000 = -32768")]
    [InlineData("&O177777& = 65535")]
    [InlineData("(Not &HFFFF) = 0")]
    [InlineData("(Not &HFFFF&) = -65536")]
    public void Based_integer_literals_use_the_actual_vba_representation_width(
        string condition)
    {
        var source = $"#If {condition} Then\n"
            + "Public Sub Selected()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Fallback()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([], supportsLongLong: true));

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(PathFor(tree, "Selected")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Fallback")));
    }

    [Theory]
    [InlineData("&H7FFF", 32767L, 16)]
    [InlineData("&HFFFF", -1L, 16)]
    [InlineData("&H8000", -32768L, 16)]
    [InlineData("&H0000FFFF", -1L, 16)]
    [InlineData("&HFFFF&", 65535L, 32)]
    [InlineData("&O177777", -1L, 16)]
    [InlineData("Not &HFFFFFFFF&", 0L, 32)]
    [InlineData("Not &HFFFFFFFFFFFFFFFF^", 0L, 64)]
    [InlineData("-&H00000001&", -1L, 32)]
    [InlineData("1 + 1&", 2L, 32)]
    [InlineData("1^ + 1", 2L, 64)]
    [InlineData("&HFFFF& And 0&", 0L, 32)]
    public void Integer_expression_values_retain_their_vba_subtype(
        string expression,
        long expectedValue,
        int expectedWidth)
    {
        var tokens = VbaTokenStream.FromText(expression).Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine)
            .ToArray();

        var evaluation = VbaConstantExpressionSyntax.EvaluateConditionalCompilationExpression(
            tokens,
            0,
            tokens.Length,
            _ => throw new InvalidOperationException(),
            supportsLongLong: true);

        Assert.True(evaluation.Succeeded);
        Assert.Equal(expectedWidth, (int)evaluation.Value.IntegralWidth);
        Assert.Equal(expectedValue, evaluation.Value.IntegralValue);
    }

    [Theory]
    [InlineData("&H7FFF + 1")]
    [InlineData("&H7FFFFFFF& + 1&")]
    [InlineData("-&H8000")]
    [InlineData("-&H80000000&")]
    public void Integral_subtype_overflow_fails_closed(string expression)
    {
        var source = $"#If {expression} Then\n"
            + "Public Sub Overflowed()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.conditionalCompilationOverflow");
        Assert.False(evaluation.IsActive(PathFor(tree, "Overflowed")));
    }

    [Fact]
    public void Boolean_arithmetic_coercion_fails_closed()
    {
        const string source = "#If True + 1 Then\n"
            + "Public Sub Coerced()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationUnsupportedExpression");
        Assert.False(evaluation.IsActive(PathFor(tree, "Coerced")));
    }

    [Fact]
    public void Module_const_preserves_the_evaluated_integer_subtype()
    {
        const string source = "#Const ClearedLong = Not &HFFFFFFFF&\n"
            + "#If ClearedLong + 32767 + 1 = 32768& Then\n"
            + "Public Sub Selected()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Fallback()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(PathFor(tree, "Selected")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Fallback")));
    }

    [Fact]
    public void Evaluation_selects_nested_if_elseif_and_else_branches()
    {
        const string source = "#Const Edition = 2\n"
            + "#If Edition = 1 Then\n"
            + "Public Sub FirstEdition()\n"
            + "End Sub\n"
            + "#ElseIf Edition = 2 Then\n"
            + "#If Win64 And Not Legacy Then\n"
            + "Public Sub NativeEdition()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub PortableEdition()\n"
            + "End Sub\n"
            + "#End If\n"
            + "#Else\n"
            + "Public Sub FallbackEdition()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment(
            new Dictionary<string, VbaConditionalCompilationValue>
            {
                ["Win64"] = VbaConditionalCompilationValue.FromBoolean(true)
            },
            builtInConstantNames: ["Win64"]);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.True(evaluation.Succeeded);
        Assert.False(evaluation.IsActive(PathFor(tree, "FirstEdition")));
        Assert.True(evaluation.IsActive(PathFor(tree, "NativeEdition")));
        Assert.False(evaluation.IsActive(PathFor(tree, "PortableEdition")));
        Assert.False(evaluation.IsActive(PathFor(tree, "FallbackEdition")));
    }

    [Fact]
    public void Module_const_cannot_redefine_a_compiler_built_in_constant()
    {
        const string source = "#Const VBA7 = False\n"
            + "#If VBA7 Then\n"
            + "Public Sub Modern()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment(
            new Dictionary<string, VbaConditionalCompilationValue>
            {
                ["VBA7"] = VbaConditionalCompilationValue.FromBoolean(true)
            },
            builtInConstantNames: ["VBA7"]);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationBuiltInRedefinition");
        Assert.False(evaluation.IsActive(PathFor(tree, "Modern")));
    }

    [Fact]
    public void Duplicate_module_constants_are_rejected_case_insensitively()
    {
        const string source = "#Const Feature = True\n"
            + "#Const FEATURE = False";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);
        var environment = new VbaConditionalCompilationEnvironment([]);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(tree, environment);

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.conditionalCompilationDuplicateConstant");
    }

    [Fact]
    public void Undefined_constant_in_if_is_empty_and_selects_else()
    {
        const string source = "#If Missing Then\n"
            + "Public Sub Present()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub MissingFallback()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.True(evaluation.Succeeded);
        Assert.False(evaluation.IsActive(PathFor(tree, "Present")));
        Assert.True(evaluation.IsActive(PathFor(tree, "MissingFallback")));
    }

    [Fact]
    public void Const_directive_in_an_inactive_branch_is_still_processed()
    {
        const string source = "#If False Then\n"
            + "#Const Feature = True\n"
            + "#End If\n"
            + "#If Feature Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#Else\n"
            + "Public Sub Disabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.True(evaluation.Succeeded);
        Assert.True(evaluation.IsActive(PathFor(tree, "Enabled")));
        Assert.False(evaluation.IsActive(PathFor(tree, "Disabled")));
    }

    [Fact]
    public void Undefined_constant_in_const_is_an_error_even_in_an_inactive_branch()
    {
        const string source = "#If False Then\n"
            + "#Const Feature = Missing\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationUndefinedConstant");
    }

    [Fact]
    public void Cyclic_const_definitions_fail_closed()
    {
        const string source = "#Const First = Second\n"
            + "#Const Second = First\n"
            + "#If First Then\n"
            + "Public Sub Cyclic()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.conditionalCompilationCyclicConstant");
        Assert.False(evaluation.IsActive(PathFor(tree, "Cyclic")));
    }

    [Fact]
    public void Malformed_const_expression_fails_closed()
    {
        const string source = "#Const Feature =\n"
            + "#If Feature Then\n"
            + "Public Sub Enabled()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.conditionalCompilationMalformed");
        Assert.False(evaluation.IsActive(PathFor(tree, "Enabled")));
    }

    [Fact]
    public void Malformed_preprocessor_structure_fails_closed()
    {
        const string source = "#If True Then\n"
            + "Public Sub Unclosed()\n"
            + "End Sub";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal));
        Assert.False(evaluation.IsActive(VbaConditionalCompilationBranchPath.Root));
    }

    [Fact]
    public void Unsupported_value_semantics_fail_closed()
    {
        const string source = "#If CStr(1) = \"1\" Then\n"
            + "Public Sub TextComparison()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationUnsupportedExpression");
        Assert.False(evaluation.IsActive(PathFor(tree, "TextComparison")));
    }

    [Fact]
    public void Unsuffixed_literal_that_requires_floating_coercion_fails_closed()
    {
        const string source = "#If 2147483648 Then\n"
            + "Public Sub FloatingCoercion()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([]));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code
                == "syntax.conditionalCompilationUnsupportedExpression");
        Assert.False(evaluation.IsActive(PathFor(tree, "FloatingCoercion")));
    }

    [Fact]
    public void Integer_overflow_fails_closed()
    {
        const string source = "#Const Maximum = 9223372036854775807^\n"
            + "#If Maximum + 1 Then\n"
            + "Public Sub Overflowed()\n"
            + "End Sub\n"
            + "#End If";
        var tree = VbaSyntaxTree.ParseModule("file:///C:/work/Module1.bas", source);

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(
            tree,
            new VbaConditionalCompilationEnvironment([], supportsLongLong: true));

        Assert.False(evaluation.Succeeded);
        Assert.Contains(
            evaluation.Diagnostics,
            diagnostic => diagnostic.Code == "syntax.conditionalCompilationOverflow");
        Assert.False(evaluation.IsActive(PathFor(tree, "Overflowed")));
    }

    private static VbaConditionalCompilationBranchPath PathFor(
        VbaSyntaxTree tree,
        string procedureName)
    {
        var declaration = Assert.Single(
            tree.Module.CallableDeclarations,
            candidate => candidate.Name == procedureName);
        Assert.True(VbaConditionalCompilationBranchFacts.TryGetPath(
            tree,
            declaration.Range,
            requireCompleteStructure: true,
            out var path));
        return path;
    }
}
