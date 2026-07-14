using VbaLanguageServer.ProjectModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ProjectManifestFixtureTests
{
    [Theory]
    [InlineData("primary-document.json", "PrimaryDocumentProject", "Book1", 1)]
    [InlineData("document-source-set.json", "DocumentSourceSetProject", "Book1", 1)]
    [InlineData("references.json", "ReferencesProject", "Book1", 1)]
    [InlineData("source-template.json", "SourceTemplateProject", "Book1", 1)]
    [InlineData("multi-document.json", "MultiDocumentProject", "Book1", 2)]
    public void SharedFixturesLoadAsLanguageServerProjectManifests(
        string fixtureName,
        string expectedProjectName,
        string expectedPrimaryDocument,
        int expectedDocumentCount)
    {
        var manifest = ProjectManifestReader.Parse(
            File.ReadAllText(ProjectManifestFixturePath(fixtureName)),
            fixtureName);

        Assert.Equal(expectedProjectName, manifest.ProjectName);
        Assert.Equal(expectedPrimaryDocument, manifest.PrimaryDocument);
        Assert.Equal(expectedDocumentCount, manifest.Documents.Count);
    }

    [Theory]
    [InlineData("invalid-missing-primary-document.json", "primaryDocument")]
    [InlineData("invalid-primary-document-not-defined.json", "primaryDocument")]
    [InlineData("invalid-empty-reference-name.json", "reference name")]
    public void SharedInvalidFixturesFailLanguageServerManifestValidation(
        string fixtureName,
        string expectedMessage)
    {
        var ex = Assert.Throws<VbaProjectManifestException>(() =>
            ProjectManifestReader.Parse(File.ReadAllText(ProjectManifestFixturePath(fixtureName)), fixtureName));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectManifestFixturePath(string fixtureName)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-manifest",
            fixtureName));
}
