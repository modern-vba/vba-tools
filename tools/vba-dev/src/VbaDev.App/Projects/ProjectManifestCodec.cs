using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>Validates manifest values at both byte admission and canonical encoding.</summary>
internal static class ProjectManifestCodec
{
    internal static ProjectManifest Decode(
        ReadOnlySpan<byte> bytes,
        string manifestPath,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        try
        {
            var manifest = ProjectManifestReader.Parse(
                ProjectManifestByteDecoding.Decode(bytes, manifestPath), manifestPath);
            ValidateIsolation(manifest, manifestPath, manifestPath, pathIdentityResolver);
            return manifest;
        }
        catch (VbaProjectManifestException error)
        {
            throw new ProjectManifestException(error.Message, error);
        }
    }

    internal static byte[] Encode(
        ProjectManifest manifest,
        string manifestPath,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        try
        {
            ProjectManifestValidator.Validate(manifest, ProjectManifest.ManifestFileName);
            ValidateIsolation(manifest, manifestPath, ProjectManifest.ManifestFileName, pathIdentityResolver);
            return ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest);
        }
        catch (VbaProjectManifestException error)
        {
            throw new ProjectManifestException(error.Message, error);
        }
    }

    private static void ValidateIsolation(
        ProjectManifest manifest,
        string manifestPath,
        string manifestName,
        IFileSystemPathIdentityResolver pathIdentityResolver)
        => _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
            manifest, manifestPath, manifestName, pathIdentityResolver);
}
