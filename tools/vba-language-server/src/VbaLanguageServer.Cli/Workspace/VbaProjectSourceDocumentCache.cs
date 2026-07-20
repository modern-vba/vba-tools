using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Caches parsed and projected documents over syntax-free project disk facts.
/// </summary>
internal sealed class VbaProjectSourceDocumentCache
{
    private readonly object gate = new();
    private readonly Dictionary<string, SourceState> states =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (gate)
            {
                return states.Values.Count(
                    state => state.Document is not null);
            }
        }
    }

    /// <summary>
    /// Gets the parsed projection for a disk fact or creates it when its
    /// decoded content identity has changed.
    /// </summary>
    public VbaTrackedDocument GetOrCreateDocument(
        VbaProjectDiskSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceState state;
        long capturedGeneration;
        lock (gate)
        {
            if (!states.TryGetValue(source.FullPath, out state!))
            {
                state = new SourceState();
                states.Add(source.FullPath, state);
            }

            if (state.Document is { } cached
                && cached.ContentIdentity.Equals(
                    source.ContentIdentity))
            {
                return cached.Document;
            }

            state.Generation++;
            capturedGeneration = state.Generation;
            state.ActiveBuilds++;
        }

        try
        {
            var syntaxTree = VbaSyntaxTree.ParseModule(
                source.Uri,
                source.Text);
            var document = new VbaTrackedDocument(
                source.Uri,
                source.Text,
                syntaxTree,
                SourceDocument: VbaSourceDocumentProjector.Project(
                    source.Uri,
                    syntaxTree));
            lock (gate)
            {
                if (state.Document is { } concurrentlyCached
                    && concurrentlyCached.ContentIdentity.Equals(
                        source.ContentIdentity))
                {
                    return concurrentlyCached.Document;
                }

                if (state.Generation == capturedGeneration)
                {
                    state.Document = new CachedDocument(
                        source.ContentIdentity,
                        document);
                }
            }

            return document;
        }
        finally
        {
            lock (gate)
            {
                state.ActiveBuilds--;
                if (state.ActiveBuilds == 0
                    && state.Document is null)
                {
                    states.Remove(source.FullPath);
                }
            }
        }
    }

    /// <summary>
    /// Releases the parsed projection retained for one disk source.
    /// </summary>
    public void Invalidate(string localPath)
    {
        var fullPath = Path.GetFullPath(localPath);
        lock (gate)
        {
            if (!states.TryGetValue(fullPath, out var state))
            {
                return;
            }

            state.Generation++;
            state.Document = null;
            if (state.ActiveBuilds == 0)
            {
                states.Remove(fullPath);
            }
        }
    }

    private sealed class SourceState
    {
        public long Generation { get; set; }

        public int ActiveBuilds { get; set; }

        public CachedDocument? Document { get; set; }
    }

    private sealed record CachedDocument(
        VbaProjectDiskContentIdentity ContentIdentity,
        VbaTrackedDocument Document);
}
