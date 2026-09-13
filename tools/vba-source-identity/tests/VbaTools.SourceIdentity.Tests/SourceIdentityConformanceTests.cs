using System.Text.Json;
using Xunit;

namespace VbaTools.SourceIdentities.Tests;

public sealed class SourceIdentityConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var document = ReadCorpus();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expected = !OperatingSystem.IsWindows() && item.TryGetProperty("posix", out var posix)
                ? posix : item.GetProperty("windows");
            yield return [item.GetProperty("id").GetString()!, item.GetProperty("uri").GetString()!,
                expected.GetProperty("accepted").GetBoolean(),
                expected.TryGetProperty("path", out var path) ? path.GetString()! : string.Empty];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Raw_and_already_admitted_URI_follow_the_shared_contract(string id, string raw, bool accepted, string path)
    {
        Assert.True(accepted == SourceIdentity.TryFromUri(raw, out var actual), id);
        Assert.Equal(path, actual.Path);
        if (Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            Assert.Equal(accepted, SourceIdentity.TryFromUri(parsed, out var reused));
            Assert.Equal(actual, reused);
            Assert.Equal(raw, parsed.OriginalString);
        }
    }

    [Fact]
    public void Equivalence_groups_define_equality_and_hashes_without_Unicode_normalization()
    {
        using var document = ReadCorpus();
        var admitted = new List<(string Group, SourceIdentity Identity)>();
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expected = !OperatingSystem.IsWindows() && item.TryGetProperty("posix", out var posix)
                ? posix : item.GetProperty("windows");
            if (!expected.GetProperty("accepted").GetBoolean()) continue;
            Assert.True(SourceIdentity.TryFromUri(item.GetProperty("uri").GetString(), out var identity));
            admitted.Add((expected.GetProperty("group").GetString()!, identity));
        }
        foreach (var first in admitted)
        foreach (var second in admitted)
        {
            Assert.Equal(first.Group == second.Group, first.Identity == second.Identity);
            if (first.Group == second.Group) Assert.Equal(first.Identity.GetHashCode(), second.Identity.GetHashCode());
        }
    }

    private static JsonDocument ReadCorpus()
        => JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "source-identity", "cases.json")));
}
