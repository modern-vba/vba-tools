namespace VbaLanguageServer.Syntax;

/// <summary>
/// Re-parses one changed module member and projects the unaffected syntax into the next source snapshot.
/// </summary>
internal static class VbaSyntaxTreeIncrementalParser
{
    public static bool TryParseModuleMember(
        string uri,
        string source,
        VbaSyntaxTree previousSyntaxTree,
        out VbaSyntaxTreeParseResult result)
    {
        result = default!;
        var previousSource = previousSyntaxTree.SourceText;
        var currentSource = VbaSourceText.Update(source, previousSource);
        if (previousSyntaxTree.Diagnostics.Count > 0
            || previousSyntaxTree.Module.Kind == VbaModuleKind.FormModule
            || !previousSyntaxTree.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase)
            || !TryFindChangedLineRange(
                previousSource,
                currentSource,
                out var oldStartLine,
                out var oldEndLine,
                out var newStartLine,
                out var newEndLine))
        {
            return false;
        }
        var previousMember = FindSingleContainingMember(
            previousSyntaxTree.Module.Members,
            oldStartLine,
            oldEndLine);
        if (previousMember is null
            || TouchesMemberBoundary(previousMember, oldStartLine, oldEndLine))
        {
            return false;
        }

        var lineDelta = currentSource.Lines.Count - previousSource.Lines.Count;
        var currentMemberStartLine = previousMember.BlockRange.Start.Line;
        var currentMemberEndLine = previousMember.BlockRange.End.Line + lineDelta;
        if (currentMemberEndLine >= currentSource.Lines.Count
            || newStartLine <= currentMemberStartLine
            || newEndLine >= currentMemberEndLine
            || HasCrossMemberPreprocessorBlock(previousSyntaxTree.Module.PreprocessorBlocks, previousMember.BlockRange))
        {
            return false;
        }

        var maskedSource = MaskUnchangedMembers(
            source,
            currentSource,
            previousSyntaxTree.Module,
            currentMemberStartLine,
            currentMemberEndLine);
        var parsedMemberTree = VbaSyntaxTreeParser.ParseModule(uri, maskedSource);
        if (parsedMemberTree.Diagnostics.Count > 0)
        {
            return false;
        }

