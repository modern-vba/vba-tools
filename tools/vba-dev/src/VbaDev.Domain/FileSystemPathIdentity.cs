namespace VbaDev.Domain;

/// <summary>
/// Resolves a path to the filesystem identity used for safety and containment decisions.
/// </summary>
public interface IFileSystemPathIdentityResolver
{
    /// <summary>
    /// Resolves one path without silently following an identity that cannot be proved.
    /// </summary>
    /// <param name="path">The path whose identity should be resolved.</param>
    /// <returns>The canonical and operation-safe path identity.</returns>
    FileSystemPathIdentity Resolve(string path);
}

/// <summary>
/// Describes the canonical identity and usable operation path for one filesystem path.
/// </summary>
/// <param name="CanonicalPath">The filesystem-canonical path used for comparisons.</param>
/// <param name="OperationPath">The normalized path used for filesystem operations.</param>
/// <param name="ObjectIdentity">The existing filesystem object identity, when available.</param>
public sealed record FileSystemPathIdentity(
    string CanonicalPath,
    string OperationPath,
    FileSystemObjectIdentity? ObjectIdentity);

/// <summary>
/// Identifies an existing filesystem object independently from its path spelling.
/// </summary>
/// <param name="VolumeSerialNumber">The volume serial number.</param>
/// <param name="FileIndex">The stable file index on the volume.</param>
public readonly record struct FileSystemObjectIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

/// <summary>
/// Compares filesystem-canonical identities for safety and containment decisions.
/// </summary>
public static class FileSystemPathIdentityRelations
{
    /// <summary>
    /// Returns whether two identities refer to the same canonical path or existing object.
    /// </summary>
    public static bool Same(FileSystemPathIdentity left, FileSystemPathIdentity right)
        => Path.TrimEndingDirectorySeparator(left.CanonicalPath).Equals(
                Path.TrimEndingDirectorySeparator(right.CanonicalPath),
                StringComparison.OrdinalIgnoreCase)
            || left.ObjectIdentity is not null
                && right.ObjectIdentity is not null
                && left.ObjectIdentity == right.ObjectIdentity;

    /// <summary>
    /// Returns whether the candidate is the directory itself or physically below it.
    /// </summary>
    public static bool SameOrDescendant(
        FileSystemPathIdentity candidate,
        FileSystemPathIdentity directory)
        => Same(candidate, directory)
            || IsSameOrDescendantCanonicalPath(
                candidate.CanonicalPath,
                directory.CanonicalPath);

    /// <summary>
    /// Returns whether two source roots cover any of the same physical subtree.
    /// </summary>
    public static bool RootsOverlap(
        FileSystemPathIdentity left,
        FileSystemPathIdentity right)
        => SameOrDescendant(left, right)
            || SameOrDescendant(right, left);

    private static bool IsSameOrDescendantCanonicalPath(
        string candidatePath,
        string directoryPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(candidatePath);
        var directory = Path.TrimEndingDirectorySeparator(directoryPath);
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
