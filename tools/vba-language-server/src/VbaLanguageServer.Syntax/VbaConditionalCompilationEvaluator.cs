using System.Collections.ObjectModel;

namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies the supported value representation of a conditional-compilation constant.
/// </summary>
public enum VbaConditionalCompilationValueKind
{
    /// <summary>
    /// A VBA Boolean value.
    /// </summary>
    Boolean,

    /// <summary>
    /// A signed integral value.
    /// </summary>
    Integer
}

internal enum VbaConditionalCompilationIntegralWidth
{
    Int16 = 16,
    Int32 = 32,
    Int64 = 64
}

/// <summary>
/// Represents one value that can participate in supported conditional-compilation evaluation.
/// </summary>
public readonly record struct VbaConditionalCompilationValue
{
    private VbaConditionalCompilationValue(
        VbaConditionalCompilationValueKind kind,
        long integralValue,
        VbaConditionalCompilationIntegralWidth integralWidth)
    {
        Kind = kind;
        IntegralValue = integralValue;
        IntegralWidth = integralWidth;
    }

    /// <summary>
    /// Gets the represented value kind.
    /// </summary>
    public VbaConditionalCompilationValueKind Kind { get; }

    /// <summary>
    /// Gets the VBA integral representation. True is -1 and False is zero.
    /// </summary>
    public long IntegralValue { get; }

    internal VbaConditionalCompilationIntegralWidth IntegralWidth { get; }

    /// <summary>
    /// Creates a VBA Boolean conditional-compilation value.
    /// </summary>
    public static VbaConditionalCompilationValue FromBoolean(bool value)
        => new(
            VbaConditionalCompilationValueKind.Boolean,
            value ? -1 : 0,
            VbaConditionalCompilationIntegralWidth.Int16);

    /// <summary>
    /// Creates a signed integral value using the narrowest VBA representation that fits.
    /// </summary>
    public static VbaConditionalCompilationValue FromInteger(long value)
        => FromInteger(
            value,
            value is >= short.MinValue and <= short.MaxValue
                ? VbaConditionalCompilationIntegralWidth.Int16
                : value is >= int.MinValue and <= int.MaxValue
                    ? VbaConditionalCompilationIntegralWidth.Int32
                    : VbaConditionalCompilationIntegralWidth.Int64);

    internal static VbaConditionalCompilationValue FromInteger(
        long value,
        VbaConditionalCompilationIntegralWidth integralWidth)
        => new(VbaConditionalCompilationValueKind.Integer, value, integralWidth);

    internal bool IsTruthy => IntegralValue != 0;
}

/// <summary>
/// Supplies project and host constants for one conditional-compilation evaluation.
/// </summary>
public sealed class VbaConditionalCompilationEnvironment
{
    private readonly IReadOnlyDictionary<string, VbaConditionalCompilationValue> globalConstants;
    private readonly IReadOnlySet<string> builtInConstantNames;

    /// <summary>
    /// Creates an immutable, case-insensitive evaluation environment.
    /// </summary>
    public VbaConditionalCompilationEnvironment(
        IEnumerable<KeyValuePair<string, VbaConditionalCompilationValue>> globalConstants,
        IEnumerable<string>? builtInConstantNames = null,
        bool supportsLongLong = false)
    {
        ArgumentNullException.ThrowIfNull(globalConstants);

        var constants = new Dictionary<string, VbaConditionalCompilationValue>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var constant in globalConstants)
        {
            if (!supportsLongLong
                && constant.Value.IntegralWidth
                    == VbaConditionalCompilationIntegralWidth.Int64)
            {
                throw new ArgumentException(
                    $"Conditional-compilation global constant '{constant.Key}' requires the " +
                    "LongLong capability.",
                    nameof(globalConstants));
            }

            if (string.IsNullOrWhiteSpace(constant.Key) || !constants.TryAdd(constant.Key, constant.Value))
            {
                throw new ArgumentException(
                    $"Conditional-compilation global constant '{constant.Key}' is invalid or duplicated.",
                    nameof(globalConstants));
            }
        }

