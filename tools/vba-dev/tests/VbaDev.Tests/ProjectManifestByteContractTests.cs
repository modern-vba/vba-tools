using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectManifestByteContractTests
{
    public static IEnumerable<object[]> ConformanceCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-manifest-encoding", "cases.json")));
        foreach (var entry in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [entry.GetProperty("name").GetString()!,
                Convert.FromBase64String(entry.GetProperty("base64").GetString()!),
                entry.GetProperty("accepted").GetBoolean()];
        }
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void DiskManifestLoadObeysTheSharedByteContract(string name, byte[] bytes, bool accepted)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.CreateDirectory(name), ProjectManifest.ManifestFileName);
        File.WriteAllBytes(path, bytes);
        var store = new JsonProjectManifestStore();

        if (accepted)
        {
            Assert.Equal("Encoding 日本語 🙂 �", store.Load(path).ProjectName);
        }
        else
        {
            var error = Assert.Throws<ProjectManifestException>(() => store.Load(path));
            Assert.Contains("encoding", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task MutationRejectsInvalidBytesBeforeRebasingOrLaunchingWork()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        var bytes = (byte[])ConformanceCases().Single(entry => (string)entry[0] == "utf32le-bom")[1];
        File.WriteAllBytes(path, bytes);
        var rebased = false;

        var error = await Assert.ThrowsAsync<ProjectManifestException>(() =>
            new ProjectManifestMutationCoordinator().ExecuteAsync(
                temp.Path, ProjectManifestMutationCommand.ReferenceRemove,
                _ =>
                {
                    rebased = true;
                    return ProjectManifestMutationPlan<string>.NoOp("unchanged");
                }, CancellationToken.None));

        Assert.Contains("encoding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(rebased);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void InvalidUtf8CannotEstablishAProjectManifestThroughReplacementDecoding()
    {
        using var temp = TempDirectory.Create();
        var manifest = ProjectManifest.CreateDefault("BeforeXAfter", "Book1", temp.Path, null);
        var json = Encoding.Unicode.GetString(
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest).AsSpan(2));
        var bytes = Encoding.UTF8.GetBytes(json);
        bytes[json.IndexOf('X')] = 0xff;
        var path = Path.Combine(temp.Path, ProjectManifest.ManifestFileName);
        File.WriteAllBytes(path, bytes);

        var error = Assert.Throws<ProjectManifestException>(() => new JsonProjectManifestStore().Load(path));

        Assert.Contains("encoding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
