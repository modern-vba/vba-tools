using System.Collections.Immutable;
using System.Text;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Debugging;

/// <summary>
/// Admits one transported launch generation as a single source-analysis authority.
/// </summary>
internal sealed class DebugSourceAdmission
{
    private readonly TransportedDebugSourceSnapshotValidator snapshotValidator;
    private readonly Func<string, string, VbaSyntaxTree> parseSource;

    internal DebugSourceAdmission(
        int activeWindowsCodePage,
        Func<string, string, VbaSyntaxTree>? parseSource = null)
        : this(
            new TransportedDebugSourceSnapshotValidator(activeWindowsCodePage),
            parseSource)
    {
    }

    private DebugSourceAdmission(
        TransportedDebugSourceSnapshotValidator snapshotValidator,
        Func<string, string, VbaSyntaxTree>? parseSource)
    {
        this.snapshotValidator = snapshotValidator
            ?? throw new ArgumentNullException(nameof(snapshotValidator));
        this.parseSource = parseSource ?? VbaSyntaxTree.ParseModule;
    }

    internal static DebugSourceAdmission CreateForCurrentWindowsSession()
        => new(
            TransportedDebugSourceSnapshotValidator.CreateForCurrentWindowsSession(),
            parseSource: null);

    internal AdmittedDebugSourceSnapshot Admit(
        TransportedDebugSourceSnapshot snapshot,
        string? moduleName,
        string? procedureName,
        DebugGenerationId generationId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(generationId);

        var frozenTransport = Freeze(snapshot);
        var validatedSnapshot = snapshotValidator.Validate(frozenTransport);
        ValidateRequestedTarget(moduleName, procedureName);

        var activeSource = validatedSnapshot.ActiveSource is null
            ? null
            : new DebugSourcePosition(
                validatedSnapshot.ActiveSource.SourceUri,
                validatedSnapshot.ActiveSource.Line,
                validatedSnapshot.ActiveSource.Character);
        var parsedSources = validatedSnapshot.Sources
            .Where(source => source.Text is not null)
            .Select(Parse)
            .ToImmutableArray();
        var index = AdmissionIndex.Create(parsedSources);
        var resolvedTarget = moduleName is not null
            ? ResolveExplicitTarget(index, moduleName, procedureName!)
            : ResolveActiveTarget(index, activeSource);
        ValidateTargetUniquenessAndEligibility(index, resolvedTarget);
        if (!VbaConditionalCompilationBranchFacts.TryGetPath(
                resolvedTarget.Source.SyntaxTree,
                resolvedTarget.Callable.Range,
                requireCompleteStructure: true,
                out var targetConditionalPath))
        {
            throw new DebugSetupException(
                $"VBA debug target '{resolvedTarget.Source.SyntaxTree.Module.Identity.Name}." +
                $"{resolvedTarget.Callable.Name}' has no complete conditional-compilation branch identity.");
        }

        var target = new DebugTargetProcedure(
            resolvedTarget.Source.SyntaxTree.Module.Identity.Name,
            resolvedTarget.Callable.Name)
        {
            ConditionalCompilationPath = targetConditionalPath
        };
        var mappedEvidence = validatedSnapshot.Breakpoints
            .Select(breakpoint => MapBreakpoint(
                index,
                new DebugSourceBreakpoint(breakpoint.SourceUri, breakpoint.Line)))
            .ToImmutableArray();

        ValidateCompleteSourceIdentities(index);
        var buildSources = new AdmittedDebugBuildSourceSet(
            generationId,
            validatedSnapshot.Sources);
        var proof = DeferredDebugConditionalCompilationProof.Create(
            generationId,
            target,
            resolvedTarget.Source.SyntaxTree,
            mappedEvidence.Select(evidence => (
                evidence.Breakpoint,
                evidence.Source.SyntaxTree)));
        return new AdmittedDebugSourceSnapshot(
            generationId,
            activeSource,
            target,
            mappedEvidence.Select(evidence => evidence.Breakpoint).ToImmutableArray(),
            buildSources,
            proof);
    }

    private ParsedDebugSource Parse(ValidatedTransportedDebugSource source)
    {
        var syntaxTree = parseSource(source.SourceUri!, source.Text!);
        return new ParsedDebugSource(source.SourceUri!, syntaxTree);
    }

