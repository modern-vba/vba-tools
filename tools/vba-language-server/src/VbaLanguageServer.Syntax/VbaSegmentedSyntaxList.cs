using System.Collections;

namespace VbaLanguageServer.Syntax;

internal interface IVbaSegmentedSyntaxList
{
    int SegmentCount { get; }

    int LazyProjectedItemCount { get; }

    int NestingDepth { get; }

    int MaxProjectionStepCount { get; }

    int MaxLookupStepCount { get; }
}

internal sealed class VbaSegmentedSyntaxList<T> : IReadOnlyList<T>, IVbaSegmentedSyntaxList
{
    private readonly Segment[] segments;
    private readonly int[] segmentEnds;

    public VbaSegmentedSyntaxList(params Segment[] segments)
    {
        var flattenedSegments = new List<Segment>(segments.Length);
        foreach (var segment in segments)
        {
            AppendFlattenedSegment(
                flattenedSegments,
                segment.Source,
                segment.StartIndex,
                segment.Count,
                segment.Projection);
        }

        this.segments = flattenedSegments.ToArray();
        segmentEnds = new int[this.segments.Length];
        var itemCount = 0;
        for (var index = 0; index < this.segments.Length; index++)
        {
            itemCount = checked(itemCount + this.segments[index].Count);
            segmentEnds[index] = itemCount;
        }

        Count = itemCount;
        LazyProjectedItemCount = this.segments
            .Where(segment => segment.Projection is not null)
            .Sum(segment => segment.Count);
        MaxProjectionStepCount = this.segments
            .Select(segment => segment.Projection?.StepCount ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        NestingDepth = 1 + this.segments
            .Select(segment => segment.Source is IVbaSegmentedSyntaxList nested
                ? nested.NestingDepth
                : 0)
            .DefaultIfEmpty(0)
            .Max();
        MaxLookupStepCount = GetMaxLookupStepCount(this.segments.Length);
    }

    public int Count { get; }

    public int SegmentCount => segments.Length;

    public int LazyProjectedItemCount { get; }

    public int NestingDepth { get; }

    public int MaxProjectionStepCount { get; }

    public int MaxLookupStepCount { get; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            var segmentIndex = FindSegmentIndex(index);
            var segment = segments[segmentIndex];
            var segmentStart = segmentIndex == 0
                ? 0
                : segmentEnds[segmentIndex - 1];
            var item = segment.Source[
                segment.StartIndex + index - segmentStart];
            return segment.Projection is null
                ? item
                : segment.Projection.Apply(item);
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var segment in segments)
        {
            for (var index = 0; index < segment.Count; index++)
            {
                var item = segment.Source[segment.StartIndex + index];
                yield return segment.Projection is null
                    ? item
                    : segment.Projection.Apply(item);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    private int FindSegmentIndex(int index)
    {
        var low = 0;
        var high = segmentEnds.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (index < segmentEnds[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private static int GetMaxLookupStepCount(int segmentCount)
    {
        var steps = 0;
        while (segmentCount > 0)
        {
            steps++;
            segmentCount >>= 1;
        }

        return steps;
    }

    private static void AppendFlattenedSegment(
        List<Segment> destination,
        IReadOnlyList<T> source,
        int startIndex,
        int count,
        Projection? projection)
    {
        if (count <= 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        if (count > source.Count || startIndex > source.Count - count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (source is not VbaSegmentedSyntaxList<T> segmentedSource)
        {
            AppendLeafSegment(
                destination,
                Segment.FromProjection(
                    source,
                    startIndex,
                    count,
                    projection));
            return;
        }

        var sliceEnd = startIndex + count;
        var nestedStart = 0;
        foreach (var nestedSegment in segmentedSource.segments)
        {
            var nestedEnd = nestedStart + nestedSegment.Count;
            var overlapStart = Math.Max(startIndex, nestedStart);
            var overlapEnd = Math.Min(sliceEnd, nestedEnd);
            if (overlapStart < overlapEnd)
            {
                AppendFlattenedSegment(
                    destination,
                    nestedSegment.Source,
                    nestedSegment.StartIndex + overlapStart - nestedStart,
                    overlapEnd - overlapStart,
                    Projection.Compose(
                        nestedSegment.Projection,
                        projection));
            }

            if (nestedEnd >= sliceEnd)
            {
                break;
            }

            nestedStart = nestedEnd;
        }
    }

    private static void AppendLeafSegment(
        List<Segment> destination,
        Segment segment)
    {
        if (destination.Count > 0)
        {
            var previous = destination[^1];
            if (ReferenceEquals(previous.Source, segment.Source)
                && previous.StartIndex + previous.Count == segment.StartIndex
                && Projection.AreEquivalent(
                    previous.Projection,
                    segment.Projection))
            {
                destination[^1] = previous with
                {
                    Count = previous.Count + segment.Count
                };
                return;
            }
        }

        destination.Add(segment);
    }

    internal sealed record Segment
    {
        public Segment(
            IReadOnlyList<T> source,
            int startIndex,
            int count,
            Func<T, T>? project = null)
            : this(
                source,
                startIndex,
                count,
                Projection.FromArbitrary(project))
        {
        }

        private Segment(
            IReadOnlyList<T> source,
            int startIndex,
            int count,
            Projection? projection)
        {
            Source = source;
            StartIndex = startIndex;
            Count = count;
            Projection = projection;
        }

        public IReadOnlyList<T> Source { get; init; }

        public int StartIndex { get; init; }

        public int Count { get; init; }

        internal Projection? Projection { get; init; }

        internal static Segment FromProjection(
            IReadOnlyList<T> source,
            int startIndex,
            int count,
            Projection? projection)
            => new(
                source,
                startIndex,
                count,
                projection);

        public static Segment WithCoordinateShift(
            IReadOnlyList<T> source,
            int startIndex,
            int count,
            Func<T, int, int, T> shift,
            int lineDelta,
            int offsetDelta)
            => new(
                source,
                startIndex,
                count,
                Projection.FromCoordinateShift(
                    shift,
                    lineDelta,
                    offsetDelta));
    }

    internal sealed class Projection
    {
        private readonly Step[] steps;

        private Projection(params Step[] steps)
        {
            this.steps = steps;
        }

        public int StepCount => steps.Length;

        public T Apply(T item)
        {
            var projected = item;
            foreach (var step in steps)
            {
                projected = step.Apply(projected);
            }

            return projected;
        }

        public static Projection? FromArbitrary(Func<T, T>? project)
            => project is null
                ? null
                : new Projection(Step.FromArbitrary(project));

        public static Projection? FromCoordinateShift(
            Func<T, int, int, T> shift,
            int lineDelta,
            int offsetDelta)
        {
            ArgumentNullException.ThrowIfNull(shift);
            return lineDelta == 0 && offsetDelta == 0
                ? null
                : new Projection(
                    Step.FromCoordinateShift(
                        shift,
                        lineDelta,
                        offsetDelta));
        }

        public static Projection? Compose(
            Projection? inner,
            Projection? outer)
        {
            if (inner is null)
            {
                return outer;
            }

            if (outer is null)
            {
                return inner;
            }

            var combined = new List<Step>(
                inner.steps.Length + outer.steps.Length);
            combined.AddRange(inner.steps);
            foreach (var step in outer.steps)
            {
                AppendComposedStep(combined, step);
            }

            return combined.Count == 0
                ? null
                : new Projection(combined.ToArray());
        }

        public static bool AreEquivalent(
            Projection? left,
            Projection? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null
                || right is null
                || left.steps.Length != right.steps.Length)
            {
                return false;
            }

            for (var index = 0; index < left.steps.Length; index++)
            {
                if (!left.steps[index].IsEquivalentTo(right.steps[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AppendComposedStep(
            List<Step> destination,
            Step step)
        {
            if (destination.Count == 0
                || !destination[^1].TryComposeCoordinateShift(
                    step,
                    out var composed))
            {
                destination.Add(step);
                return;
            }

            destination.RemoveAt(destination.Count - 1);
            if (composed is not null)
            {
                destination.Add(composed);
            }
        }

        private sealed class Step
        {
            private readonly Func<T, T>? project;
            private readonly Func<T, int, int, T>? coordinateShift;

            private Step(
                Func<T, T>? project,
                Func<T, int, int, T>? coordinateShift,
                int lineDelta,
                int offsetDelta)
            {
                this.project = project;
                this.coordinateShift = coordinateShift;
                LineDelta = lineDelta;
                OffsetDelta = offsetDelta;
            }

            private int LineDelta { get; }

            private int OffsetDelta { get; }

            public T Apply(T item)
                => coordinateShift is null
                    ? project!(item)
                    : coordinateShift(
                        item,
                        LineDelta,
                        OffsetDelta);

            public bool TryComposeCoordinateShift(
                Step outer,
                out Step? composed)
            {
                composed = null;
                if (coordinateShift is null
                    || outer.coordinateShift is null
                    || !coordinateShift.Equals(outer.coordinateShift))
                {
                    return false;
                }

                var lineDelta = LineDelta + outer.LineDelta;
                var offsetDelta = OffsetDelta + outer.OffsetDelta;
                if (lineDelta != 0 || offsetDelta != 0)
                {
                    composed = FromCoordinateShift(
                        coordinateShift,
                        lineDelta,
                        offsetDelta);
                }

                return true;
            }

            public bool IsEquivalentTo(Step other)
                => LineDelta == other.LineDelta
                    && OffsetDelta == other.OffsetDelta
                    && Equals(project, other.project)
                    && Equals(coordinateShift, other.coordinateShift);

            public static Step FromArbitrary(Func<T, T> project)
                => new(
                    project,
                    coordinateShift: null,
                    lineDelta: 0,
                    offsetDelta: 0);

            public static Step FromCoordinateShift(
                Func<T, int, int, T> coordinateShift,
                int lineDelta,
                int offsetDelta)
                => new(
                    project: null,
                    coordinateShift,
                    lineDelta,
                    offsetDelta);
        }
    }
}
