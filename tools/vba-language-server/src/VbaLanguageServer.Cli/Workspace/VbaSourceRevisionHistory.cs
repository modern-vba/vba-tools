using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Retains only source revisions that may invalidate an active or not-yet-started capture.
/// </summary>
internal sealed class VbaSourceRevisionHistory
{
    private readonly object gate = new();
    private readonly bool retainOnlyWhileCapturesActive;
    private readonly Dictionary<VbaDocumentIdentity, SourceRevision>
        revisions = [];
    private readonly SortedDictionary<long, int> activeWatermarks = [];

    public VbaSourceRevisionHistory(
        bool retainOnlyWhileCapturesActive = false)
    {
        this.retainOnlyWhileCapturesActive =
            retainOnlyWhileCapturesActive;
    }

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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var identity))
        {
            return;
        }

        lock (gate)
        {
            if (retainOnlyWhileCapturesActive
                && activeWatermarks.Count == 0)
            {
                return;
            }

            revisions[identity] = new SourceRevision(uri, revision);
        }
    }

    public long GetRevision(string uri)
    {
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var identity))
        {
            return 0;
        }

        lock (gate)
        {
            return revisions.TryGetValue(identity, out var revision)
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
