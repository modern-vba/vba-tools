using System.Security.Cryptography;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.Tests;

internal static class InitialWorkbookTestArtifactEvidence
{
    public static InitialWorkbookArtifactEvidence Capture(string workbookPath)
    {
        var fullPath = Path.GetFullPath(workbookPath);
        var identity = new FileSystemPathIdentityResolver()
            .Resolve(fullPath)
            .ObjectIdentity
            ?? throw new PlatformNotSupportedException(
                "Initial workbook test evidence requires stable file identity.");
        var bytes = File.ReadAllBytes(fullPath);
        return new InitialWorkbookArtifactEvidence(
            fullPath,
            identity,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
