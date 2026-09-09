using System.Collections.Immutable;

namespace VbaTools.Semantics;

/// <summary>Preserves diagnostic explanations with or without related-location support.</summary>
public sealed class VbaDiagnosticPresentation
{
    private VbaDiagnosticPresentation(string message, ImmutableArray<VbaDiagnosticRelatedInformation> relatedInformation)
    {
        Message = message;
        RelatedInformation = relatedInformation;
    }

    public string Message { get; }
    public ImmutableArray<VbaDiagnosticRelatedInformation> RelatedInformation { get; }

    public static VbaDiagnosticPresentation Create(
        string message, IReadOnlyList<VbaDiagnosticDetail>? details, bool supportsRelatedInformation)
    {
        details ??= [];
        var fallback = (supportsRelatedInformation ? details.Where(detail => detail.Location is null) : details)
            .Select(detail => detail.FallbackText).Distinct(StringComparer.Ordinal).ToArray();
        var related = supportsRelatedInformation
            ? details.Where(detail => detail.Location is not null)
                .Select(detail => new VbaDiagnosticRelatedInformation(detail.Location!, detail.RelatedMessage))
                .ToImmutableArray()
            : [];
        return new(fallback.Length == 0 ? message : $"{message}\n{string.Join('\n', fallback)}", related);
    }
}

public sealed record VbaDiagnosticRelatedInformation(VbaDiagnosticLocation Location, string Message);