    private static TransportedDebugSourceSnapshot Freeze(
        TransportedDebugSourceSnapshot snapshot)
        => new(
            snapshot.SchemaVersion,
            snapshot.Sources.Select(source => source with { }).ToImmutableArray())
        {
            ActiveSource = snapshot.ActiveSource is null
                ? null
                : snapshot.ActiveSource with { },
            Breakpoints = snapshot.Breakpoints
                .Select(breakpoint => breakpoint with { })
                .ToImmutableArray()
        };

    private static void ValidateRequestedTarget(
        string? moduleName,
        string? procedureName)
    {
        var hasModule = moduleName is not null;
        var hasProcedure = procedureName is not null;
        if (hasModule != hasProcedure)
        {
            throw new DebugSetupException(
                "The VBA launch request must specify 'module' and 'procedure' together.");
        }
        if (hasModule)
        {
            ValidateExplicitIdentifier("module", moduleName!, maximumLength: 31);
            ValidateExplicitIdentifier("procedure", procedureName!, maximumLength: 255);
        }
    }

    private static void ValidateExplicitIdentifier(
        string fieldName,
        string value,
        int maximumLength)
    {
        if (!VbaIdentifier.IsIdentifier(value) ||
            value.EnumerateRunes().Take(maximumLength + 1).Count() > maximumLength)
        {
            throw new DebugSetupException(
                $"The VBA launch request '{fieldName}' must be an exact MS-VBAL IDENTIFIER " +
                $"between 1 and {maximumLength} characters.");
        }
    }

    private static ResolvedDebugTarget ResolveExplicitTarget(
        AdmissionIndex index,
        string moduleName,
        string procedureName)
    {
        var moduleMatches = index.GetSourcesByModuleName(moduleName);
        if (moduleMatches.Length != 1)
        {
            throw new DebugSetupException(moduleMatches.Length == 0
                ? $"VBA debug module '{moduleName}' was not found in the selected document source snapshot."
                : $"VBA debug module '{moduleName}' is ambiguous in the selected document source snapshot.");
        }

        var callableMatches = moduleMatches[0].SyntaxTree.Module.CallableDeclarations
            .Where(callable => callable.Name.Equals(
                procedureName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (callableMatches.Length != 1)
        {
            throw new DebugSetupException(callableMatches.Length == 0
                ? $"VBA debug procedure '{moduleName}.{procedureName}' was not found in the selected document source snapshot."
                : $"VBA debug procedure '{moduleName}.{procedureName}' is ambiguous in the selected document source snapshot.");
        }

        return new ResolvedDebugTarget(moduleMatches[0], callableMatches[0]);
    }

    private static ResolvedDebugTarget ResolveActiveTarget(
        AdmissionIndex index,
        DebugSourcePosition? activeSource)
    {
        if (activeSource is null)
        {
            throw new DebugSetupException(
                "The VBA launch request requires 'sourceSnapshot.activeSource' " +
                "when module and procedure are omitted.");
        }

        var sourceMatches = index.GetSourcesByUri(activeSource.SourceUri);
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Active VBA source '{activeSource.SourceUri}' is not present in the selected document source snapshot."
                : $"Active VBA source '{activeSource.SourceUri}' is ambiguous in the selected document source snapshot.");
        }

        var source = sourceMatches[0];
        var lines = source.SyntaxTree.SourceText.Lines;
        if (activeSource.Line < 0 || activeSource.Line >= lines.Count)
        {
            throw new DebugSetupException(
                $"Active VBA source line {activeSource.Line} is outside '{activeSource.SourceUri}'.");
        }
        var line = lines[activeSource.Line];
        if (activeSource.Character < 0 || activeSource.Character > line.Text.Length)
        {
            throw new DebugSetupException(
                $"Active VBA source character {activeSource.Character} is outside line " +
                $"{activeSource.Line} in '{activeSource.SourceUri}'.");
        }

        var offset = line.StartOffset + activeSource.Character;
        var callableMatches = source.SyntaxTree.Module.CallableDeclarations
            .Where(callable => callable.BlockRange.Start.Offset <= offset &&
                               offset <= callable.BlockRange.End.Offset)
            .ToArray();
        if (callableMatches.Length != 1)
        {
            throw new DebugSetupException(callableMatches.Length == 0
                ? $"Active VBA position {activeSource.Line}:{activeSource.Character} is not inside a procedure in '{activeSource.SourceUri}'."
                : $"Active VBA position {activeSource.Line}:{activeSource.Character} is ambiguous in '{activeSource.SourceUri}'.");
        }

        return new ResolvedDebugTarget(source, callableMatches[0]);
    }

