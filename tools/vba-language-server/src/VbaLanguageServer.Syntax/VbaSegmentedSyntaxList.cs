using System.Collections;

namespace VbaLanguageServer.Syntax;

internal interface IVbaSegmentedSyntaxList
{
    int SegmentCount { get; }

    int LazyProjectedItemCount { get; }

    int EagerProjectedItemCount { get; }
}

internal sealed class VbaSegmentedSyntaxList<T> : IReadOnlyList<T>, IVbaSegmentedSyntaxList
{
    private readonly Segment[] segments;

    public VbaSegmentedSyntaxList(params Segment[] segments)
    {
        this.segments = segments.Where(segment => segment.Count > 0).ToArray();
        Count = this.segments.Sum(segment => segment.Count);
        LazyProjectedItemCount = this.segments
            .Where(segment => segment.Project is not null)
            .Sum(segment => segment.Count);
    }

    public int Count { get; }

    public int SegmentCount => segments.Length;

    public int LazyProjectedItemCount { get; }

    public int EagerProjectedItemCount => 0;

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var relative = index;
            foreach (var segment in segments)
            {
                if (relative < segment.Count)
                {
                    var item = segment.Source[segment.StartIndex + relative];
                    return segment.Project is null ? item : segment.Project(item);
                }

                relative -= segment.Count;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var segment in segments)
        {
            for (var index = 0; index < segment.Count; index++)
            {
                var item = segment.Source[segment.StartIndex + index];
                yield return segment.Project is null ? item : segment.Project(item);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    internal sealed record Segment(
        IReadOnlyList<T> Source,
        int StartIndex,
        int Count,
        Func<T, T>? Project = null);
}