        var builtIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in builtInConstantNames ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)
                || !builtIns.Add(name)
                || !constants.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Conditional-compilation built-in constant '{name}' is invalid, duplicated, or has no value.",
                    nameof(builtInConstantNames));
            }
        }

        this.globalConstants = new ReadOnlyDictionary<string, VbaConditionalCompilationValue>(constants);
        this.builtInConstantNames = builtIns;
        SupportsLongLong = supportsLongLong;
    }

    /// <summary>
    /// Gets whether the verified compiler context supports the VBA LongLong subtype.
    /// </summary>
    public bool SupportsLongLong { get; }

    internal bool TryGetGlobalConstant(
        string name,
        out VbaConditionalCompilationValue value)
        => globalConstants.TryGetValue(name, out value);

    internal bool IsBuiltInConstant(string name) => builtInConstantNames.Contains(name);
}

/// <summary>
/// Reports active structural branches or diagnostics that prevented a safe evaluation.
/// </summary>
public sealed class VbaConditionalCompilationEvaluation
{
    private readonly IReadOnlySet<VbaConditionalCompilationBranchIdentity> activeBranches;

    internal VbaConditionalCompilationEvaluation(
        IEnumerable<VbaConditionalCompilationBranchIdentity> activeBranches,
        IEnumerable<VbaSyntaxDiagnostic> diagnostics)
    {
        this.activeBranches = new HashSet<VbaConditionalCompilationBranchIdentity>(activeBranches);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>
    /// Gets whether the complete module evaluation succeeded.
    /// </summary>
    public bool Succeeded => Diagnostics.Count == 0;

    /// <summary>
    /// Gets the diagnostics that prevented a safe evaluation.
    /// </summary>
    public IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Determines whether a structural branch path is active in this evaluation.
    /// </summary>
    public bool IsActive(VbaConditionalCompilationBranchPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Succeeded && path.Branches.All(activeBranches.Contains);
    }
}

/// <summary>
/// Evaluates structural conditional-compilation branches against explicit compiler constants.
/// </summary>
public static class VbaConditionalCompilationEvaluator
{
    /// <summary>
    /// Evaluates one parsed module without inferring project or host constants.
    /// </summary>
    public static VbaConditionalCompilationEvaluation Evaluate(
        VbaSyntaxTree tree,
        VbaConditionalCompilationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(environment);

        var preprocessorDiagnostics = tree.Diagnostics
            .Where(diagnostic => diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal))
            .ToArray();
        if (preprocessorDiagnostics.Length != 0)
        {
            return new VbaConditionalCompilationEvaluation([], preprocessorDiagnostics);
        }

        var diagnostics = new List<VbaSyntaxDiagnostic>();
        var context = new ModuleEvaluationContext(
            tree.Module.PreprocessorDirectives,
            environment,
            diagnostics);
        context.EvaluateDefinitions();
        if (diagnostics.Count != 0)
        {
            return new VbaConditionalCompilationEvaluation([], diagnostics);
        }

        var activeBranches = new HashSet<VbaConditionalCompilationBranchIdentity>();
        foreach (var block in tree.Module.PreprocessorBlocks)
        {
            EvaluateBlock(block, context, parentIsActive: true, activeBranches, diagnostics);
        }

        return diagnostics.Count == 0
            ? new VbaConditionalCompilationEvaluation(activeBranches, [])
            : new VbaConditionalCompilationEvaluation([], diagnostics);
    }

    private static void EvaluateBlock(
        VbaPreprocessorBlockSyntax block,
        ModuleEvaluationContext context,
        bool parentIsActive,
        ISet<VbaConditionalCompilationBranchIdentity> activeBranches,
        ICollection<VbaSyntaxDiagnostic> diagnostics)
    {
        VbaPreprocessorBranchSyntax? selected = null;
        foreach (var branch in block.Branches)
        {
            if (branch.Directive.Kind == VbaPreprocessorDirectiveKind.Else)
            {
                selected ??= branch;
                continue;
            }

            var expression = EvaluateBranchCondition(branch.Directive, context);
            if (!expression.Succeeded)
            {
                diagnostics.Add(ToDiagnostic(branch.Directive, expression));
                continue;
            }

            if (selected is null && expression.Value.IsTruthy)
            {
                selected = branch;
            }
        }

        foreach (var branch in block.Branches)
        {
            var branchIsActive = parentIsActive && ReferenceEquals(branch, selected);
            if (branchIsActive)
            {
                activeBranches.Add(new VbaConditionalCompilationBranchIdentity(
                    block.IfDirective.Range.Start.Offset,
                    branch.Directive.Range.Start.Offset));
            }

            foreach (var nested in branch.NestedBlocks)
            {
                EvaluateBlock(
                    nested,
                    context,
                    branchIsActive,
                    activeBranches,
                    diagnostics);
            }
        }
    }

