using System.Collections.Frozen;
using System.Security.Cryptography;

namespace VbaTools.ProjectMetadata;

/// <summary>The platform declared by the VBA project information prefix.</summary>
public enum VbaProjectSystemKind
{
    Win16 = 0,
    Win32 = 1,
    Mac = 2,
    Win64 = 3
}

/// <summary>Identifies the exact VBA project part, not its containing package.</summary>
public sealed record VbaProjectPartContentIdentity
{
    private VbaProjectPartContentIdentity(string sha256) => Sha256 = sha256;

    public string Sha256 { get; }

    internal static VbaProjectPartContentIdentity FromBytes(ReadOnlySpan<byte> bytes)
        => new(Convert.ToHexString(SHA256.HashData(bytes)));
}

/// <summary>Immutable facts admitted from one VBA project package.</summary>
public sealed class VbaProjectPackageMetadata
{
    internal VbaProjectPackageMetadata(
        string projectName,
        int codePage,
        VbaProjectSystemKind systemKind,
        IReadOnlyDictionary<string, short> projectConstants,
        VbaProjectPartContentIdentity vbaProjectPartContentIdentity)
    {
        ProjectName = projectName;
        CodePage = codePage;
        SystemKind = systemKind;
        ProjectConstants = projectConstants.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        VbaProjectPartContentIdentity = vbaProjectPartContentIdentity;
    }

    public string ProjectName { get; }

    public int CodePage { get; }

    public VbaProjectSystemKind SystemKind { get; }

    public IReadOnlyDictionary<string, short> ProjectConstants { get; }

    public VbaProjectPartContentIdentity VbaProjectPartContentIdentity { get; }
}

/// <summary>The format boundary at which package admission failed.</summary>
public enum VbaProjectPackageMetadataReadFailureKind
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

public sealed record VbaProjectPackageMetadataReadFailure(
    VbaProjectPackageMetadataReadFailureKind Kind,
    string Message);

/// <summary>Exactly one immutable metadata value or neutral format failure.</summary>
public sealed class VbaProjectPackageMetadataReadResult
{
    private VbaProjectPackageMetadataReadResult(
        VbaProjectPackageMetadata? metadata,
        VbaProjectPackageMetadataReadFailure? failure)
    {
        Metadata = metadata;
        Failure = failure;
    }

    public VbaProjectPackageMetadata? Metadata { get; }

    public VbaProjectPackageMetadataReadFailure? Failure { get; }

    internal static VbaProjectPackageMetadataReadResult Success(VbaProjectPackageMetadata metadata)
        => new(metadata, null);

    internal static VbaProjectPackageMetadataReadResult Failed(
        VbaProjectPackageMetadataReadFailureKind kind,
        string message)
        => new(null, new VbaProjectPackageMetadataReadFailure(kind, message));
}
