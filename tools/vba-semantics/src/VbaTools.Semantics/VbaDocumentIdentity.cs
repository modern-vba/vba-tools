namespace VbaTools.Semantics;

/// <summary>
/// Opaque equality identity for one source document.
/// </summary>
internal readonly struct VbaDocumentIdentity
    : IEquatable<VbaDocumentIdentity>,
      IComparable<VbaDocumentIdentity>
{
    private readonly VbaDocumentIdentityKind kind;
    private readonly string? canonicalValue;

    internal VbaDocumentIdentity(
        VbaDocumentIdentityKind kind,
        string canonicalValue)
    {
        this.kind = kind;
        this.canonicalValue = canonicalValue;
    }

    internal bool IsLocalFile
        => kind == VbaDocumentIdentityKind.LocalFile
            && canonicalValue is not null;

    internal string CanonicalValue
        => canonicalValue
            ?? throw new InvalidOperationException(
                "An uninitialized document identity has no canonical value.");

    internal string StableKey
        => canonicalValue is null
            ? throw new InvalidOperationException(
                "An uninitialized document identity has no stable key.")
            : string.Join("\u001e", kind, canonicalValue);

    public bool Equals(VbaDocumentIdentity other)
        => kind == other.kind
            && StringComparer.OrdinalIgnoreCase.Equals(
                canonicalValue,
                other.canonicalValue);

    public override bool Equals(object? obj)
        => obj is VbaDocumentIdentity other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(
            kind,
            canonicalValue is null
                ? 0
                : StringComparer.OrdinalIgnoreCase.GetHashCode(
                    canonicalValue));

    public int CompareTo(VbaDocumentIdentity other)
    {
        var kindComparison = kind.CompareTo(other.kind);
        return kindComparison != 0
            ? kindComparison
            : StringComparer.OrdinalIgnoreCase.Compare(
                canonicalValue,
                other.canonicalValue);
    }

    public static bool operator ==(
        VbaDocumentIdentity left,
        VbaDocumentIdentity right)
        => left.Equals(right);

    public static bool operator !=(
        VbaDocumentIdentity left,
        VbaDocumentIdentity right)
        => !left.Equals(right);

    public override string ToString() => canonicalValue ?? "";
}

internal enum VbaDocumentIdentityKind
{
    LocalFile,
    UnresolvedFileUri,
    NormalizedUri
}