    private static VbaConditionalCompilationExpressionEvaluation EvaluateBranchCondition(
        VbaPreprocessorDirectiveSyntax directive,
        ModuleEvaluationContext context)
    {
        var body = VbaPreprocessorParser.GetNormalizedDirectiveBody(directive.Text);
        var tokens = SignificantTokens(VbaTokenStream.FromText(body).Tokens);
        if (tokens.Count < 3
            || !tokens[^1].Text.Equals("Then", StringComparison.OrdinalIgnoreCase))
        {
            return VbaConditionalCompilationExpressionEvaluation.Failure(
                VbaConditionalCompilationFailureKind.Malformed,
                "The conditional-compilation directive has no complete condition.");
        }

        return VbaConstantExpressionSyntax.EvaluateConditionalCompilationExpression(
            tokens,
            start: 1,
            end: tokens.Count - 1,
            context.ResolveConditionConstant,
            context.SupportsLongLong);
    }

    private static IReadOnlyList<VbaToken> SignificantTokens(IReadOnlyList<VbaToken> tokens)
        => tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();

    private static VbaSyntaxDiagnostic ToDiagnostic(
        VbaPreprocessorDirectiveSyntax directive,
        VbaConditionalCompilationExpressionEvaluation evaluation)
        => new(
            evaluation.FailureKind switch
            {
                VbaConditionalCompilationFailureKind.Malformed =>
                    "syntax.conditionalCompilationMalformed",
                VbaConditionalCompilationFailureKind.Undefined =>
                    "syntax.conditionalCompilationUndefinedConstant",
                VbaConditionalCompilationFailureKind.Cycle =>
                    "syntax.conditionalCompilationCyclicConstant",
                VbaConditionalCompilationFailureKind.BuiltInRedefinition =>
                    "syntax.conditionalCompilationBuiltInRedefinition",
                VbaConditionalCompilationFailureKind.DuplicateDefinition =>
                    "syntax.conditionalCompilationDuplicateConstant",
                VbaConditionalCompilationFailureKind.Overflow =>
                    "syntax.conditionalCompilationOverflow",
                _ => "syntax.conditionalCompilationUnsupportedExpression"
            },
            evaluation.Message,
            directive.Range);

    private sealed class ModuleEvaluationContext
    {
        private readonly VbaConditionalCompilationEnvironment environment;
        private readonly ICollection<VbaSyntaxDiagnostic> diagnostics;
        private readonly Dictionary<string, ConstantDefinition> definitions = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VbaConditionalCompilationValue> values = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> evaluating = new(StringComparer.OrdinalIgnoreCase);

        public ModuleEvaluationContext(
            IReadOnlyList<VbaPreprocessorDirectiveSyntax> directives,
            VbaConditionalCompilationEnvironment environment,
            ICollection<VbaSyntaxDiagnostic> diagnostics)
        {
            this.environment = environment;
            this.diagnostics = diagnostics;
            foreach (var directive in directives.Where(
                candidate => candidate.Kind == VbaPreprocessorDirectiveKind.Const))
            {
                TryAddDefinition(directive);
            }
        }

        public void EvaluateDefinitions()
        {
            foreach (var definition in definitions.Values)
            {
                var evaluation = ResolveDefinition(definition.Name);
                if (!evaluation.Succeeded)
                {
                    diagnostics.Add(ToDiagnostic(definition.Directive, evaluation));
                }
            }
        }

        public VbaConditionalCompilationExpressionEvaluation ResolveConditionConstant(
            string name)
        {
            if (definitions.ContainsKey(name))
            {
                return ResolveDefinition(name);
            }

            if (environment.TryGetGlobalConstant(name, out var value))
            {
                return VbaConditionalCompilationExpressionEvaluation.Success(value);
            }

            // VBA treats names that are undefined in #If/#ElseIf as Empty.
            return VbaConditionalCompilationExpressionEvaluation.Success(
                VbaConditionalCompilationValue.FromInteger(0));
        }

