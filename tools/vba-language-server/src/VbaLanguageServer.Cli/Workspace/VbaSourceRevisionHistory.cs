using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Retains only source revisions that may invalidate an active or not-yet-started capture.
/// </summary>
internal sealed class VbaSourceRevisionHistory
{
    private readonly object gate = new();
    private readonly Dictionary<string, SourceRevision> revisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<long, int> activeWatermarks = [];

    public int Count
    {
        get
        {
            lock (gate)
            {
                return revisions.Count;
            }
        }
    }

    public IDisposable BeginCapture(long watermark)
    {
        lock (gate)
        {
            activeWatermarks.TryGetValue(watermark, out var count);
            activeWatermarks[watermark] = count + 1;
            PruneAcknowledgedRevisions();
        }

        return new CaptureLease(this, watermark);
    }

    public void Record(string uri, long revision)
    {
        var key = CreateIdentityKey(uri);
        lock (gate)
        {
            revisions[key] = new SourceRevision(uri, revision);
        }
    }

    public long GetRevision(string uri)
    {
        var key = CreateIdentityKey(uri);
        lock (gate)
        {
            return revisions.TryGetValue(key, out var revision)
                ? revision.Revision
                : 0;
        }
    }

    public IReadOnlyList<(string Uri, long Revision)> CaptureEntries()
    {
        lock (gate)
        {
            return revisions.Values
                .Select(revision => (revision.Uri, revision.Revision))
                .ToArray();
        }
    }

    private void Release(long watermark)
    {
        lock (gate)
        {
            if (!activeWatermarks.TryGetValue(watermark, out var count))
            {
                return;
            }

            if (count == 1)
            {
                activeWatermarks.Remove(watermark);
            }
            else
            {
                activeWatermarks[watermark] = count - 1;
            }

            if (activeWatermarks.Count == 0)
            {
                revisions.Clear();
                return;
            }

            PruneAcknowledgedRevisions();
        }
    }

    private void PruneAcknowledgedRevisions()
    {
        if (activeWatermarks.Count == 0)
        {
            return;
        }

        var oldestWatermark = activeWatermarks.First().Key;
        foreach (var key in revisions
            .Where(pair => pair.Value.Revision <= oldestWatermark)
            .Select(pair => pair.Key)
            .ToArray())
        {
            revisions.Remove(key);
        }
    }

    private static string CreateIdentityKey(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null)
        {
            return $"uri:{uri}";
        }

        try
        {
            return $"path:{Path.GetFullPath(localPath)}";
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return $"uri:{uri}";
        }
    }

    private sealed record SourceRevision(string Uri, long Revision);

    private sealed class CaptureLease : IDisposable
    {
        private VbaSourceRevisionHistory? owner;
        private readonly long watermark;

        public CaptureLease(
            VbaSourceRevisionHistory owner,
            long watermark)
        {
            this.owner = owner;
            this.watermark = watermark;
        }

        public void Dispose()
            => Interlocked.Exchange(ref owner, null)?.Release(watermark);
    }
}