        var currentMember = parsedMemberTree.Module.Members.SingleOrDefault(member =>
            member.BlockRange.Start.Line == currentMemberStartLine
            && member.BlockRange.End.Line == currentMemberEndLine);
        if (currentMember is null
            || currentMember.Kind != previousMember.Kind
            || !currentMember.Name.Equals(previousMember.Name, StringComparison.OrdinalIgnoreCase)
            || !previousSyntaxTree.Module.Identity.Name.Equals(
                parsedMemberTree.Module.Identity.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var syntaxTree = MergeSyntaxTree(
            uri,
            previousSyntaxTree,
            parsedMemberTree,
            previousMember,
            currentMember,
            currentSource);
        var update = new VbaModuleMemberIncrementalUpdate(
            previousMember,
            currentMember,
            oldStartLine,
            oldEndLine,
            newStartLine,
            newEndLine);
        result = new VbaSyntaxTreeParseResult(
            syntaxTree,
            VbaSyntaxTreeParseUpdateKind.ModuleMember,
            update);
        return true;
    }

    private static VbaSyntaxTree MergeSyntaxTree(
        string uri,
        VbaSyntaxTree previousTree,
        VbaSyntaxTree parsedMemberTree,
        VbaModuleMemberSyntax previousMember,
        VbaModuleMemberSyntax currentMember,
        VbaSourceText sourceText)
    {
        var oldRange = previousMember.BlockRange;
        var newRange = currentMember.BlockRange;
        var lineDelta = newRange.End.Line - oldRange.End.Line;
        var offsetDelta = newRange.End.Offset - oldRange.End.Offset;
        var module = previousTree.Module with
        {
            Members = MergeByRange(
                previousTree.Module.Members,
                parsedMemberTree.Module.Members,
                member => member.BlockRange,
                member => Shift(member, lineDelta, offsetDelta),
                oldRange,
                newRange),
            Declarations = MergeByRange(
                previousTree.Module.Declarations,
                parsedMemberTree.Module.Declarations,
                declaration => declaration.Range,
                declaration => Shift(declaration, lineDelta, offsetDelta),
                oldRange,
                newRange),
            CallableDeclarations = MergeByRange(
                previousTree.Module.CallableDeclarations,
                parsedMemberTree.Module.CallableDeclarations,
                declaration => declaration.BlockRange,
                declaration => Shift(declaration, lineDelta, offsetDelta),
                oldRange,
                newRange),
            Statements = MergeByRange(
                previousTree.Module.Statements,
                parsedMemberTree.Module.Statements,
                statement => statement.Range,
                statement => statement with { Range = Shift(statement.Range, lineDelta, offsetDelta) },
                oldRange,
                newRange),
            Expressions = MergeByRange(
                previousTree.Module.Expressions,
                parsedMemberTree.Module.Expressions,
                expression => expression.Range,
                expression => expression with { Range = Shift(expression.Range, lineDelta, offsetDelta) },
                oldRange,
                newRange),
            ArgumentLists = MergeByRange(
                previousTree.Module.ArgumentLists,
                parsedMemberTree.Module.ArgumentLists,
                argumentList => argumentList.Range,
                argumentList => Shift(argumentList, lineDelta, offsetDelta),
                oldRange,
                newRange),
            Blocks = MergeByRange(
                previousTree.Module.Blocks,
                parsedMemberTree.Module.Blocks,
                block => block.Range,
                block => Shift(block, lineDelta, offsetDelta),
                oldRange,
                newRange),
            LineLabels = MergeByRange(
                previousTree.Module.LineLabels,
                parsedMemberTree.Module.LineLabels,
                label => label.Range,
                label => Shift(label, lineDelta, offsetDelta),
                oldRange,
                newRange),
            PreprocessorDirectives = MergeByRange(
                previousTree.Module.PreprocessorDirectives,
                parsedMemberTree.Module.PreprocessorDirectives,
                directive => directive.Range,
                directive => Shift(directive, lineDelta, offsetDelta),
                oldRange,
                newRange),
            PreprocessorBlocks = MergeByRange(
                previousTree.Module.PreprocessorBlocks,
                parsedMemberTree.Module.PreprocessorBlocks,
                block => block.Range,
                block => Shift(block, lineDelta, offsetDelta),
                oldRange,
                newRange),
            Range = sourceText.FullRange
        };
        return new VbaSyntaxTree(
            uri,
            sourceText,
            MergeTokenStreams(
                previousTree.TokenStream,
                parsedMemberTree.TokenStream,
                oldRange,
                newRange,
                lineDelta,
                offsetDelta),
            module,
            []);
    }

    private static IReadOnlyList<T> MergeByRange<T>(
        IReadOnlyList<T> previousItems,
        IReadOnlyList<T> parsedItems,
        Func<T, VbaSyntaxRange> getRange,
        Func<T, T> shift,
        VbaSyntaxRange oldRange,
        VbaSyntaxRange newRange)
    {
        var merged = new List<T>(previousItems.Count + parsedItems.Count);
        var lineDelta = newRange.End.Line - oldRange.End.Line;
        var offsetDelta = newRange.End.Offset - oldRange.End.Offset;
        var requiresShift = lineDelta != 0 || offsetDelta != 0;
        var parsedItemsAdded = false;
        foreach (var item in previousItems)
        {
            var range = getRange(item);
            if (range.End.Offset <= oldRange.Start.Offset)
            {
                merged.Add(item);
                continue;
            }

            if (range.Start.Offset < oldRange.End.Offset)
            {
                continue;
            }

            if (!parsedItemsAdded)
            {
                AddContainedItems(merged, parsedItems, getRange, newRange);
                parsedItemsAdded = true;
            }

            merged.Add(requiresShift ? shift(item) : item);
        }

        if (!parsedItemsAdded)
        {
            AddContainedItems(merged, parsedItems, getRange, newRange);
        }

        return merged;
    }

    private static void AddContainedItems<T>(
        ICollection<T> destination,
        IReadOnlyList<T> items,
        Func<T, VbaSyntaxRange> getRange,
        VbaSyntaxRange containingRange)
    {
        foreach (var item in items)
        {
            if (IsContainedBy(getRange(item), containingRange))
            {
                destination.Add(item);
            }
        }
    }

    private static VbaTokenStream MergeTokenStreams(
        VbaTokenStream previousTokens,
        VbaTokenStream parsedTokens,
        VbaSyntaxRange oldRange,
        VbaSyntaxRange newRange,
        int lineDelta,
        int offsetDelta)
    {
        var previous = previousTokens.Tokens;
        var parsed = parsedTokens.Tokens;
        var prefixEnd = FindFirstTokenEndingAfter(
            previous,
            oldRange.Start.Offset);
        var suffixStart = FindFirstTokenStartingAtOrAfter(
            previous,
            oldRange.End.Offset);
        var parsedStart = FindFirstTokenStartingAtOrAfter(
            parsed,
            newRange.Start.Offset);
        var parsedEnd = FindFirstTokenStartingAtOrAfter(
            parsed,
            newRange.End.Offset);
        var tokens = new List<VbaToken>(
            prefixEnd + (parsedEnd - parsedStart) + (previous.Count - suffixStart));
        for (var index = 0; index < prefixEnd; index++)
        {
            tokens.Add(previous[index]);
        }

        for (var index = parsedStart; index < parsed.Count; index++)
        {
            var token = parsed[index];
            if (token.Range.Start.Offset > newRange.End.Offset)
            {
                break;
            }

            if (IsContainedBy(token.Range, newRange))
            {
                tokens.Add(token);
            }
        }

        var requiresShift = lineDelta != 0 || offsetDelta != 0;
        for (var index = suffixStart; index < previous.Count; index++)
        {
            var token = previous[index];
            tokens.Add(requiresShift
                ? token with { Range = Shift(token.Range, lineDelta, offsetDelta) }
                : token);
        }

        return new VbaTokenStream(tokens);
    }

    private static int FindFirstTokenEndingAfter(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var low = 0;
        var high = tokens.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (tokens[middle].Range.End.Offset <= offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int FindFirstTokenStartingAtOrAfter(
        IReadOnlyList<VbaToken> tokens,
        int offset)
    {
        var low = 0;
        var high = tokens.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (tokens[middle].Range.Start.Offset < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static string MaskUnchangedMembers(
        string source,
        VbaSourceText sourceText,
        VbaModuleSyntax previousModule,
        int memberStartLine,
        int memberEndLine)
    {
        return string.Create(
            source.Length,
            (source, sourceText, previousModule, memberStartLine, memberEndLine),
            static (masked, state) =>
            {
                masked.Fill(' ');
                var searchOffset = 0;
                while (searchOffset < state.source.Length)
                {
                    var relativeNewLineOffset = state.source
                        .AsSpan(searchOffset)
                        .IndexOfAny('\r', '\n');
                    if (relativeNewLineOffset < 0)
                    {
                        break;
                    }

                    var newLineOffset = searchOffset + relativeNewLineOffset;
                    masked[newLineOffset] = state.source[newLineOffset];
                    searchOffset = newLineOffset + 1;
                }

                var preservedLines = state.previousModule.Attributes
                    .Select(attribute => attribute.Range.Start.Line)
                    .Concat(state.previousModule.Options.Select(option => option.Range.Start.Line))
                    .Append(state.previousModule.Identity.Range.Start.Line)
                    .Where(line => line >= 0 && line < state.sourceText.Lines.Count)
                    .Concat(Enumerable.Range(
                        state.memberStartLine,
                        state.memberEndLine - state.memberStartLine + 1))
                    .Distinct();
                foreach (var lineNumber in preservedLines)
                {
                    var line = state.sourceText.Lines[lineNumber];
                    state.source.AsSpan(
                            line.StartOffset,
                            line.EndOffset - line.StartOffset)
                        .CopyTo(masked[line.StartOffset..]);
                }
            });
    }

    private static bool TryFindChangedLineRange(
        VbaSourceText oldSource,
        VbaSourceText newSource,
        out int oldStartLine,
        out int oldEndLine,
        out int newStartLine,
        out int newEndLine)
    {
        var oldLines = oldSource.Lines;
        var newLines = newSource.Lines;
        var prefix = 0;
        while (prefix < oldLines.Count
            && prefix < newLines.Count
            && PhysicalLinesEqual(oldSource, prefix, newSource, prefix))
        {
            prefix++;
        }

        if (prefix == oldLines.Count && prefix == newLines.Count)
        {
            oldStartLine = oldEndLine = newStartLine = newEndLine = 0;
            return false;
        }

        var oldSuffix = oldLines.Count - 1;
        var newSuffix = newLines.Count - 1;
        while (oldSuffix >= prefix
            && newSuffix >= prefix
            && PhysicalLinesEqual(oldSource, oldSuffix, newSource, newSuffix))
        {
            oldSuffix--;
            newSuffix--;
        }

        oldStartLine = prefix;
        oldEndLine = Math.Max(prefix, oldSuffix);
        newStartLine = prefix;
        newEndLine = Math.Max(prefix, newSuffix);
        return true;
    }

    private static bool PhysicalLinesEqual(
        VbaSourceText leftSource,
        int leftLineIndex,
        VbaSourceText rightSource,
        int rightLineIndex)
    {
        var leftLine = leftSource.Lines[leftLineIndex];
        var rightLine = rightSource.Lines[rightLineIndex];
        if (ReferenceEquals(leftLine, rightLine))
        {
            return true;
        }

        var leftEndOffset = leftLineIndex + 1 < leftSource.Lines.Count
            ? leftSource.Lines[leftLineIndex + 1].StartOffset
            : leftSource.Text.Length;
        var rightEndOffset = rightLineIndex + 1 < rightSource.Lines.Count
            ? rightSource.Lines[rightLineIndex + 1].StartOffset
            : rightSource.Text.Length;
        return leftSource.Text.AsSpan(leftLine.StartOffset, leftEndOffset - leftLine.StartOffset)
            .SequenceEqual(rightSource.Text.AsSpan(
                rightLine.StartOffset,
                rightEndOffset - rightLine.StartOffset));
    }

    private static VbaModuleMemberSyntax? FindSingleContainingMember(
        IReadOnlyList<VbaModuleMemberSyntax> members,
        int startLine,
        int endLine)
    {
        var containingMembers = members
            .Where(member => member.BlockRange.Start.Line <= startLine && member.BlockRange.End.Line >= endLine)
            .ToArray();
        return containingMembers.Length == 1 ? containingMembers[0] : null;
    }

    private static bool TouchesMemberBoundary(VbaModuleMemberSyntax member, int startLine, int endLine)
        => startLine <= member.BlockRange.Start.Line || endLine >= member.BlockRange.End.Line;

    private static bool HasCrossMemberPreprocessorBlock(
        IReadOnlyList<VbaPreprocessorBlockSyntax> blocks,
        VbaSyntaxRange memberRange)
        => blocks.Any(block => RangesOverlap(block.Range, memberRange) && !IsContainedBy(block.Range, memberRange));

    private static bool RangesOverlap(VbaSyntaxRange left, VbaSyntaxRange right)
        => left.Start.Offset < right.End.Offset && right.Start.Offset < left.End.Offset;

    private static bool IsContainedBy(VbaSyntaxRange range, VbaSyntaxRange container)
        => range.Start.Offset >= container.Start.Offset && range.End.Offset <= container.End.Offset;

    private static VbaModuleMemberSyntax Shift(
        VbaModuleMemberSyntax member,
        int lineDelta,
        int offsetDelta)
        => member with { BlockRange = Shift(member.BlockRange, lineDelta, offsetDelta) };

    private static VbaDeclarationSyntax Shift(
        VbaDeclarationSyntax declaration,
        int lineDelta,
        int offsetDelta)
        => declaration with
        {
            Range = Shift(declaration.Range, lineDelta, offsetDelta),
            LineIndex = declaration.LineIndex + lineDelta,
            ParentProcedureRange = declaration.ParentProcedureRange is null
                ? null
                : Shift(declaration.ParentProcedureRange, lineDelta, offsetDelta)
        };

    private static VbaCallableDeclarationSyntax Shift(
        VbaCallableDeclarationSyntax declaration,
        int lineDelta,
        int offsetDelta)
        => declaration with
        {
            Range = Shift(declaration.Range, lineDelta, offsetDelta),
            BlockRange = Shift(declaration.BlockRange, lineDelta, offsetDelta),
            Parameters = declaration.Parameters
                .Select(parameter => parameter with { Range = Shift(parameter.Range, lineDelta, offsetDelta) })
                .ToArray(),
            LineIndex = declaration.LineIndex + lineDelta
        };

    private static VbaArgumentListSyntax Shift(
        VbaArgumentListSyntax argumentList,
        int lineDelta,
        int offsetDelta)
        => argumentList with
        {
            Range = Shift(argumentList.Range, lineDelta, offsetDelta),
            Arguments = argumentList.Arguments.Select(argument => argument with
            {
                Range = Shift(argument.Range, lineDelta, offsetDelta),
                NameRange = argument.NameRange is null ? null : Shift(argument.NameRange, lineDelta, offsetDelta),
                ValueRange = argument.ValueRange is null ? null : Shift(argument.ValueRange, lineDelta, offsetDelta)
            }).ToArray()
        };

    private static VbaBlockSyntax Shift(
        VbaBlockSyntax block,
        int lineDelta,
        int offsetDelta)
        => block with
        {
            OpenerRange = Shift(block.OpenerRange, lineDelta, offsetDelta),
            CloserRange = block.CloserRange is null
                ? null
                : Shift(block.CloserRange, lineDelta, offsetDelta),
            Branches = block.Branches.Select(branch => branch with
            {
                HeaderRange = Shift(branch.HeaderRange, lineDelta, offsetDelta),
                Range = Shift(branch.Range, lineDelta, offsetDelta)
            }).ToArray(),
            Range = Shift(block.Range, lineDelta, offsetDelta)
        };

    private static VbaLineLabelSyntax Shift(
        VbaLineLabelSyntax label,
        int lineDelta,
        int offsetDelta)
        => label with
        {
            Range = Shift(label.Range, lineDelta, offsetDelta),
            ProcedureRange = Shift(label.ProcedureRange, lineDelta, offsetDelta)
        };

    private static VbaPreprocessorDirectiveSyntax Shift(
        VbaPreprocessorDirectiveSyntax directive,
        int lineDelta,
        int offsetDelta)
        => directive with { Range = Shift(directive.Range, lineDelta, offsetDelta) };

    private static VbaPreprocessorBlockSyntax Shift(
        VbaPreprocessorBlockSyntax block,
        int lineDelta,
        int offsetDelta)
        => block with
        {
            IfDirective = Shift(block.IfDirective, lineDelta, offsetDelta),
            Branches = block.Branches.Select(branch => branch with
            {
                Directive = Shift(branch.Directive, lineDelta, offsetDelta),
                Range = Shift(branch.Range, lineDelta, offsetDelta),
                NestedBlocks = branch.NestedBlocks
                    .Select(nested => Shift(nested, lineDelta, offsetDelta))
                    .ToArray()
            }).ToArray(),
            EndDirective = block.EndDirective is null
                ? null
                : Shift(block.EndDirective, lineDelta, offsetDelta),
            Range = Shift(block.Range, lineDelta, offsetDelta)
        };

    private static VbaSyntaxRange Shift(VbaSyntaxRange range, int lineDelta, int offsetDelta)
        => new(
            Shift(range.Start, lineDelta, offsetDelta),
            Shift(range.End, lineDelta, offsetDelta));

    private static VbaSyntaxPosition Shift(VbaSyntaxPosition position, int lineDelta, int offsetDelta)
        => position with
        {
            Line = position.Line + lineDelta,
            Offset = position.Offset + offsetDelta
        };
}
