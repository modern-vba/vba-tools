namespace VbaLanguageServer.Syntax;

/// <summary>
/// Identifies one nested conditional-compilation branch path in a syntax snapshot.
/// </summary>
public sealed class VbaConditionalCompilationBranchPath
    : IEquatable<VbaConditionalCompilationBranchPath>
{
    private static readonly VbaConditionalCompilationBranchPath EmptyPath = new([]);
    private readonly IReadOnlyList<VbaConditionalCompilationBranchIdentity> branches;

    internal VbaConditionalCompilationBranchPath(
        IEnumerable<VbaConditionalCompilationBranchIdentity> branches)
    {
        this.branches = Array.AsReadOnly(branches.ToArray());
    }

    /// <summary>
    /// Gets the outermost-to-innermost branch identities.
    /// </summary>
    public IReadOnlyList<VbaConditionalCompilationBranchIdentity> Branches => branches;

    /// <summary>
    /// Gets the root path used by source outside conditional-compilation branches.
    /// </summary>
    public static VbaConditionalCompilationBranchPath Root => EmptyPath;

    /// <summary>
    /// Gets whether the position is outside conditional compilation.
    /// </summary>
    public bool IsEmpty => branches.Count == 0;

    /// <summary>
    /// Determines whether this path is an outer prefix of another path.
    /// </summary>
    public bool IsPrefixOf(VbaConditionalCompilationBranchPath other)
        => branches.Count <= other.branches.Count
            && branches.SequenceEqual(other.branches.Take(branches.Count));

    /// <inheritdoc />
    public bool Equals(VbaConditionalCompilationBranchPath? other)
        => other is not null && branches.SequenceEqual(other.branches);

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is VbaConditionalCompilationBranchPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var branch in branches)
        {
            hash.Add(branch);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares two branch paths by structural identity.
    /// </summary>
    public static bool operator ==(
        VbaConditionalCompilationBranchPath? left,
        VbaConditionalCompilationBranchPath? right)
        => EqualityComparer<VbaConditionalCompilationBranchPath>.Default.Equals(
            left,
            right);

    /// <summary>
    /// Compares two branch paths by structural identity.
    /// </summary>
    public static bool operator !=(
        VbaConditionalCompilationBranchPath? left,
        VbaConditionalCompilationBranchPath? right)
        => !(left == right);
}

/// <summary>
/// Identifies one branch by the stable directive offsets that precede its body.
/// </summary>
/// <param name="IfDirectiveOffset">The containing #If directive start offset.</param>
/// <param name="BranchDirectiveOffset">The active #If, #ElseIf, or #Else directive start offset.</param>
public readonly record struct VbaConditionalCompilationBranchIdentity(
    int IfDirectiveOffset,
    int BranchDirectiveOffset);

/// <summary>
/// Identifies the directive that closes one conditional-compilation branch body.
/// </summary>
/// <param name="Kind">The closing branch or block directive kind.</param>
/// <param name="Range">The exact directive source range.</param>
public sealed record VbaConditionalCompilationBoundary(
    VbaPreprocessorDirectiveKind Kind,
    VbaSyntaxRange Range);

/// <summary>
/// Resolves and validates conditional-compilation branch-local ownership.
/// </summary>
public static class VbaConditionalCompilationBranchFacts
{
    private const int MaximumConditionalCompilationDepth = 128;
    private const string MalformedDirectiveDiagnosticCode =
        "syntax.malformedPreprocessorDirective";

    /// <summary>
    /// Determines whether two paths can be active in the same compilation.
    /// </summary>
    public static bool CanCoexist(
        VbaConditionalCompilationBranchPath left,
        VbaConditionalCompilationBranchPath right)
    {
        var commonDepth = Math.Min(left.Branches.Count, right.Branches.Count);
        for (var index = 0; index < commonDepth; index++)
        {
            var leftBranch = left.Branches[index];
            var rightBranch = right.Branches[index];
            if (leftBranch == rightBranch)
            {
                continue;
            }

            return leftBranch.IfDirectiveOffset != rightBranch.IfDirectiveOffset;
        }

        return true;
    }

