using VbaDev.Infrastructure.FileSystem;
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
        var fixturePath = ProjectManifestFixturePath(fixtureName);
        var manifest = ProjectManifestReader.Parse(
            File.ReadAllText(fixturePath),
            fixtureName);
        _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
            manifest,
            fixturePath,
            fixtureName,
            new FileSystemPathIdentityResolver());

        Assert.Equal(expectedProjectName, manifest.ProjectName);
        Assert.Equal(expectedPrimaryDocument, manifest.PrimaryDocument);
        Assert.Equal(expectedDocumentCount, manifest.Documents.Count);
    }

    [Theory]
    [InlineData("invalid-missing-primary-document.json", "primaryDocument")]
    [InlineData("invalid-missing-reference-requested.json", "requested")]
    [InlineData("invalid-primary-document-not-defined.json", "primaryDocument")]
    [InlineData("invalid-empty-reference-name.json", "reference name")]
    [InlineData("invalid-empty-common-modules-repository.json", "commonModulesRepository")]
    [InlineData("invalid-empty-command-defaults.json", "commandDefaults")]
    [InlineData("invalid-empty-excel-automation-defaults.json", "commandDefaults.excelAutomation")]
    [InlineData("invalid-empty-project-name.json", "projectName")]
    [InlineData("invalid-empty-primary-document.json", "primaryDocument")]
    [InlineData("invalid-empty-test-defaults.json", "commandDefaults.test")]
    [InlineData("invalid-unknown-root-property.json", "unexpected")]
    [InlineData("invalid-unknown-document-property.json", "unexpected")]
    [InlineData("invalid-unknown-common-module-property.json", "unexpected")]
    [InlineData("invalid-unknown-command-default-property.json", "unexpected")]
    [InlineData("invalid-unknown-excel-automation-default-property.json", "unexpected")]
    [InlineData("invalid-unknown-test-default-property.json", "unexpected")]
    [InlineData("invalid-workbook-open-timeout.json", "workbookOpenTimeoutSeconds")]
    [InlineData("invalid-workbook-save-timeout.json", "workbookSaveTimeoutSeconds")]
    [InlineData("invalid-mis-cased-root-property.json", "ProjectName")]
    [InlineData("invalid-mis-cased-test-default-property.json", "Format")]
    [InlineData("invalid-mis-cased-document-kind.json", "EXCEL")]
    [InlineData("invalid-test-execution-timeout.json", "executionTimeoutSeconds")]
    [InlineData("invalid-test-format.json", "JSON")]
    [InlineData("invalid-missing-selection-arrays.json", "commonModules")]
    [InlineData("invalid-missing-template-path.json", "templatePath")]
    [InlineData("invalid-null-optional-property.json", "commonModulesRepository")]
    [InlineData("invalid-null-command-default.json", "test")]
    [InlineData("invalid-null-document.json", "Book1")]
    [InlineData("invalid-null-reference.json", "null")]
    [InlineData("invalid-schema-version.json", "schemaVersion")]
    [InlineData("invalid-standard-library-reference.json", "always active")]
    [InlineData("invalid-untrimmed-reference-name.json", "leading or trailing")]
    [InlineData("invalid-duplicate-reference-name.json", "duplicate")]
    [InlineData("invalid-equal-source-roots.json", "Book1")]
    [InlineData("invalid-nested-source-roots.json", "Book2")]
    public void SharedInvalidFixturesFailLanguageServerManifestValidation(
        string fixtureName,
        string expectedMessage)
    {
        var fixturePath = ProjectManifestFixturePath(fixtureName);
        var ex = Assert.Throws<VbaProjectManifestException>(() =>
        {
            var manifest = ProjectManifestReader.Parse(
                File.ReadAllText(fixturePath),
                fixtureName);
            _ = DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                fixturePath,
                fixtureName,
                new FileSystemPathIdentityResolver());
        });

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
