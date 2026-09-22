using System.Security.Cryptography;
using System.Text.Json;
using VbaDev.App.Build;
using VbaTools.Semantics;
using Xunit;

namespace VbaDev.Tests;

public sealed class TypeLibCatalogBuildEvidenceTests
{
    private static readonly VbaProjectReferenceCatalogIdentity Identity =
        new("Fixture", "000204ef-0000-0000-c000-000000000046", 4, 2, 1041, "C:/fixture/library.dll/2");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void CapturedInputPreservesOrderAndDuplicatesAndReplaysThroughTheRealBuilder()
    {
        using var temp = TempDirectory.Create();
        var member = new TypeLibCatalogMember("Item", VbaSourceDefinitionKind.Property, "Japanese: \u65e5\u672c\u8a9e\r\n\u001f");
        var type = new TypeLibCatalogType("Second", VbaSourceDefinitionKind.Module, null, [member, member]);
        var metadata = new TypeLibCatalogMetadata("Fixture", [type, type with { Name = "first" }, type], "Fixture");

        var actual = new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata);

        var capture = Assert.Single(Directory.GetDirectories(temp.Path));
        using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture, "input.json")));
        var replayInput = input.RootElement.GetProperty("metadata").Deserialize<TypeLibCatalogMetadata>(JsonOptions)!;
        Assert.Equal(["Second", "first", "Second"], replayInput.Types.Select(item => item.Name));
        Assert.Equal(2, replayInput.Types[0].Members.Count);
        Assert.Equal(member.Documentation, replayInput.Types[0].Members[0].Documentation);
        Assert.Equal(Identity, input.RootElement.GetProperty("identity").Deserialize<VbaProjectReferenceCatalogIdentity>(JsonOptions));
        var replay = TypeLibReferenceCatalogBuilder.Build(Identity.ReferenceName, replayInput);
        Assert.Equal(JsonSerializer.Serialize(actual), JsonSerializer.Serialize(replay));
    }

    [Fact]
    public void BuilderFailureRetainsVerifiedInputAndTheOriginalExceptionStack()
    {
        using var temp = TempDirectory.Create();
        var metadata = new TypeLibCatalogMetadata("Fixture", null!, "Fixture");

        var error = Assert.Throws<ArgumentNullException>(() =>
            new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata));

        Assert.Contains(nameof(TypeLibReferenceCatalogBuilder.Build), error.StackTrace);
        var capture = Assert.Single(Directory.GetDirectories(temp.Path));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(capture, "input.json"))));
        using var prepared = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "prepared.json")));
        using var outcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "outcome.json")));
        Assert.Equal(hash, prepared.RootElement.GetProperty("inputSha256").GetString());
        Assert.Equal(hash, outcome.RootElement.GetProperty("inputSha256").GetString());
        Assert.Equal("failed", outcome.RootElement.GetProperty("status").GetString());
        var capturedException = outcome.RootElement.GetProperty("exception");
        Assert.Equal(error.GetType().FullName, capturedException.GetProperty("type").GetString());
        Assert.StartsWith(capturedException.GetProperty("details").GetString()!, error.ToString());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("missing")]
    [InlineData("relative")]
    public void UnavailableOrUnsafeEvidenceRootDoesNotChangeTheBuilderResultOrFailure(string mode)
    {
        using var temp = TempDirectory.Create();
        var root = Path.Combine(temp.Path, "unavailable");
        if (mode == "file") File.WriteAllText(root, "keep");
        if (mode == "relative") root = Path.GetRelativePath(Environment.CurrentDirectory, root);
        var metadata = new TypeLibCatalogMetadata("Fixture", [], null);

        var result = new TypeLibCatalogBuildEvidence(root).Build(Identity, metadata);

        Assert.Equal(JsonSerializer.Serialize(TypeLibReferenceCatalogBuilder.Build(Identity.ReferenceName, metadata)),
            JsonSerializer.Serialize(result));
        Assert.Throws<ArgumentNullException>(() =>
            new TypeLibCatalogBuildEvidence(root).Build(Identity, metadata with { Types = null! }));
        Assert.Empty(Directory.GetDirectories(temp.Path));
        if (mode == "file") Assert.Equal("keep", File.ReadAllText(root));
    }

    [Fact]
    public void UnpairedSurrogateMarksCaptureIncompleteWithoutChangingTheProductResult()
    {
        using var temp = TempDirectory.Create();
        var metadata = new TypeLibCatalogMetadata("Fixture", [
            new("Example", VbaSourceDefinitionKind.Module, "invalid: \ud800", [])]);

        var result = new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata);

        Assert.Equal(JsonSerializer.Serialize(TypeLibReferenceCatalogBuilder.Build(Identity.ReferenceName, metadata)),
            JsonSerializer.Serialize(result));
        var capture = Assert.Single(Directory.GetDirectories(temp.Path));
        Assert.True(File.Exists(Path.Combine(capture, "capture-error.json")));
        Assert.False(File.Exists(Path.Combine(capture, "prepared.json")));
    }

    [Fact]
    public void CaptureIdentifiesTheActualRuntimeAndLinqAssembly()
    {
        using var temp = TempDirectory.Create();
        new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, new("Fixture", []));
        var capture = Assert.Single(Directory.GetDirectories(temp.Path));
        using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture, "input.json")));
        var runtime = input.RootElement.GetProperty("runtime");
        Assert.Equal(Environment.ProcessId, runtime.GetProperty("processId").GetInt32());
        Assert.Equal(Environment.Version.ToString(), runtime.GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(runtime.GetProperty("processStartedAtUtc").GetString()));
        var linq = Assert.Single(runtime.GetProperty("assemblies").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "System.Linq");
        Assert.Equal(typeof(Enumerable).Assembly.ManifestModule.ModuleVersionId.ToString(),
            linq.GetProperty("moduleVersionId").GetString());
        var executable = runtime.GetProperty("executable");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!))),
            executable.GetProperty("sha256").GetString());
    }
}
