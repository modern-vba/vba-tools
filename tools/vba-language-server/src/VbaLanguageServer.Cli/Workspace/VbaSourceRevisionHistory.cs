using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

internal sealed record VbaSourceRevision(
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long Revision);

/// <summary>
/// Retains only source revisions that may invalidate an active or not-yet-started capture.
/// </summary>
internal sealed class VbaSourceRevisionHistory
{
    private readonly object gate = new();
    private readonly bool retainOnlyWhileCapturesActive;
    private readonly Dictionary<VbaDocumentIdentity, VbaSourceRevision>
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

    public void Record(
        VbaIdentifiedDocument document,
        long revision)
    {
        lock (gate)
        {
            if (retainOnlyWhileCapturesActive
                && activeWatermarks.Count == 0)
            {
                return;
            }

            revisions[document.Identity] =
                new VbaSourceRevision(
                    document.Identity,
                    document.Uri,
                    revision);
        }
    }

    public long GetRevision(VbaDocumentIdentity documentIdentity)
    {
        lock (gate)
        {
            return revisions.TryGetValue(documentIdentity, out var revision)
                ? revision.Revision
                : 0;
        }
    }

    public IReadOnlyList<VbaSourceRevision> CaptureEntries()
    {
        lock (gate)
        {
            return revisions.Values.ToArray();
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
