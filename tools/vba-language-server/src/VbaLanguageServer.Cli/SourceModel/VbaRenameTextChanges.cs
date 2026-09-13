using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using VbaTools.Syntax;

namespace VbaLanguageServer.SourceModel;

/// <summary>Retains each Rename proof's validated source edits and their immutable text results.</summary>
internal sealed class VbaRenameTextChanges : ReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>>
{
    private readonly IReadOnlyDictionary<string, VbaSourceTextEditResult> results;

    private VbaRenameTextChanges(
        IDictionary<string, IReadOnlyList<VbaTextEdit>> edits,
        IReadOnlyDictionary<string, VbaSourceTextEditResult> results)
        : base(edits)
    {
        this.results = results;
    }

    public new static VbaRenameTextChanges Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<VbaTextEdit>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, VbaSourceTextEditResult>(StringComparer.OrdinalIgnoreCase));

    public static bool TryCreate(
        IReadOnlyDictionary<string, IReadOnlyList<VbaTextEdit>> edits,
        Func<string, VbaSourceText?> findSource,
        [NotNullWhen(true)] out VbaRenameTextChanges? changes)
    {
        changes = null;
        var snapshots = new Dictionary<string, VbaSourceTextEditResult>(StringComparer.OrdinalIgnoreCase);
        var protocolEdits = new Dictionary<string, IReadOnlyList<VbaTextEdit>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, documentEdits) in edits)
        {
            var before = findSource(uri);
            if (before is null)
            {
                return false;
            }

            var sourceEdits = new List<VbaSourceTextEdit>(documentEdits.Count);
            foreach (var edit in documentEdits)
            {
                if (!before.TryGetOffset(edit.Range.Start.Line, edit.Range.Start.Character, out var start)
                    || !before.TryGetOffset(edit.Range.End.Line, edit.Range.End.Character, out var end))
                {
                    return false;
                }

                sourceEdits.Add(new VbaSourceTextEdit(start, end, edit.NewText));
            }

            if (!VbaSourceTextEditResult.TryApply(before, sourceEdits, out var result))
            {
                return false;
            }

            snapshots.Add(uri, result);
            protocolEdits.Add(uri, Array.AsReadOnly(documentEdits.ToArray()));
        }

        changes = new VbaRenameTextChanges(protocolEdits, snapshots);
        return true;
    }

    public string GetTextAfter(string uri, string unchangedText)
        => results.TryGetValue(uri, out var result) ? result.After.Text : unchangedText;

    public VbaRange MapRange(string uri, VbaRange range)
    {
        if (!results.TryGetValue(uri, out var result))
        {
            return range;
        }

        return new VbaRange(MapEndpoint(result, range.Start), MapEndpoint(result, range.End));
    }

    public int MapOffset(string uri, int offset)
        => results.TryGetValue(uri, out var result) ? MapOffset(result, offset) : offset;

    private static VbaPosition MapEndpoint(VbaSourceTextEditResult result, VbaPosition position)
    {
        if (!result.Before.TryGetOffset(position.Line, position.Character, out var beforeOffset)
            || !result.After.TryGetPosition(MapOffset(result, beforeOffset), out var afterPosition))
        {
            throw new VbaRenameCorrespondenceException();
        }

        return new VbaPosition(afterPosition.Line, afterPosition.Character);
    }

    private static int MapOffset(VbaSourceTextEditResult result, int offset)
    {
        // A renamed identifier, or a dependent prefix/suffix, retains its exact boundaries.
        // Rename has no meaning-preserving correspondence for a point inside a replacement.
        foreach (var replacement in result.Replacements)
        {
            if (offset == replacement.BeforeStartOffset)
            {
                return replacement.AfterStartOffset;
            }
            if (offset == replacement.BeforeEndOffset)
            {
                return replacement.AfterEndOffset;
            }
            if (offset > replacement.BeforeStartOffset && offset < replacement.BeforeEndOffset)
            {
                throw new VbaRenameCorrespondenceException();
            }
        }

        foreach (var span in result.UnchangedSpans)
        {
            if (offset >= span.BeforeStartOffset && offset <= span.BeforeEndOffset)
            {
                return span.AfterStartOffset + offset - span.BeforeStartOffset;
            }
        }

        if (offset == 0 && result.Before.IsEmpty && result.Replacements.Count == 0)
        {
            return 0;
        }

        throw new VbaRenameCorrespondenceException();
    }
}

/// <summary>Signals a failed Rename proof, never an arbitrary mapping of replaced text.</summary>
internal sealed class VbaRenameCorrespondenceException : Exception;
