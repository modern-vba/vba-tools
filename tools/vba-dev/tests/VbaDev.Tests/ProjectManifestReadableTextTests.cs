using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectManifestReadableTextTests
{
    [Fact]
    public void SaveAndReloadPreserveJsonEscapingAlongsideReadableJapanese()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var name = "集計\"引用\"\\区切り/" + string.Concat(Enumerable.Range(0, 32).Select(value => (char)value));
        var manifest = ProjectManifest.CreateDefault(name, "月次帳票", root, null);
        var store = new JsonProjectManifestStore();
        var path = Path.Combine(root, ProjectManifest.ManifestFileName);

        store.Save(root, manifest);

        var bytes = File.ReadAllBytes(path);
        var json = Encoding.Unicode.GetString(bytes.AsSpan(2));
        Assert.Contains("集計", json);
        Assert.Contains("引用", json);
        Assert.Contains("\\\\区切り/", json);
        Assert.Contains("\\u0000", json);
        Assert.Contains("\\n", json);
        Assert.Contains("\\r", json);
        Assert.Contains("\\t", json);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(name, parsed.RootElement.GetProperty("projectName").GetString());
        Assert.Equal(name, store.Load(path).ProjectName);

        store.Save(root, store.Load(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void SaveAndReloadPreserveReadableJapaneseAndStableCanonicalBytes()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("集計ツール", "月次帳票", root, null);
        var store = new JsonProjectManifestStore();
        var path = Path.Combine(root, ProjectManifest.ManifestFileName);
        var expectedBytes = ExpectedJapaneseManifestBytes();

        for (var cycle = 0; cycle < 3; cycle++)
        {
            store.Save(root, manifest);

            Assert.Equal(expectedBytes, File.ReadAllBytes(path));
            manifest = store.Load(path);
            Assert.Equal("集計ツール", manifest.ProjectName);
            Assert.Equal("月次帳票", manifest.PrimaryDocument);
            Assert.Equal("src/月次帳票", manifest.Documents["月次帳票"].SourcePath);
        }
    }

    [Theory]
    [InlineData("initial creation")]
    [InlineData("mutation")]
    [InlineData("recovery")]
    public async Task ManifestWritingPathsUseTheSameReadableCanonicalBytes(string writingPath)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("集計ツール", "月次帳票", root, null);
        var path = Path.Combine(root, ProjectManifest.ManifestFileName);
        var store = new JsonProjectManifestStore();

        switch (writingPath)
        {
            case "initial creation":
                using (var tracker = new NewProjectArtifactTracker(new WindowsExactFileSystemObjectOwnershipFactory()))
                {
                    var stage = NewProjectInitialManifestStager.Stage(
                        path, manifest, tracker, new FileSystemPathIdentityResolver());
                    Assert.Equal(ExpectedJapaneseManifestBytes(), File.ReadAllBytes(stage.TemporaryPath));
                    stage.CommitCreateOnly();
                }
                break;
            case "mutation":
                store.Save(root, manifest with { ProjectName = "Before" });
                await new ProjectManifestMutationCoordinator().ExecuteAsync(
                    root,
                    ProjectManifestMutationCommand.ReferenceRemove,
                    snapshot => ProjectManifestMutationPlan<string>.Commit(
                        snapshot.Manifest with { ProjectName = "集計ツール" }, "updated"),
                    CancellationToken.None);
                break;
            case "recovery":
                path = new ProjectManifestEditor(new ProjectManifestAtomicWriter())
                    .CreateRecoveryAfterFailedSave(root, manifest, new IOException("commit failed"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writingPath));
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ExpectedJapaneseManifestBytes(), bytes);
        Assert.Equal("集計ツール", store.Load(path).ProjectName);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16le-bom")]
    [InlineData("utf16be-bom")]
    public void AcceptedInputIsUnchangedUntilSaveThenUsesReadableCanonicalBytes(string inputEncoding)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var path = Path.Combine(root, ProjectManifest.ManifestFileName);
        var expectedBytes = ExpectedJapaneseManifestBytes();
        var json = Encoding.Unicode.GetString(expectedBytes.AsSpan(2));
        Encoding encoding = inputEncoding switch
        {
            "utf8" => new UTF8Encoding(false, true),
            "utf8-bom" => new UTF8Encoding(true, true),
            "utf16le-bom" => new UnicodeEncoding(false, true, true),
            "utf16be-bom" => new UnicodeEncoding(true, true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(inputEncoding))
        };
        var inputBytes = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
        File.WriteAllBytes(path, inputBytes);
        var store = new JsonProjectManifestStore();

        var manifest = store.Load(path);

        Assert.Equal(inputBytes, File.ReadAllBytes(path));
        Assert.Equal("集計ツール", manifest.ProjectName);
        store.Save(root, manifest);
        Assert.Equal(expectedBytes, File.ReadAllBytes(path));
    }

    private static byte[] ExpectedJapaneseManifestBytes()
    {
        var expected = """
        {
          "schemaVersion": 1,
          "projectName": "集計ツール",
          "primaryDocument": "月次帳票",
          "documents": {
            "月次帳票": {
              "kind": "excel",
              "sourcePath": "src/月次帳票",
              "templatePath": "src/月次帳票/月次帳票.xlsm",
              "publishPath": "publish/月次帳票.xlsm",
              "commonModules": [],
              "references": []
            }
          }
        }
        """.ReplaceLineEndings("\r\n") + "\r\n";
        return Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(expected)).ToArray();
    }
}