    internal static bool CanRangeAffectPath(
        VbaSyntaxTree tree,
        VbaSyntaxRange range,
        VbaConditionalCompilationBranchPath selectedPath,
        bool requireCompleteStructure)
        => !TryGetPath(
                tree,
                range,
                requireCompleteStructure,
                out var path)
            || CanCoexist(path, selectedPath);

    internal static bool CanMalformedBarrierAffectPath(
        VbaSyntaxTree tree,
        VbaBlockSyntax barrier,
        VbaConditionalCompilationBranchPath selectedPath,
        bool requireCompleteStructure)
        => CanRangeAffectPath(
                tree,
                barrier.OpenerRange,
                selectedPath,
                requireCompleteStructure)
            && (barrier.MalformedBarrierOwnerRange is null
                || CanRangeAffectPath(
                    tree,
                    barrier.MalformedBarrierOwnerRange,
                    selectedPath,
                    requireCompleteStructure));

    /// <summary>
    /// Resolves the exact branch path containing a source range.
    /// </summary>
    public static bool TryGetPath(
        VbaSyntaxTree tree,
        VbaSyntaxRange range,
        bool requireCompleteStructure,
        out VbaConditionalCompilationBranchPath path)
    {
        path = default!;
        if (range.Start.Offset < 0
            || range.End.Offset < range.Start.Offset
            || tree.Module.PreprocessorDirectives.Any(directive =>
                Overlaps(range, directive.Range))
            || HasUnclosedOrphanBranchBefore(tree, range.Start.Offset))
        {
            return false;
        }

        var branches = new List<VbaConditionalCompilationBranchIdentity>();
        if (!TryAppendContainingPath(
            tree,
            tree.Module.PreprocessorBlocks,
            range,
            requireCompleteStructure,
            depth: 0,
            branches))
        {
            return false;
        }

        path = new VbaConditionalCompilationBranchPath(branches);
        return true;
    }

    internal static bool TryGetStructuralPath(
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaSyntaxRange range,
        out VbaConditionalCompilationBranchPath path)
    {
        var branches = new List<VbaConditionalCompilationBranchIdentity>();
        if (!TryAppendStructuralContainingPath(
            blocks,
            range,
            depth: 0,
            branches))
        {
            path = default!;
            return false;
        }

        path = new VbaConditionalCompilationBranchPath(branches);
        return true;
    }

    internal static bool TryGetStructuralClosingDirective(
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaConditionalCompilationBranchPath path,
        out VbaPreprocessorDirectiveSyntax? directive)
    {
        directive = null;
        if (path.IsEmpty)
        {
            return true;
        }

        if (!TryResolveInnermostBranch(
            blocks,
            path,
            out var block,
            out var branchIndex))
        {
            return false;
        }

        directive = branchIndex + 1 < block.Branches.Count
            ? block.Branches[branchIndex + 1].Directive
            : block.EndDirective;
        return true;
    }

    private static bool HasUnclosedOrphanBranchBefore(
        VbaSyntaxTree tree,
        int offset)
    {
        var conditionalDepth = 0;
        var hasOrphanBranch = false;
        foreach (var directive in tree.Module.PreprocessorDirectives
            .Where(directive => directive.Range.Start.Offset < offset)
            .OrderBy(directive => directive.Range.Start.Offset))
        {
            var isMalformed = HasMalformedDirectiveDiagnostic(tree, directive);
            switch (directive.Kind)
            {
                case VbaPreprocessorDirectiveKind.If:
                    conditionalDepth++;
                    break;

                case VbaPreprocessorDirectiveKind.ElseIf:
                case VbaPreprocessorDirectiveKind.Else:
                    if (conditionalDepth == 0)
                    {
                        hasOrphanBranch = true;
                    }

                    break;

                case VbaPreprocessorDirectiveKind.EndIf:
                    if (isMalformed)
                    {
                        break;
                    }

                    if (conditionalDepth > 0)
                    {
                        conditionalDepth--;
                    }
                    else
                    {
                        hasOrphanBranch = false;
                    }

                    break;
            }
        }

        return hasOrphanBranch;
    }

