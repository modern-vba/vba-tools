using VbaDev.Infrastructure.FileSystem;
using System.Diagnostics;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class DocumentSourceSetIsolationValidatorTests
{
    [Fact]
    public void EqualRootsReportBothDocumentNamesAndOriginalSourcePaths()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifest = CreateTwoDocumentManifest(
            "src/Shared",
            "src/Shared");

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                manifestPath,
                ProjectManifest.ManifestFileName,
                new FileSystemPathIdentityResolver()));

        Assert.Equal(
            "Project manifest document source roots overlap: document 'Book1' sourcePath 'src/Shared' conflicts with document 'Book2' sourcePath 'src/Shared': vba-project.json",
            error.Message);
    }

    [Fact]
    public void SymbolicLinkAliasToAnotherSourceRootIsRejected()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sharedRoot = temp.CreateDirectory("SharedSource");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        Directory.CreateSymbolicLink(
            Path.Combine(sourceDirectory, "Alias"),
            sharedRoot);
        var manifest = CreateTwoDocumentManifest(
            "../SharedSource",
            "src/Alias");

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                Path.Combine(root, ProjectManifest.ManifestFileName),
                ProjectManifest.ManifestFileName,
                new FileSystemPathIdentityResolver()));

        Assert.Contains("document 'Book1' sourcePath '../SharedSource'", error.Message, StringComparison.Ordinal);
        Assert.Contains("document 'Book2' sourcePath 'src/Alias'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JunctionAliasToAnotherSourceRootIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sharedRoot = temp.CreateDirectory("SharedSource");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var junctionPath = Path.Combine(sourceDirectory, "JunctionAlias");
        CreateDirectoryJunction(
            junctionPath,
            sharedRoot);
        var manifest = CreateTwoDocumentManifest(
            "../SharedSource",
            "src/JunctionAlias");

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                Path.Combine(root, ProjectManifest.ManifestFileName),
                ProjectManifest.ManifestFileName,
                new FileSystemPathIdentityResolver()));

        Assert.Contains("document 'Book1' sourcePath '../SharedSource'", error.Message, StringComparison.Ordinal);
        Assert.Contains("document 'Book2' sourcePath 'src/JunctionAlias'", error.Message, StringComparison.Ordinal);
        Directory.Delete(junctionPath);
    }

    [Fact]
    public void CaseOnlyAliasToAnotherSourceRootIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "CaseRoot"));
        var manifest = CreateTwoDocumentManifest(
            "src/CaseRoot",
            "SRC/caseroot");

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                Path.Combine(root, ProjectManifest.ManifestFileName),
                ProjectManifest.ManifestFileName,
                new FileSystemPathIdentityResolver()));

        Assert.Contains("sourcePath 'src/CaseRoot'", error.Message, StringComparison.Ordinal);
        Assert.Contains("sourcePath 'SRC/caseroot'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityLookupFailureRejectsTheCompleteManifest()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = CreateTwoDocumentManifest(
            "src/Book1",
            "src/Book2");

        var error = Assert.Throws<VbaProjectManifestException>(() =>
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                Path.Combine(root, ProjectManifest.ManifestFileName),
                ProjectManifest.ManifestFileName,
                new FailingIdentityResolver()));

        Assert.Contains("Document 'Book1' sourcePath 'src/Book1'", error.Message, StringComparison.Ordinal);
        Assert.Contains("safely resolvable filesystem identity", error.Message, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
    }

    [Fact]
    public void PhysicallyDisjointRootsAreAccepted()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Book2"));
        var manifest = CreateTwoDocumentManifest(
            "src/Book1",
            "src/Book2");

        var identities = DocumentSourceSetIsolationValidator.ResolveAndValidate(
            manifest,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            ProjectManifest.ManifestFileName,
            new FileSystemPathIdentityResolver());

        Assert.Equal(2, identities.Count);
        Assert.False(FileSystemPathIdentityRelations.RootsOverlap(
            identities["Book1"],
            identities["Book2"]));
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start junction creation.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Junction creation failed: {standardOutput}{standardError}");
    }

    private static ProjectManifest CreateTwoDocumentManifest(
        string firstSourcePath,
        string secondSourcePath)
        => new(
            ProjectManifest.CurrentSchemaVersion,
            "Project",
            "Book1",
            new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = ProjectDocument.CreateExcel("Book1") with
                {
                    SourcePath = firstSourcePath
                },
                ["Book2"] = ProjectDocument.CreateExcel("Book2") with
                {
                    SourcePath = secondSourcePath
                }
            });

    private sealed class FailingIdentityResolver : IFileSystemPathIdentityResolver
    {
        public FileSystemPathIdentity Resolve(string path)
            => throw new UnauthorizedAccessException(path);
    }
}
