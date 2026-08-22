using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildSourceSnapshotOutputSafetyValidatorTests
{
    [Fact]
    public void ValidatedPathsRemainBoundWhenCallerRetargetsOriginalAliases()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var context = new ProjectContextResolver(new JsonProjectManifestStore()).Resolve(
            new ProjectResolutionRequest(root, null, root));
        Directory.CreateDirectory(Path.GetDirectoryName(context.TemplateDocumentPath)!);
        File.WriteAllText(context.TemplateDocumentPath, "selected-template", Encoding.UTF8);

        var acceptedSnapshotPath = temp.CreateDirectory("accepted-snapshot");
        File.WriteAllText(
            Path.Combine(acceptedSnapshotPath, "Accepted.bas"),
            "Attribute VB_Name = \"Accepted\"",
            Encoding.UTF8);
        var laterSnapshotPath = temp.CreateDirectory("later-snapshot");
        File.WriteAllText(
            Path.Combine(laterSnapshotPath, "Later.bas"),
            "Attribute VB_Name = \"Later\"",
            Encoding.UTF8);
        var snapshotAliasPath = Path.Combine(temp.Path, "snapshot-alias");
        Directory.CreateSymbolicLink(snapshotAliasPath, acceptedSnapshotPath);

        var acceptedOutputDirectory = temp.CreateDirectory("accepted-output");
        var laterOutputDirectory = temp.CreateDirectory("later-output");
        var outputAliasPath = Path.Combine(temp.Path, "output-alias");
        Directory.CreateSymbolicLink(outputAliasPath, acceptedOutputDirectory);
        var selectedOutputPath = Path.Combine(outputAliasPath, "Book1.xlsm");

        var validatedPaths = new BuildSourceSnapshotOutputSafetyValidator().Validate(
            context,
            snapshotAliasPath,
            selectedOutputPath);

        Directory.Delete(snapshotAliasPath);
        Directory.CreateSymbolicLink(snapshotAliasPath, laterSnapshotPath);
        Directory.Delete(outputAliasPath);
        Directory.CreateSymbolicLink(outputAliasPath, laterOutputDirectory);

        using var capture = new BuildSourceSnapshotCaptureFactory(
                temp.CreateDirectory("scratch"))
            .Create(validatedPaths.SourceSnapshotPath, CancellationToken.None);
        using var transaction = WorkbookOutputTransaction.Create(
            context.TemplateDocumentPath,
            validatedPaths.OutputPath);
        transaction.Commit();

        Assert.Equal("Accepted.bas", Assert.Single(capture.SourceFiles).FileName);
        Assert.True(File.Exists(Path.Combine(acceptedOutputDirectory, "Book1.xlsm")));
        Assert.False(File.Exists(Path.Combine(laterOutputDirectory, "Book1.xlsm")));
    }
}
