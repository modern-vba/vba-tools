using System.Text.Json.Nodes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevReferenceListContractTests
{
    private static readonly string ProjectPath = Path.GetFullPath(@"C:\work\Project");

    [Fact]
    public void CompleteNonzeroResponseAcceptsAdditiveFieldsAndUnknownWarningCodes()
    {
        var root = CreateResolvedResponse("library a");
        root["futureRoot"] = new JsonObject { ["enabled"] = true };
        root["warnings"] = new JsonArray(
            new JsonObject
            {
                ["code"] = "futureWarning",
                ["message"] = "A future warning remains informational.",
                ["futureMessageField"] = 1
            });
        var entry = root["references"]!.AsArray()[0]!.AsObject();
        entry["futureEntry"] = "ignored";
        entry["identity"]!.AsObject()["futureIdentity"] = "ignored";

        var result = Parse(root, ["Library A"], exitCode: 9);

        Assert.True(result.IsTrusted);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Assert.Single(result.Entries).Identity?.Guid);
    }

    [Fact]
    public void CompleteResponseWithDiagnosticRejectsWholeInvocation()
    {
        var root = CreateResolvedResponse("Library A");
        root["diagnostics"] = new JsonArray(
            new JsonObject
            {
                ["code"] = "futureDiagnostic",
                ["message"] = "An unknown diagnostic cannot select an identity."
            });

        var result = Parse(root, ["Library A"]);

        Assert.False(result.IsTrusted);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("scope")]
    [InlineData("project")]
    [InlineData("document")]
    [InlineData("mode")]
    [InlineData("incomplete")]
    [InlineData("unknown-status")]
    [InlineData("unknown-reason")]
    [InlineData("wrong-type")]
    [InlineData("missing-required")]
    [InlineData("inconsistent-resolved")]
    [InlineData("uppercase-guid")]
    [InlineData("version-range")]
    [InlineData("version-exponent")]
    [InlineData("duplicate-control")]
    [InlineData("malformed")]
    public void InvalidControlOrIdentityPayloadRejectsWholeInvocation(string scenario)
    {
        var root = CreateResolvedResponse("Library A");
        string json;
        switch (scenario)
        {
            case "schema":
                root["schemaVersion"] = "2.0";
                json = root.ToJsonString();
                break;
            case "scope":
                root["scope"] = "environment";
                json = root.ToJsonString();
                break;
            case "project":
                root["project"] = Path.GetFullPath(@"C:\work\Other");
                json = root.ToJsonString();
                break;
            case "document":
                root["document"] = "Book2";
                json = root.ToJsonString();
                break;
            case "mode":
                root["mode"] = "available";
                json = root.ToJsonString();
                break;
            case "incomplete":
                root["complete"] = false;
                json = root.ToJsonString();
                break;
            case "unknown-status":
                root["references"]!.AsArray()[0]!.AsObject()["status"] = "future";
                json = root.ToJsonString();
                break;
            case "unknown-reason":
                var unknownReasonEntry = root["references"]!.AsArray()[0]!.AsObject();
                unknownReasonEntry["status"] = "unavailable";
                unknownReasonEntry.Remove("identity");
                unknownReasonEntry["reasonCode"] = "futureReason";
                unknownReasonEntry["candidates"] = new JsonArray();
                unknownReasonEntry["message"] = "A future reason is not authoritative.";
                json = root.ToJsonString();
                break;
            case "wrong-type":
                root["complete"] = "true";
                json = root.ToJsonString();
                break;
            case "missing-required":
                root.Remove("warnings");
                json = root.ToJsonString();
                break;
            case "inconsistent-resolved":
                root["references"]!.AsArray()[0]!.AsObject()["candidates"] = new JsonArray();
                json = root.ToJsonString();
                break;
            case "uppercase-guid":
                root["references"]!.AsArray()[0]!["identity"]!["guid"] =
                    "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
                json = root.ToJsonString();
                break;
            case "version-range":
                root["references"]!.AsArray()[0]!["identity"]!["major"] = 65536;
                json = root.ToJsonString();
                break;
            case "version-exponent":
                json = root.ToJsonString().Replace("\"major\":1", "\"major\":1e0", StringComparison.Ordinal);
                break;
            case "duplicate-control":
                json = root.ToJsonString().Replace(
                    "\"scope\":\"project\"",
                    "\"scope\":\"project\",\"scope\":\"project\"",
                    StringComparison.Ordinal);
                break;
            case "malformed":
                json = "{not-json";
                break;
            default:
                throw new InvalidOperationException($"Unknown test scenario '{scenario}'.");
        }

        var result = VbaDevReferenceListContract.Parse(
            new VbaDevReferenceListProcessResult(0, json, ""),
            ProjectPath,
            "Book1",
            [new VbaProjectReference("Library A")]);

        Assert.False(result.IsTrusted);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void CandidateIdentitiesMustBeDistinctAndCanonicallySorted()
    {
        var root = CreateIssueResponse(
            "ambiguous",
            "multipleUsableIdentities",
            new JsonArray(
                CreateIdentity("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 1, 0),
                CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0)));

        var unsorted = Parse(root, ["Library A"]);
        root["references"]!.AsArray()[0]!["candidates"] = new JsonArray(
            CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0),
            CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0));
        var duplicate = Parse(root, ["Library A"]);
        root["references"]!.AsArray()[0]!["candidates"] = new JsonArray(
            CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 0, 65535),
            CreateIdentity("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", 65535, 0));
        var canonical = Parse(root, ["Library A"], exitCode: 4);

        Assert.False(unsorted.IsTrusted);
        Assert.False(duplicate.IsTrusted);
        Assert.True(canonical.IsTrusted);
        Assert.Null(Assert.Single(canonical.Entries).Identity);
    }

    [Fact]
    public void NotRegisteredRequiresEmptyCandidatesWhileNoUsableIdentityMayRetainCandidates()
    {
        var notRegisteredRoot = CreateIssueResponse(
            "unavailable",
            "notRegistered",
            new JsonArray());
        var notRegistered = Parse(notRegisteredRoot, ["Library A"], exitCode: 2);
        notRegisteredRoot["references"]!.AsArray()[0]!["candidates"] = new JsonArray(
            CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0));
        var inconsistentNotRegistered = Parse(notRegisteredRoot, ["Library A"]);
        var noUsableIdentity = Parse(
            CreateIssueResponse(
                "unavailable",
                "noUsableIdentity",
                new JsonArray(
                    CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0))),
            ["Library A"],
            exitCode: 2);

        Assert.True(notRegistered.IsTrusted);
        Assert.False(inconsistentNotRegistered.IsTrusted);
        Assert.True(noUsableIdentity.IsTrusted);
    }

    [Theory]
    [InlineData("probeTimeout")]
    [InlineData("cancelled")]
    public void UnverifiedEntryRejectsOtherwiseCompleteInvocation(string reasonCode)
    {
        var root = CreateIssueResponse(
            "unverified",
            reasonCode,
            new JsonArray(CreateIdentity("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0)));

        var result = Parse(root, ["Library A"], exitCode: 1);

        Assert.False(result.IsTrusted);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void ConfiguredEntriesMustMatchManifestOrderAndMembershipCaseInsensitively()
    {
        var root = CreateEnvelope();
        root["references"] = new JsonArray(
            CreateResolvedEntry("Library B", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            CreateResolvedEntry("Library A", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var reordered = Parse(root, ["Library A", "Library B"]);
        root["references"] = new JsonArray(
            CreateResolvedEntry("library a", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CreateResolvedEntry("LIBRARY B", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var exactMembership = Parse(root, ["Library A", "Library B"]);

        Assert.False(reordered.IsTrusted);
        Assert.True(exactMembership.IsTrusted);

        root["references"] = new JsonArray(
            CreateResolvedEntry("LIBRARY A", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            CreateResolvedEntry("library a", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var repeatedOccurrences = Parse(root, ["Library A", "library a"]);
        Assert.True(repeatedOccurrences.IsTrusted);
        Assert.Equal(2, repeatedOccurrences.Entries.Count);
    }

    private static VbaDevReferenceListInvocationResult Parse(
        JsonObject root,
        IReadOnlyList<string> referenceNames,
        int exitCode = 0)
        => VbaDevReferenceListContract.Parse(
            new VbaDevReferenceListProcessResult(exitCode, root.ToJsonString(), ""),
            ProjectPath,
            "Book1",
            referenceNames.Select(name => new VbaProjectReference(name)).ToArray());

    private static JsonObject CreateResolvedResponse(string name)
    {
        var root = CreateEnvelope();
        root["references"] = new JsonArray(
            CreateResolvedEntry(name, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        return root;
    }

    private static JsonObject CreateIssueResponse(
        string status,
        string reasonCode,
        JsonArray candidates)
    {
        var root = CreateEnvelope();
        root["references"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "Library A",
                ["status"] = status,
                ["reasonCode"] = reasonCode,
                ["candidates"] = candidates,
                ["message"] = "The reference was not conclusively resolved."
            });
        return root;
    }

    private static JsonObject CreateEnvelope()
        => new()
        {
            ["schemaVersion"] = "1.0",
            ["scope"] = "project",
            ["project"] = ProjectPath,
            ["document"] = "Book1",
            ["mode"] = "configured",
            ["complete"] = true,
            ["warnings"] = new JsonArray(),
            ["references"] = new JsonArray()
        };

    private static JsonObject CreateResolvedEntry(string name, string guid)
        => new()
        {
            ["name"] = name,
            ["status"] = "resolved",
            ["identity"] = CreateIdentity(guid, 1, 0)
        };

    private static JsonObject CreateIdentity(string guid, int major, int minor)
        => new()
        {
            ["guid"] = guid,
            ["major"] = major,
            ["minor"] = minor
        };
}
