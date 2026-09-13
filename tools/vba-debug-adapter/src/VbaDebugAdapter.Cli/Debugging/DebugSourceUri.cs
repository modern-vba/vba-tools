using VbaTools.SourceIdentities;

namespace VbaDebugAdapter.Debugging;

/// <summary>
/// Keeps URI spelling and its lexical interpretation together. Rejection remains
/// the source-admission authority's decision, in its existing validation order.
/// </summary>
internal sealed record DebugSourceUri
{
    private DebugSourceUri(string? originalUri, SourceIdentity? identity)
    {
        OriginalUri = originalUri;
        Identity = identity;
    }

    internal string? OriginalUri { get; }

    internal SourceIdentity? Identity { get; }

    internal static DebugSourceUri Create(string? originalUri)
        => new(originalUri, SourceIdentity.TryFromUri(originalUri, out var identity) ? identity : null);
}