    private static bool TryAppendStructuralContainingPath(
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaSyntaxRange range,
        int depth,
        ICollection<VbaConditionalCompilationBranchIdentity> path)
    {
        var containingBlocks = blocks
            .Where(block => ContainsBodyRange(block, range))
            .Take(2)
            .ToArray();
        if (containingBlocks.Length == 0)
        {
            return true;
        }

        if (containingBlocks.Length != 1 || depth >= MaximumConditionalCompilationDepth)
        {
            return false;
        }

        var block = containingBlocks[0];
        var branchMatches = block.Branches
            .Select((branch, index) => (branch, index))
            .Where(item => ContainsBranchBody(block, item.index, range))
            .Take(2)
            .ToArray();
        if (branchMatches.Length != 1)
        {
            return false;
        }

        var selected = branchMatches[0];
        path.Add(new VbaConditionalCompilationBranchIdentity(
            block.IfDirective.Range.Start.Offset,
            selected.branch.Directive.Range.Start.Offset));
        return TryAppendStructuralContainingPath(
            selected.branch.NestedBlocks,
            range,
            depth + 1,
            path);
    }

    /// <summary>
    /// Determines whether a range belongs to an expected branch path.
    /// </summary>
    public static bool HasPath(
        VbaSyntaxTree tree,
        VbaSyntaxRange range,
        VbaConditionalCompilationBranchPath expected,
        bool requireCompleteStructure)
        => TryGetPath(tree, range, requireCompleteStructure, out var actual)
            && actual.Equals(expected);

    /// <summary>
    /// Proves that a structured VBA block remains inside its owning branch envelope.
    /// </summary>
    public static bool IsBlockLocal(
        VbaSyntaxTree tree,
        VbaBlockSyntax block,
        VbaConditionalCompilationBranchPath expected,
        bool requireCompleteStructure)
    {
        if (!HasPath(tree, block.OpenerRange, expected, requireCompleteStructure)
            || block.CloserRange is { } closer
                && !HasPath(tree, closer, expected, requireCompleteStructure))
        {
            return false;
        }

        if (!block.Branches.All(branch =>
                HasPath(
                    tree,
                    branch.HeaderRange,
                    expected,
                    requireCompleteStructure))
            || !TryGetBranchEnvelope(
                tree,
                expected,
                requireCompleteStructure,
                out var envelopeStart,
                out var envelopeEnd)
            || !Contains(envelopeStart, envelopeEnd, block.Range))
        {
            return false;
        }

        return block.Branches.All(branch =>
            Contains(envelopeStart, envelopeEnd, branch.Range));
    }

    /// <summary>
    /// Resolves the next directive that closes the innermost selected branch.
    /// </summary>
    public static bool TryGetClosingBoundary(
        VbaSyntaxTree tree,
        VbaConditionalCompilationBranchPath path,
        int line,
        out VbaConditionalCompilationBoundary boundary)
    {
        boundary = default!;
        if (path.IsEmpty
            || path.Branches.Count > MaximumConditionalCompilationDepth
            || !TryResolveInnermostBranch(
                tree.Module.PreprocessorBlocks,
                path,
                out var block,
                out var branchIndex)
            || !ValidateBlock(tree, block, requireCompleteStructure: true))
        {
            return false;
        }

        var directive = branchIndex + 1 < block.Branches.Count
            ? block.Branches[branchIndex + 1].Directive
            : block.EndDirective;
        if (directive is null
            || directive.Kind is not VbaPreprocessorDirectiveKind.ElseIf
                and not VbaPreprocessorDirectiveKind.Else
                and not VbaPreprocessorDirectiveKind.EndIf
            || directive.Range.Start.Line != line)
        {
            return false;
        }

        boundary = new VbaConditionalCompilationBoundary(
            directive.Kind,
            directive.Range);
        return true;
    }