        public bool SupportsLongLong => environment.SupportsLongLong;

        private void TryAddDefinition(VbaPreprocessorDirectiveSyntax directive)
        {
            var body = VbaPreprocessorParser.GetNormalizedDirectiveBody(directive.Text);
            var tokens = SignificantTokens(VbaTokenStream.FromText(body).Tokens);
            if (tokens.Count < 4
                || !tokens[0].Text.Equals("Const", StringComparison.OrdinalIgnoreCase)
                || !VbaIdentifierSyntaxFacts.IsValidDeclaredName(tokens[1])
                || tokens[2].Text != "=")
            {
                diagnostics.Add(new VbaSyntaxDiagnostic(
                    "syntax.conditionalCompilationMalformed",
                    "The conditional-compilation constant directive is malformed.",
                    directive.Range));
                return;
            }

            if (environment.IsBuiltInConstant(tokens[1].Text))
            {
                diagnostics.Add(ToDiagnostic(
                    directive,
                    VbaConditionalCompilationExpressionEvaluation.Failure(
                        VbaConditionalCompilationFailureKind.BuiltInRedefinition,
                        $"Compiler built-in constant '{tokens[1].Text}' cannot be redefined.")));
                return;
            }

            var definition = new ConstantDefinition(
                tokens[1].Text,
                directive,
                tokens,
                ExpressionStart: 3);
            if (!definitions.TryAdd(tokens[1].Text, definition))
            {
                diagnostics.Add(ToDiagnostic(
                    directive,
                    VbaConditionalCompilationExpressionEvaluation.Failure(
                        VbaConditionalCompilationFailureKind.DuplicateDefinition,
                        $"Conditional-compilation constant '{tokens[1].Text}' is duplicated.")));
            }
        }

        private VbaConditionalCompilationExpressionEvaluation ResolveDefinition(string name)
        {
            if (values.TryGetValue(name, out var value))
            {
                return VbaConditionalCompilationExpressionEvaluation.Success(value);
            }

            if (!definitions.TryGetValue(name, out var definition))
            {
                return environment.TryGetGlobalConstant(name, out value)
                    ? VbaConditionalCompilationExpressionEvaluation.Success(value)
                    : VbaConditionalCompilationExpressionEvaluation.Failure(
                        VbaConditionalCompilationFailureKind.Undefined,
                        $"Conditional-compilation constant '{name}' is undefined.");
            }

            if (!evaluating.Add(name))
            {
                return VbaConditionalCompilationExpressionEvaluation.Failure(
                    VbaConditionalCompilationFailureKind.Cycle,
                    $"Conditional-compilation constant '{name}' is cyclic.");
            }

            var evaluation = VbaConstantExpressionSyntax.EvaluateConditionalCompilationExpression(
                definition.Tokens,
                definition.ExpressionStart,
                definition.Tokens.Count,
                ResolveDefinition,
                environment.SupportsLongLong);
            evaluating.Remove(name);
            if (evaluation.Succeeded)
            {
                values.Add(name, evaluation.Value);
            }

            return evaluation;
        }

        private sealed record ConstantDefinition(
            string Name,
            VbaPreprocessorDirectiveSyntax Directive,
            IReadOnlyList<VbaToken> Tokens,
            int ExpressionStart);
    }
}

internal enum VbaConditionalCompilationFailureKind
{
    None,
    Malformed,
    Undefined,
    Cycle,
    BuiltInRedefinition,
    DuplicateDefinition,
    Unsupported,
    Overflow
}

internal readonly record struct VbaConditionalCompilationExpressionEvaluation(
    bool Succeeded,
    VbaConditionalCompilationValue Value,
    VbaConditionalCompilationFailureKind FailureKind,
    string Message)
{
    public static VbaConditionalCompilationExpressionEvaluation Success(
        VbaConditionalCompilationValue value)
        => new(true, value, VbaConditionalCompilationFailureKind.None, string.Empty);

    public static VbaConditionalCompilationExpressionEvaluation Failure(
        VbaConditionalCompilationFailureKind kind,
        string message)
        => new(false, default, kind, message);
}
