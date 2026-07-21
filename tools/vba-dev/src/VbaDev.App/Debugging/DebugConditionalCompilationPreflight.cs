using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Proves that the selected target and every participating breakpoint belong to active
/// conditional-compilation branches before any native VBE command is issued.
/// </summary>
public sealed class DebugConditionalCompilationPreflight
{
    /// <summary>
    /// Validates all conditional participants against explicit project and host constants.
    /// </summary>
    public void Validate(
        DebugLaunchRequest request,
        IReadOnlyList<VbeBreakpoint> breakpoints,
        VbaConditionalCompilationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(breakpoints);
        ArgumentNullException.ThrowIfNull(environment);

        var conditionalBreakpoints = breakpoints
            .Where(breakpoint => breakpoint.ConditionalCompilationPath.Branches.Count != 0)
            .ToArray();
        var conditionalTarget = request.Target.ConditionalCompilationPath.Branches.Count != 0;
        if (!conditionalTarget && conditionalBreakpoints.Length == 0)
        {
            return;
        }

        var parsedSources = request.SourceSnapshot.Sources
            .Select(source => new ParsedSource(
                source,
                VbaSyntaxTree.ParseModule(new Uri(source.Path).AbsoluteUri, source.Text)))
            .ToArray();
        var evaluations = new Dictionary<string, VbaConditionalCompilationEvaluation>(
            StringComparer.OrdinalIgnoreCase);

        ParsedSource? targetSource = null;
        if (conditionalTarget)
        {
            var targetMatches = parsedSources
                .Where(source => source.Tree.Module.Identity.Name.Equals(
                    request.Target.ModuleName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (targetMatches.Length != 1)
            {
                throw new DebugSetupException(
                    $"The conditional-compilation source for VBA debug target " +
                    $"'{request.Target.ModuleName}.{request.Target.ProcedureName}' is not uniquely present " +
                    "in the saved source snapshot.");
            }

            targetSource = targetMatches[0];
            _ = Evaluate(targetSource, environment, evaluations);
        }

        var breakpointSources = new Dictionary<VbeBreakpoint, ParsedSource>();
        foreach (var breakpoint in conditionalBreakpoints)
        {
            var sourcePath = Path.GetFullPath(breakpoint.Source.SourcePath);
            var sourceMatches = parsedSources
                .Where(source => Path.GetFullPath(source.Source.Path).Equals(
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (sourceMatches.Length != 1)
            {
                throw InvalidBreakpoint(
                    breakpoint,
                    "its conditional-compilation source is not uniquely present in the saved source snapshot");
            }

            var parsedSource = sourceMatches[0];
            breakpointSources.Add(breakpoint, parsedSource);
            _ = Evaluate(parsedSource, environment, evaluations);
        }

        if (targetSource is not null)
        {
            var evaluation = evaluations[targetSource.Source.Path];
            ThrowEvaluationFailureForTarget(request.Target, evaluation);
            if (!evaluation.IsActive(request.Target.ConditionalCompilationPath))
            {
                throw new DebugSetupException(
                    $"VBA debug target '{request.Target.ModuleName}.{request.Target.ProcedureName}' is inactive " +
                    "in the actual generated workbook compilation context.");
            }
        }

        foreach (var breakpoint in conditionalBreakpoints)
        {
            var evaluation = evaluations[breakpointSources[breakpoint].Source.Path];
            ThrowEvaluationFailureForBreakpoint(breakpoint, evaluation);
            if (!evaluation.IsActive(breakpoint.ConditionalCompilationPath))
            {
                throw InvalidBreakpoint(
                    breakpoint,
                    "its physical source line is inactive in the actual generated workbook compilation context");
            }
        }
    }

    private static VbaConditionalCompilationEvaluation Evaluate(
        ParsedSource source,
        VbaConditionalCompilationEnvironment environment,
        IDictionary<string, VbaConditionalCompilationEvaluation> evaluations)
    {
        if (evaluations.TryGetValue(source.Source.Path, out var existing))
        {
            return existing;
        }

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(source.Tree, environment);
        evaluations.Add(source.Source.Path, evaluation);
        return evaluation;
    }

    private static void ThrowEvaluationFailureForTarget(
        DebugTargetProcedure target,
        VbaConditionalCompilationEvaluation evaluation)
    {
        if (evaluation.Succeeded)
        {
            return;
        }

        throw new DebugSetupException(
            $"VBA debug target '{target.ModuleName}.{target.ProcedureName}' conditional compilation " +
            $"could not be proved in the actual generated workbook compilation context: " +
            DescribeDiagnostics(evaluation));
    }

    private static void ThrowEvaluationFailureForBreakpoint(
        VbeBreakpoint breakpoint,
        VbaConditionalCompilationEvaluation evaluation)
    {
        if (evaluation.Succeeded)
        {
            return;
        }

        throw InvalidBreakpoint(
            breakpoint,
            "its conditional compilation could not be proved in the actual generated workbook " +
            $"compilation context: {DescribeDiagnostics(evaluation)}");
    }

    private static DebugSetupException InvalidBreakpoint(
        VbeBreakpoint breakpoint,
        string reason)
        => new(
            $"Invalid breakpoint at '{breakpoint.Source.SourcePath}:{breakpoint.Source.EditorLine + 1}': " +
            $"{reason}. The breakpoint was not relocated.");

    private static string DescribeDiagnostics(VbaConditionalCompilationEvaluation evaluation)
        => string.Join(
            "; ",
            evaluation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private sealed record ParsedSource(
        DebugSourceFileSnapshot Source,
        VbaSyntaxTree Tree);
}