    private static void ValidateTargetUniquenessAndEligibility(
        AdmissionIndex index,
        ResolvedDebugTarget target)
    {
        var moduleName = target.Source.SyntaxTree.Module.Identity.Name;
        var procedureName = target.Callable.Name;
        if (index.GetSourcesByModuleName(moduleName).Length != 1)
        {
            throw new DebugSetupException(
                $"VBA debug module '{moduleName}' is ambiguous in the selected document source snapshot.");
        }
        if (target.Source.SyntaxTree.Module.CallableDeclarations.Count(callable =>
                callable.Name.Equals(procedureName, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new DebugSetupException(
                $"VBA debug procedure '{moduleName}.{procedureName}' is ambiguous " +
                "in the selected document source snapshot.");
        }
        if (target.Source.SyntaxTree.Module.Kind != VbaModuleKind.StandardModule)
        {
            throw new DebugSetupException(
                $"VBA debug module '{moduleName}' is not a standard module; " +
                "class, form, and document modules cannot contain a debug target.");
        }
        if (!string.Equals(
                target.Callable.DeclarationKeyword,
                "Sub",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not a Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        var publicKeyword = string.IsNullOrEmpty(target.Callable.VisibilityKeyword) ||
            target.Callable.VisibilityKeyword.Equals("Public", StringComparison.OrdinalIgnoreCase);
        if (target.Callable.Visibility != VbaDeclarationVisibility.Public || !publicKeyword)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not public; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        if (target.Callable.Parameters.Count != 0)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not parameterless; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        if (target.Callable.IsExternal)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is an external Declare Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
    }

    private static MappedBreakpointEvidence MapBreakpoint(
        AdmissionIndex index,
        DebugSourceBreakpoint breakpoint)
    {
        var sourceMatches = index.GetSourcesByUri(breakpoint.SourceUri);
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Debug breakpoint source '{breakpoint.SourceUri}' is not present in the source snapshot."
                : $"Debug breakpoint source '{breakpoint.SourceUri}' is ambiguous in the source snapshot.");
        }

        var source = sourceMatches[0];
        if (source.SyntaxTree.Module.Identity.Metadata?.IsAuthoritative != true)
        {
            throw new DebugSetupException(
                $"Debug breakpoint source '{source.SourceUri}' does not contain exactly one " +
                "valid exported module identity.");
        }

        var ambiguousIdentity = index.FirstAmbiguousAuthoritativeIdentity;
        if (ambiguousIdentity is not null)
        {
            throw new DebugSetupException(
                "Invalid breakpoint setup: exported module identity " +
                $"'{ambiguousIdentity}' is ambiguous in the source snapshot.");
        }

        return new MappedBreakpointEvidence(
            source.BreakpointProjection.Map(breakpoint),
            source);
    }

    private static void ValidateCompleteSourceIdentities(
        AdmissionIndex index)
    {
        foreach (var source in index.Sources)
        {
            if (source.SyntaxTree.Module.Identity.Metadata?.IsAuthoritative != true)
            {
                throw new DebugSetupException(
                    $"Debug source '{source.SourceUri}' does not contain exactly one " +
                    "valid exported module identity.");
            }
        }

        var ambiguousIdentity = index.FirstAmbiguousAuthoritativeIdentity;
        if (ambiguousIdentity is not null)
        {
            throw new DebugSetupException(
                "Debug source snapshot contains ambiguous exported module identity " +
                $"'{ambiguousIdentity}'.");
        }
    }

    private sealed class ParsedDebugSource
    {
        private DebugBreakpointProjection? breakpointProjection;

        internal ParsedDebugSource(
            string sourceUri,
            VbaSyntaxTree syntaxTree)
        {
            SourceUri = sourceUri;
            SyntaxTree = syntaxTree;
        }

        internal string SourceUri { get; }

        internal VbaSyntaxTree SyntaxTree { get; }

        internal DebugBreakpointProjection BreakpointProjection =>
            breakpointProjection ??= DebugBreakpointProjection.Create(SyntaxTree);
    }

    private sealed class AdmissionIndex
    {
        private readonly IReadOnlyDictionary<string, ImmutableArray<ParsedDebugSource>>
            sourcesByUri;
        private readonly IReadOnlyDictionary<string, ImmutableArray<ParsedDebugSource>>
            sourcesByModuleName;

        private AdmissionIndex(
            ImmutableArray<ParsedDebugSource> sources,
            IReadOnlyDictionary<string, ImmutableArray<ParsedDebugSource>> sourcesByUri,
            IReadOnlyDictionary<string, ImmutableArray<ParsedDebugSource>> sourcesByModuleName,
            string? firstAmbiguousAuthoritativeIdentity)
        {
            Sources = sources;
            this.sourcesByUri = sourcesByUri;
            this.sourcesByModuleName = sourcesByModuleName;
            FirstAmbiguousAuthoritativeIdentity = firstAmbiguousAuthoritativeIdentity;
        }

        internal ImmutableArray<ParsedDebugSource> Sources { get; }

        internal string? FirstAmbiguousAuthoritativeIdentity { get; }

        internal static AdmissionIndex Create(
            ImmutableArray<ParsedDebugSource> sources)
        {
            var authoritativeIdentityCounts = sources
                .Where(source =>
                    source.SyntaxTree.Module.Identity.Metadata?.IsAuthoritative == true)
                .GroupBy(
                    source => source.SyntaxTree.Module.Identity.Metadata!.Name!,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.OrdinalIgnoreCase);
            var firstAmbiguousAuthoritativeIdentity = sources
                .Where(source =>
                    source.SyntaxTree.Module.Identity.Metadata?.IsAuthoritative == true)
                .Select(source => source.SyntaxTree.Module.Identity.Metadata!.Name!)
                .FirstOrDefault(name => authoritativeIdentityCounts[name] > 1);
            return new AdmissionIndex(
                sources,
                CreateIndex(sources, source => source.SourceUri),
                CreateIndex(sources, source => source.SyntaxTree.Module.Identity.Name),
                firstAmbiguousAuthoritativeIdentity);
        }

        internal ImmutableArray<ParsedDebugSource> GetSourcesByUri(string sourceUri)
            => sourcesByUri.TryGetValue(sourceUri, out var matches)
                ? matches
                : [];

        internal ImmutableArray<ParsedDebugSource> GetSourcesByModuleName(string moduleName)
            => sourcesByModuleName.TryGetValue(moduleName, out var matches)
                ? matches
                : [];

        private static IReadOnlyDictionary<string, ImmutableArray<ParsedDebugSource>>
            CreateIndex(
                IEnumerable<ParsedDebugSource> sources,
                Func<ParsedDebugSource, string> getKey)
            => sources
                .GroupBy(getKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToImmutableArray(),
                    StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ResolvedDebugTarget(
        ParsedDebugSource Source,
        VbaCallableDeclarationSyntax Callable);

    private sealed record MappedBreakpointEvidence(
        VbeBreakpoint Breakpoint,
        ParsedDebugSource Source);
}

/// <summary>
/// Holds all source-derived facts for one admitted debug generation.
/// </summary>
internal sealed class AdmittedDebugSourceSnapshot
{
    private readonly DeferredDebugConditionalCompilationProof conditionalCompilationProof;

    internal AdmittedDebugSourceSnapshot(
        DebugGenerationId generationId,
        DebugSourcePosition? activeSource,
        DebugTargetProcedure target,
        ImmutableArray<VbeBreakpoint> mappedBreakpoints,
        AdmittedDebugBuildSourceSet buildSources,
        DeferredDebugConditionalCompilationProof conditionalCompilationProof)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(buildSources);
        ArgumentNullException.ThrowIfNull(conditionalCompilationProof);
        if (buildSources.GenerationId != generationId ||
            conditionalCompilationProof.GenerationId != generationId)
        {
            throw new ArgumentException(
                "The admitted build source set must belong to the same debug generation.",
                nameof(buildSources));
        }

        GenerationId = generationId;
        ActiveSource = activeSource;
        Target = target;
        MappedBreakpoints = mappedBreakpoints;
        BuildSources = buildSources;
        this.conditionalCompilationProof = conditionalCompilationProof;
    }

    internal DebugGenerationId GenerationId { get; }

    internal DebugSourcePosition? ActiveSource { get; }

    internal DebugTargetProcedure Target { get; }

    internal ImmutableArray<VbeBreakpoint> MappedBreakpoints { get; }

    internal AdmittedDebugBuildSourceSet BuildSources { get; }

    internal bool RequiresConditionalCompilationVerification =>
        conditionalCompilationProof.IsRequired;

    internal void VerifyConditionalCompilation(
        VbaConditionalCompilationEnvironment environment)
        => conditionalCompilationProof.Verify(environment);
}

/// <summary>
/// Defers branch-activity verification until the built workbook supplies its actual environment.
/// </summary>
internal sealed class DeferredDebugConditionalCompilationProof
{
    internal DebugGenerationId GenerationId { get; }

    private readonly ConditionalTarget? target;
    private readonly ImmutableArray<ConditionalBreakpoint> breakpoints;

    private DeferredDebugConditionalCompilationProof(
        DebugGenerationId generationId,
        ConditionalTarget? target,
        ImmutableArray<ConditionalBreakpoint> breakpoints)
    {
        GenerationId = generationId;
        this.target = target;
        this.breakpoints = breakpoints;
    }

    internal bool IsRequired => target is not null || !breakpoints.IsEmpty;

    internal static DeferredDebugConditionalCompilationProof Create(
        DebugGenerationId generationId,
        DebugTargetProcedure target,
        VbaSyntaxTree targetSyntaxTree,
        IEnumerable<(VbeBreakpoint Breakpoint, VbaSyntaxTree SyntaxTree)>
            mappedBreakpoints)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetSyntaxTree);
        ArgumentNullException.ThrowIfNull(mappedBreakpoints);
        return new DeferredDebugConditionalCompilationProof(
            generationId,
            target.ConditionalCompilationPath.Branches.Count == 0
                ? null
                : new ConditionalTarget(target, targetSyntaxTree),
            mappedBreakpoints
                .Where(evidence =>
                    evidence.Breakpoint.ConditionalCompilationPath.Branches.Count != 0)
                .Select(evidence => new ConditionalBreakpoint(
                    evidence.Breakpoint,
                    evidence.SyntaxTree))
                .ToImmutableArray());
    }

    internal void Verify(VbaConditionalCompilationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsRequired)
        {
            return;
        }

        var evaluations = new Dictionary<string, VbaConditionalCompilationEvaluation>(
            StringComparer.OrdinalIgnoreCase);
        if (target is not null)
        {
            var evaluation = Evaluate(
                target.SyntaxTree.Uri,
                target.SyntaxTree,
                environment,
                evaluations);
            ThrowEvaluationFailureForTarget(target.Target, evaluation);
            if (!evaluation.IsActive(target.Target.ConditionalCompilationPath))
            {
                throw new DebugSetupException(
                    $"VBA debug target '{target.Target.ModuleName}.{target.Target.ProcedureName}' is inactive " +
                    "in the actual generated workbook compilation context.");
            }
        }

        foreach (var breakpoint in breakpoints)
        {
            var evaluation = Evaluate(
                breakpoint.SyntaxTree.Uri,
                breakpoint.SyntaxTree,
                environment,
                evaluations);
            ThrowEvaluationFailureForBreakpoint(breakpoint.Breakpoint, evaluation);
            if (!evaluation.IsActive(breakpoint.Breakpoint.ConditionalCompilationPath))
            {
                throw InvalidBreakpoint(
                    breakpoint.Breakpoint,
                    "its physical source line is inactive in the actual generated workbook compilation context");
            }
        }
    }

    private static VbaConditionalCompilationEvaluation Evaluate(
        string sourceUri,
        VbaSyntaxTree syntaxTree,
        VbaConditionalCompilationEnvironment environment,
        IDictionary<string, VbaConditionalCompilationEvaluation> evaluations)
    {
        if (evaluations.TryGetValue(sourceUri, out var existing))
        {
            return existing;
        }

        var evaluation = VbaConditionalCompilationEvaluator.Evaluate(syntaxTree, environment);
        evaluations.Add(sourceUri, evaluation);
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
            "could not be proved in the actual generated workbook compilation context: " +
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
            $"Invalid breakpoint at '{breakpoint.Source.SourceUri}:{breakpoint.Source.EditorLine + 1}': " +
            $"{reason}. The breakpoint was not relocated.");

    private static string DescribeDiagnostics(
        VbaConditionalCompilationEvaluation evaluation)
        => string.Join(
            "; ",
            evaluation.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private sealed record ConditionalTarget(
        DebugTargetProcedure Target,
        VbaSyntaxTree SyntaxTree);

    private sealed record ConditionalBreakpoint(
        VbeBreakpoint Breakpoint,
        VbaSyntaxTree SyntaxTree);
}
