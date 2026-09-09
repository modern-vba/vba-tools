namespace VbaTools.Semantics;

public sealed record VbaSignaturePresentationIdentity(
    string Label,
    IReadOnlyList<string> ParameterLabels)
{
    public bool Matches(VbaSignaturePresentationIdentity other)
        => Label.Equals(other.Label, StringComparison.Ordinal)
            && ParameterLabels.SequenceEqual(
                other.ParameterLabels,
                StringComparer.Ordinal);
}

/// <summary>
/// Represents one physical callable signature retained by signature help.
/// </summary>
/// <param name="Signature">The physical callable signature.</param>
/// <param name="ActiveParameter">The zero-based active parameter index, or null when no parameter maps.</param>
/// <param name="IsConditionalVariant">Whether the signature is one variant of a conditional family.</param>
public sealed record VbaSignatureHelpVariant(
    VbaCallableSignature Signature,
    int? ActiveParameter,
    bool IsConditionalVariant = false)
{
    public string DisplayLabel => IsConditionalVariant
        ? $"{Signature.Label} [#If]"
        : Signature.Label;

    public VbaSignaturePresentationIdentity PresentationIdentity => new(
        DisplayLabel,
        Signature.Parameters.Select(parameter => parameter.Label).ToArray());
}

/// <summary>
/// Represents the signature help result for a call site.
/// </summary>
/// <param name="Signature">The active callable signature retained for compatibility with editor-neutral consumers.</param>
/// <param name="ActiveParameter">The active signature's zero-based parameter index, or null when no parameter maps.</param>
/// <param name="PhysicalSignatures">Every physical signature retained for presentation.</param>
/// <param name="ActiveSignature">The zero-based active signature index.</param>
public sealed record VbaSignatureHelp(
    VbaCallableSignature Signature,
    int? ActiveParameter,
    IReadOnlyList<VbaSignatureHelpVariant>? PhysicalSignatures = null,
    int ActiveSignature = 0)
{
    /// <summary>
    /// Gets every physical signature, including the ordinary single-signature fallback.
    /// </summary>
    public IReadOnlyList<VbaSignatureHelpVariant> Signatures { get; } =
        PhysicalSignatures
        ?? [new VbaSignatureHelpVariant(Signature, ActiveParameter)];
}
