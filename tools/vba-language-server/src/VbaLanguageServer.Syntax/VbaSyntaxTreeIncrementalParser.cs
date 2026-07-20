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
        out VbaSyntaxTreeChangeSet result,
        out VbaIncrementalParseObservation observation)
    {
        result = default!;
        observation = VbaIncrementalParseObservation.FullModule(
            source.Length,
            VbaIncrementalParseFallbackReason.DifferentialGuardFailed);
        var previousSource = previousSyntaxTree.SourceText;
        var currentSource = VbaSourceText.Update(source, previousSource);
        if (previousSyntaxTree.Diagnostics.Count > 0)
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.PreviousDiagnostics
            };
            return false;
        }

        if (previousSyntaxTree.Module.Kind == VbaModuleKind.FormModule)
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.UnsupportedModuleKind
            };
            return false;
        }

        if (!previousSyntaxTree.Uri.Equals(uri, StringComparison.Ordinal))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.UriMismatch
            };
            return false;
        }

        if (!TryFindChangedLineRange(
                previousSource,
                currentSource,
                out var oldStartLine,
                out var oldEndLine,
                out var newStartLine,
                out var newEndLine))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.ChangeLocalityUnproven
            };
            return false;
        }

        var previousMember = FindSingleContainingMember(
            previousSyntaxTree.Module.Members,
            oldStartLine,
            oldEndLine);
        if (previousMember is null)
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.MemberNotUnique
            };
            return false;
        }

        if (TouchesMemberBoundary(previousSource, previousMember, oldStartLine, oldEndLine))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.MemberBoundaryTouched
            };
            return false;
        }

        var lineDelta = currentSource.Lines.Count - previousSource.Lines.Count;
        var currentMemberStartLine = previousMember.BlockRange.Start.Line;
        var currentMemberEndLine = previousMember.BlockRange.End.Line + lineDelta;
        if (currentMemberEndLine >= currentSource.Lines.Count
            || newStartLine <= currentMemberStartLine
            || newEndLine >= currentMemberEndLine)
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.WindowOutOfRange
            };
            return false;
        }

        if (HasCrossMemberPreprocessorBlock(previousSyntaxTree.Module.PreprocessorBlocks, previousMember.BlockRange))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.CrossMemberPreprocessor
            };
            return false;
        }

        if (!TryCreateSourceWindow(
            source,
            currentSource,
            previousSyntaxTree.Module,
            currentMemberStartLine,
            currentMemberEndLine,
            out var window))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.WindowOutOfRange
            };
            return false;
        }

        var parsedLocalTree = VbaSyntaxTreeParser.ParseModule(uri, window.Text);
        if (parsedLocalTree.Diagnostics.Count > 0)
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.SliceParseDiagnostics
            };
            return false;
        }

        var projectedMemberTree = ProjectSyntaxTree(
            uri,
            parsedLocalTree,
            currentSource,
            window.ModuleContext,
            window.Origin);
        var currentMember = projectedMemberTree.Module.Members.SingleOrDefault(member =>
            member.BlockRange.Start.Line == currentMemberStartLine
            && member.BlockRange.End.Line == currentMemberEndLine);
        if (currentMember is null
            || currentMember.Kind != previousMember.Kind
            || !currentMember.Name.Equals(previousMember.Name, StringComparison.OrdinalIgnoreCase))
        {
            observation = observation with
            {
                FallbackReason = VbaIncrementalParseFallbackReason.MemberShapeChanged
            };
            return false;
        }

        var syntaxTree = MergeSyntaxTree(
            uri,
            previousSyntaxTree,
            projectedMemberTree,
            previousMember,
            currentMember,
            currentSource);
        result = new VbaSyntaxTreeChangeSet.ModuleMember(
            syntaxTree,
            previousMember,
            currentMember);
        observation = new VbaIncrementalParseObservation(
            VbaIncrementalParseRoute.ModuleMemberSourceWindow,
            VbaIncrementalParseFallbackReason.None,
            source.Length,
            window.Text.Length,
            window.Origin.Utf16Offset,
            window.Origin.Line,
            window.MemberUtf16Length,
            window.ModuleContext.Kind,
            window.ModuleContext.Identity.Name);
        return true;
    }

    private static bool TryCreateSourceWindow(
        string source,
        VbaSourceText currentSource,
        VbaModuleSyntax previousModule,
        int memberStartLine,
        int memberEndLine,
        out VbaModuleMemberSourceWindow window)
    {
        window = default!;
        var documentationStartLine = VbaSyntaxTreeParser.FindDocumentationCommentStartLine(
            currentSource.Lines,
            memberStartLine);
        var windowStartOffset = currentSource.Lines[documentationStartLine].StartOffset;
        var memberStartOffset = currentSource.Lines[memberStartLine].StartOffset;
        var memberEndOffset = currentSource.Lines[memberEndLine].EndOffset;
        if (windowStartOffset < 0
            || memberStartOffset < windowStartOffset
            || memberEndOffset > source.Length)
        {
            return false;
        }

        window = new VbaModuleMemberSourceWindow(
            source[windowStartOffset..memberEndOffset],
            new VbaSourceOrigin(documentationStartLine, windowStartOffset),
            new VbaModuleParseContext(
                previousModule.Kind,
                previousModule.Identity,
                previousModule.Attributes,
                previousModule.Options,
                previousModule.CodeStartLine),
            memberStartLine,
            memberEndLine,
            memberStartOffset,
            memberEndOffset);
        return true;
    }

    private static VbaSyntaxTree ProjectSyntaxTree(
        string uri,
        VbaSyntaxTree parsedLocalTree,
        VbaSourceText currentSource,
        VbaModuleParseContext moduleContext,
        VbaSourceOrigin origin)
    {
        var module = new VbaModuleSyntax(
            moduleContext.Kind,
            moduleContext.Identity,
            moduleContext.Attributes,
            moduleContext.Options,
            parsedLocalTree.Module.Members
                .Select(member => Shift(member, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.Declarations
                .Select(declaration => Shift(declaration, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.CallableDeclarations
                .Select(declaration => Shift(declaration, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.Statements
                .Select(statement => statement with
                {
                    Range = Shift(statement.Range, origin.Line, origin.Utf16Offset)
                })
                .ToArray(),
            parsedLocalTree.Module.Expressions
                .Select(expression => expression with
                {
                    Range = Shift(expression.Range, origin.Line, origin.Utf16Offset)
                })
                .ToArray(),
            parsedLocalTree.Module.ArgumentLists
                .Select(argumentList => Shift(argumentList, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.Blocks
                .Select(block => Shift(block, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.LineLabels
                .Select(label => Shift(label, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.PreprocessorDirectives
                .Select(directive => Shift(directive, origin.Line, origin.Utf16Offset))
                .ToArray(),
            parsedLocalTree.Module.PreprocessorBlocks
                .Select(block => Shift(block, origin.Line, origin.Utf16Offset))
                .ToArray(),
            null,
            moduleContext.CodeStartLine,
            currentSource.FullRange);
        var tokens = parsedLocalTree.TokenStream.Tokens
            .Select(token => token with
            {
                Range = Shift(token.Range, origin.Line, origin.Utf16Offset)
            })
            .ToArray();
        var diagnostics = parsedLocalTree.Diagnostics
            .Select(diagnostic => diagnostic with
            {
                Range = Shift(diagnostic.Range, origin.Line, origin.Utf16Offset)
            })
            .ToArray();
        return new VbaSyntaxTree(
            uri,
            currentSource,
            new VbaTokenStream(tokens),
            module,
            diagnostics);
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
                static (member, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(member, shiftLineDelta, shiftOffsetDelta),
                oldRange,
                newRange),
            Declarations = MergeByRange(
                previousTree.Module.Declarations,
                parsedMemberTree.Module.Declarations,
                declaration => declaration.Range,
                static (declaration, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(declaration, shiftLineDelta, shiftOffsetDelta),
                oldRange,
                newRange),
            CallableDeclarations = MergeByRange(
                previousTree.Module.CallableDeclarations,
                parsedMemberTree.Module.CallableDeclarations,
                declaration => declaration.BlockRange,
                static (declaration, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(declaration, shiftLineDelta, shiftOffsetDelta),
                oldRange,
                newRange),
            Statements = MergeByRange(
                previousTree.Module.Statements,
                parsedMemberTree.Module.Statements,
                statement => statement.Range,
                static (statement, shiftLineDelta, shiftOffsetDelta) =>
                    statement with
                    {
                        Range = Shift(
                            statement.Range,
                            shiftLineDelta,
                            shiftOffsetDelta)
                    },
                oldRange,
                newRange),
            Expressions = MergeByRange(
                previousTree.Module.Expressions,
                parsedMemberTree.Module.Expressions,
                expression => expression.Range,
                static (expression, shiftLineDelta, shiftOffsetDelta) =>
                    expression with
                    {
                        Range = Shift(
                            expression.Range,
                            shiftLineDelta,
                            shiftOffsetDelta)
                    },
                oldRange,
                newRange),
            ArgumentLists = MergeByRange(
                previousTree.Module.ArgumentLists,
                parsedMemberTree.Module.ArgumentLists,
                argumentList => argumentList.Range,
                static (argumentList, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(
                        argumentList,
                        shiftLineDelta,
                        shiftOffsetDelta),
                oldRange,
                newRange),
            Blocks = MergeByRange(
                previousTree.Module.Blocks,
                parsedMemberTree.Module.Blocks,
                block => block.Range,
                static (block, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(block, shiftLineDelta, shiftOffsetDelta),
                oldRange,
                newRange),
            LineLabels = MergeByRange(
                previousTree.Module.LineLabels,
                parsedMemberTree.Module.LineLabels,
                label => label.Range,
                static (label, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(label, shiftLineDelta, shiftOffsetDelta),
                oldRange,
                newRange),
            PreprocessorDirectives = MergeByRange(
                previousTree.Module.PreprocessorDirectives,
                parsedMemberTree.Module.PreprocessorDirectives,
                directive => directive.Range,
                static (directive, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(
                        directive,
                        shiftLineDelta,
                        shiftOffsetDelta),
                oldRange,
                newRange),
            PreprocessorBlocks = MergeByRange(
                previousTree.Module.PreprocessorBlocks,
                parsedMemberTree.Module.PreprocessorBlocks,
                block => block.Range,
                static (block, shiftLineDelta, shiftOffsetDelta) =>
                    Shift(block, shiftLineDelta, shiftOffsetDelta),
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
        Func<T, int, int, T> shift,
        VbaSyntaxRange oldRange,
        VbaSyntaxRange newRange)
    {
        var lineDelta = newRange.End.Line - oldRange.End.Line;
        var offsetDelta = newRange.End.Offset - oldRange.End.Offset;
        var prefixCount = 0;
        while (prefixCount < previousItems.Count
            && getRange(previousItems[prefixCount]).End.Offset <= oldRange.Start.Offset)
        {
            prefixCount++;
        }

        var suffixStart = prefixCount;
        while (suffixStart < previousItems.Count
            && getRange(previousItems[suffixStart]).Start.Offset < oldRange.End.Offset)
        {
            suffixStart++;
        }

        var parsedStart = 0;
        while (parsedStart < parsedItems.Count
            && !IsContainedBy(getRange(parsedItems[parsedStart]), newRange))
        {
            parsedStart++;
        }

        var parsedEnd = parsedStart;
        while (parsedEnd < parsedItems.Count
            && IsContainedBy(getRange(parsedItems[parsedEnd]), newRange))
        {
            parsedEnd++;
        }

        return new VbaSegmentedSyntaxList<T>(
            new VbaSegmentedSyntaxList<T>.Segment(previousItems, 0, prefixCount),
            new VbaSegmentedSyntaxList<T>.Segment(parsedItems, parsedStart, parsedEnd - parsedStart),
            VbaSegmentedSyntaxList<T>.Segment.WithCoordinateShift(
                previousItems,
                suffixStart,
                previousItems.Count - suffixStart,
                shift,
                lineDelta,
                offsetDelta));
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
        return new VbaTokenStream(new VbaSegmentedSyntaxList<VbaToken>(
            new VbaSegmentedSyntaxList<VbaToken>.Segment(previous, 0, prefixEnd),
            new VbaSegmentedSyntaxList<VbaToken>.Segment(parsed, parsedStart, parsedEnd - parsedStart),
            VbaSegmentedSyntaxList<VbaToken>.Segment.WithCoordinateShift(
                previous,
                suffixStart,
                previous.Count - suffixStart,
                static (token, shiftLineDelta, shiftOffsetDelta) =>
                    token with
                    {
                        Range = Shift(
                            token.Range,
                            shiftLineDelta,
                            shiftOffsetDelta)
                    },
                lineDelta,
                offsetDelta)));
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

    private static bool TouchesMemberBoundary(
        VbaSourceText sourceText,
        VbaModuleMemberSyntax member,
        int startLine,
        int endLine)
    {
        var headerEndLine = member.BlockRange.Start.Line;
        while (headerEndLine < member.BlockRange.End.Line)
        {
            var code = VbaSourceText.StripApostropheComment(sourceText.Lines[headerEndLine].Text);
            if (!VbaSourceText.HasLineContinuation(code))
            {
                break;
            }

            headerEndLine++;
        }

        return startLine <= headerEndLine || endLine >= member.BlockRange.End.Line;
    }

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
            Range = Shift(block.Range, lineDelta, offsetDelta),
            MalformedBarrierOwnerRange = block.MalformedBarrierOwnerRange is null
                ? null
                : Shift(block.MalformedBarrierOwnerRange, lineDelta, offsetDelta)
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
