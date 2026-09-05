using System.Collections.Immutable;
using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Build;

/// <summary>
/// Owns the exact source bytes that may be materialized for one admitted generation.
/// </summary>
internal sealed class AdmittedDebugBuildSourceSet
{
    private readonly ImmutableArray<AdmittedBuildSource> sources;

    internal AdmittedDebugBuildSourceSet(
        DebugGenerationId generationId,
        IEnumerable<ValidatedTransportedDebugSource> sources)
    {
        ArgumentNullException.ThrowIfNull(generationId);
        ArgumentNullException.ThrowIfNull(sources);
        GenerationId = generationId;
        this.sources = sources
            .Select(source => new AdmittedBuildSource(
                source.RelativePath,
                ImmutableArray.CreateRange(source.Bytes)))
            .ToImmutableArray();
    }

    internal DebugGenerationId GenerationId { get; }

    internal int Count => sources.Length;

    internal void MaterializeInto(IVbaDebugGenerationWorkspace generationWorkspace)
    {
        ArgumentNullException.ThrowIfNull(generationWorkspace);
        if (generationWorkspace.GenerationId != GenerationId)
        {
            throw new InvalidOperationException(
                "The admitted build source set does not belong to the requested debug generation.");
        }

        foreach (var source in sources)
        {
            using var stream = generationWorkspace.CreateSourceFile(source.RelativePath);
            stream.Write(source.Bytes.AsSpan());
            stream.Flush(flushToDisk: true);
        }
    }

    private sealed record AdmittedBuildSource(
        string RelativePath,
        ImmutableArray<byte> Bytes);
}
