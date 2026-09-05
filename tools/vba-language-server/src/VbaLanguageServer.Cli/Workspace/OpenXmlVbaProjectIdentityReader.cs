using System.Security.Cryptography;
using VbaTools.ProjectMetadata;

namespace VbaLanguageServer.Workspace;

internal enum VbaProjectIdentityReadFailureKind
{
    InvalidPackage,
    InvalidPackageTopology,
    InvalidVbaProjectPart,
    InvalidCompoundFile,
    InvalidCompressedDirectory,
    InvalidProjectInformation,
    UnsupportedCodePage,
    InvalidProjectName
}

internal sealed record VbaProjectIdentityRead(
    string VbaProjectName,
    int ProjectCodePage,
    VbaSourceTemplateContentIdentity SourceTemplateContentIdentity);

/// <summary>
/// Identifies the complete captured source-template package without exposing
/// the digest representation.
/// </summary>
internal sealed class VbaSourceTemplateContentIdentity
    : IEquatable<VbaSourceTemplateContentIdentity>
{
    private readonly string digest;

    private VbaSourceTemplateContentIdentity(string digest)
    {
        this.digest = digest;
    }

    public bool Equals(VbaSourceTemplateContentIdentity? other)
        => other is not null
            && digest.Equals(other.digest, StringComparison.Ordinal);

    public override bool Equals(object? obj)
        => obj is VbaSourceTemplateContentIdentity other
            && Equals(other);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(digest);

    internal static VbaSourceTemplateContentIdentity FromBytes(
        ReadOnlySpan<byte> bytes)
        => new(Convert.ToHexString(SHA256.HashData(bytes)));
}

internal sealed record VbaProjectIdentityReadFailure(
    VbaProjectIdentityReadFailureKind Kind,
    string Message);

internal sealed class VbaProjectIdentityReadResult
{
    private VbaProjectIdentityReadResult(
        VbaProjectIdentityRead? identity,
        VbaProjectIdentityReadFailure? failure)
    {
        Identity = identity;
        Failure = failure;
    }

    public VbaProjectIdentityRead? Identity { get; }

    public VbaProjectIdentityReadFailure? Failure { get; }

    public static VbaProjectIdentityReadResult Success(
        VbaProjectIdentityRead identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity, failure: null);
    }

    public static VbaProjectIdentityReadResult Failed(
        VbaProjectIdentityReadFailureKind kind,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(
            identity: null,
            new VbaProjectIdentityReadFailure(kind, message));
    }
}

internal interface IVbaProjectIdentityReader
{
    VbaProjectIdentityReadResult Read(
        byte[] sourceTemplateBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Projects neutral package metadata onto one request-scoped source-template identity.
/// </summary>
internal sealed class OpenXmlVbaProjectIdentityReader : IVbaProjectIdentityReader
{
    private readonly VbaProjectPackageMetadataReader metadataReader = new();

    public VbaProjectIdentityReadResult Read(
        byte[] sourceTemplateBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceTemplateBytes is null)
        {
            return VbaProjectIdentityReadResult.Failed(
                VbaProjectIdentityReadFailureKind.InvalidPackage,
                "The captured source-template package bytes are missing.");
        }

        if (sourceTemplateBytes.Length is <= 0
            || sourceTemplateBytes.Length > VbaProjectPackageMetadataReader.MaximumPackageLength)
        {
            return VbaProjectIdentityReadResult.Failed(
                VbaProjectIdentityReadFailureKind.InvalidPackage,
                "The captured source-template package has an invalid or excessive length.");
        }

        // Capture and the whole-package digest belong to this product boundary.
        // The neutral reader receives these same fixed bytes and identifies only
        // their VBA project part.
        var capturedPackageBytes = sourceTemplateBytes.ToArray();
        var result = metadataReader.Read(capturedPackageBytes, cancellationToken);
        if (result.Failure is { } failure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return VbaProjectIdentityReadResult.Failed(
                ProjectFailureKind(failure.Kind),
                $"The captured source-template package is unavailable: {failure.Message}");
        }

        var metadata = result.Metadata!;
        var identity = new VbaProjectIdentityRead(
            metadata.ProjectName,
            metadata.CodePage,
            VbaSourceTemplateContentIdentity.FromBytes(capturedPackageBytes));
        cancellationToken.ThrowIfCancellationRequested();
        return VbaProjectIdentityReadResult.Success(identity);
    }

    private static VbaProjectIdentityReadFailureKind ProjectFailureKind(
        VbaProjectPackageMetadataReadFailureKind kind)
        => kind switch
        {
            VbaProjectPackageMetadataReadFailureKind.InvalidPackage =>
                VbaProjectIdentityReadFailureKind.InvalidPackage,
            VbaProjectPackageMetadataReadFailureKind.InvalidPackageTopology =>
                VbaProjectIdentityReadFailureKind.InvalidPackageTopology,
            VbaProjectPackageMetadataReadFailureKind.InvalidVbaProjectPart =>
                VbaProjectIdentityReadFailureKind.InvalidVbaProjectPart,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompoundFile =>
                VbaProjectIdentityReadFailureKind.InvalidCompoundFile,
            VbaProjectPackageMetadataReadFailureKind.InvalidCompressedDirectory =>
                VbaProjectIdentityReadFailureKind.InvalidCompressedDirectory,
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectInformation =>
                VbaProjectIdentityReadFailureKind.InvalidProjectInformation,
            VbaProjectPackageMetadataReadFailureKind.UnsupportedCodePage =>
                VbaProjectIdentityReadFailureKind.UnsupportedCodePage,
            VbaProjectPackageMetadataReadFailureKind.InvalidProjectName =>
                VbaProjectIdentityReadFailureKind.InvalidProjectName,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
}