    private static bool TryAppendContainingPath(
        VbaSyntaxTree tree,
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaSyntaxRange range,
        bool requireCompleteStructure,
        int depth,
        ICollection<VbaConditionalCompilationBranchIdentity> path)
    {
        var containingBlocks = blocks
            .Where(block => ContainsBodyRange(block, range))
            .Take(2)
            .ToArray();
        if (containingBlocks.Length == 0)
        {
            return true;
        }

        if (containingBlocks.Length != 1)
        {
            return false;
        }

        var block = containingBlocks[0];
        if (depth >= MaximumConditionalCompilationDepth
            || !ValidateBlock(tree, block, requireCompleteStructure))
        {
            return false;
        }

        var branchMatches = block.Branches
            .Select((branch, index) => (branch, index))
            .Where(item => ContainsBranchBody(block, item.index, range))
            .Take(2)
            .ToArray();
        if (branchMatches.Length != 1)
        {
            return false;
        }

        var selected = branchMatches[0];
        if (HasDisqualifyingBranchDiagnostic(
            tree,
            block,
            selected.index,
            requireCompleteStructure))
        {
            return false;
        }

        path.Add(new VbaConditionalCompilationBranchIdentity(
            block.IfDirective.Range.Start.Offset,
            selected.branch.Directive.Range.Start.Offset));
        return TryAppendContainingPath(
            tree,
            selected.branch.NestedBlocks,
            range,
            requireCompleteStructure,
            depth + 1,
            path);
    }

    private static bool TryResolveInnermostBranch(
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaConditionalCompilationBranchPath path,
        out VbaPreprocessorBlockSyntax block,
        out int branchIndex)
    {
        block = default!;
        branchIndex = -1;
        if (path.Branches.Count > MaximumConditionalCompilationDepth)
        {
            return false;
        }

        var candidates = blocks;
        foreach (var identity in path.Branches)
        {
            var blockMatches = candidates
                .Where(candidate =>
                    candidate.IfDirective.Range.Start.Offset == identity.IfDirectiveOffset)
                .Take(2)
                .ToArray();
            if (blockMatches.Length != 1)
            {
                return false;
            }

            block = blockMatches[0];
            var branchMatches = block.Branches
                .Select((branch, index) => (branch, index))
                .Where(item =>
                    item.branch.Directive.Range.Start.Offset
                        == identity.BranchDirectiveOffset)
                .Take(2)
                .ToArray();
            if (branchMatches.Length != 1)
            {
                return false;
            }

            branchIndex = branchMatches[0].index;
            candidates = branchMatches[0].branch.NestedBlocks;
        }

        return branchIndex >= 0;
    }

    private static bool ValidateBlock(
        VbaSyntaxTree tree,
        VbaPreprocessorBlockSyntax block,
        bool requireCompleteStructure)
    {
        if (block.Branches.Count == 0
            || block.Branches[0].Directive.Kind != VbaPreprocessorDirectiveKind.If
            || requireCompleteStructure && block.EndDirective is null
            || HasMalformedDirectiveDiagnostic(tree, block.IfDirective)
            || block.EndDirective is { } endDirective
                && HasMalformedDirectiveDiagnostic(tree, endDirective)
            || block.Branches.Any(branch =>
                HasMalformedDirectiveDiagnostic(tree, branch.Directive)))
        {
            return false;
        }

        var hasElse = false;
        for (var index = 1; index < block.Branches.Count; index++)
        {
            var kind = block.Branches[index].Directive.Kind;
            if (kind == VbaPreprocessorDirectiveKind.Else)
            {
                if (hasElse)
                {
                    return false;
                }

                hasElse = true;
                continue;
            }

            if (kind != VbaPreprocessorDirectiveKind.ElseIf || hasElse)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasMalformedDirectiveDiagnostic(
        VbaSyntaxTree tree,
        VbaPreprocessorDirectiveSyntax directive)
        => tree.Diagnostics.Any(diagnostic =>
            diagnostic.Code.Equals(
                MalformedDirectiveDiagnosticCode,
                StringComparison.Ordinal)
            && diagnostic.Range == directive.Range);

    private static bool HasDisqualifyingBranchDiagnostic(
        VbaSyntaxTree tree,
        VbaPreprocessorBlockSyntax block,
        int branchIndex,
        bool requireCompleteStructure)
    {
        var startOffset = block.Branches[branchIndex].Directive.Range.End.Offset;
        var endOffset = branchIndex + 1 < block.Branches.Count
            ? block.Branches[branchIndex + 1].Directive.Range.Start.Offset
            : block.EndDirective?.Range.Start.Offset
                ?? block.Range.End.Offset;
        return tree.Diagnostics.Any(diagnostic =>
            diagnostic.Code.StartsWith(
                "syntax.malformedPreprocessor",
                StringComparison.Ordinal)
            && diagnostic.Range.Start.Offset < endOffset
            && startOffset < diagnostic.Range.End.Offset
            && (requireCompleteStructure
                || !IsMissingEndIfDiagnostic(diagnostic)));
    }

    private static bool IsMissingEndIfDiagnostic(VbaSyntaxDiagnostic diagnostic)
        => diagnostic.Code.Equals(
                "syntax.malformedPreprocessorNesting",
                StringComparison.Ordinal)
            && diagnostic.Message.Equals(
                "Preprocessor block is missing '#End If'.",
                StringComparison.Ordinal);

    private static bool TryGetBranchEnvelope(
        VbaSyntaxTree tree,
        VbaConditionalCompilationBranchPath path,
        bool requireCompleteStructure,
        out int startOffset,
        out int endOffset)
    {
        if (path.IsEmpty)
        {
            startOffset = tree.SourceText.FullRange.Start.Offset;
            endOffset = tree.SourceText.FullRange.End.Offset;
            return true;
        }

        if (!TryResolveInnermostBranch(
                tree.Module.PreprocessorBlocks,
                path,
                out var block,
                out var branchIndex)
            || !ValidateBlock(tree, block, requireCompleteStructure))
        {
            startOffset = 0;
            endOffset = 0;
            return false;
        }

        startOffset = block.Branches[branchIndex].Directive.Range.End.Offset;
        endOffset = branchIndex + 1 < block.Branches.Count
            ? block.Branches[branchIndex + 1].Directive.Range.Start.Offset
            : block.EndDirective?.Range.Start.Offset
                ?? block.Range.End.Offset;
        return startOffset <= endOffset;
    }

    private static bool ContainsBodyRange(
        VbaPreprocessorBlockSyntax block,
        VbaSyntaxRange range)
    {
        var endOffset = block.EndDirective?.Range.Start.Offset
            ?? block.Range.End.Offset;
        return block.IfDirective.Range.End.Offset <= range.Start.Offset
            && range.End.Offset <= endOffset;
    }

    private static bool ContainsBranchBody(
        VbaPreprocessorBlockSyntax block,
        int branchIndex,
        VbaSyntaxRange range)
    {
        var branch = block.Branches[branchIndex];
        var endOffset = branchIndex + 1 < block.Branches.Count
            ? block.Branches[branchIndex + 1].Directive.Range.Start.Offset
            : block.EndDirective?.Range.Start.Offset
                ?? block.Range.End.Offset;
        return branch.Directive.Range.End.Offset <= range.Start.Offset
            && range.End.Offset <= endOffset;
    }

    private static bool Contains(
        int startOffset,
        int endOffset,
        VbaSyntaxRange range)
        => startOffset <= range.Start.Offset
            && range.End.Offset <= endOffset;

    private static bool Overlaps(VbaSyntaxRange left, VbaSyntaxRange right)
        => left.Start.Offset < right.End.Offset
            && right.Start.Offset < left.End.Offset;
}
