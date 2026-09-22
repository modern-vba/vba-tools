using System.Collections;
using System.Text.Json;
using VbaDev.App.Build;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class TypeLibCatalogBuildEvidenceIntegrityTests
{
    private static readonly VbaProjectReferenceCatalogIdentity Identity =
        new("Fixture", "000204ef-0000-0000-c000-000000000046", 4, 2, 1041, "C:/fixture/library.dll/2");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void CapturedNestedMetadataRetainsEveryValueAndReplaysTheSameCatalog()
    {
        using var temp = TempDirectory.Create();
        const string text = "Japanese: \u65e5\u672c\u8a9e; combining: e\u0301; pair: \ud83d\ude80; \0\r\n\u001f\"\\";
        var parameter = new VbaCallableParameter("Value", text, IsOptional: true,
            DisplayLabel: "Optional ByRef Value() As External.Widget", TypeReference: new("Widget", "External"),
            IsByRef: true, IsParamArray: true, IsArray: true)
        {
            DefaultExpression = "\"" + text + "\"",
            TypeLibPassing = new(VbaTypeLibParameterDirection.InputOutput, 2),
        };
        var unknownParameter = parameter with
        {
            Name = "Unknown", Documentation = null, DisplayLabel = null, TypeReference = null,
            IsOptional = false, IsByRef = null, IsParamArray = false, IsArray = false,
            DefaultExpression = null, TypeLibPassing = new(VbaTypeLibParameterDirection.Unknown, null),
        };
        var signature = new VbaCallableSignature("Item(Value, Unknown)",
            [parameter, unknownParameter, parameter with { IsByRef = false, TypeLibPassing = null }, parameter],
            text, VbaCallableKind.Property, SupportsNamedArguments: false)
        {
            PassingConvention = VbaCallablePassingConvention.OtherExternal,
        };
        var member = new TypeLibCatalogMember("Item", VbaSourceDefinitionKind.Property, text, signature,
            new("Widget", "External"), VbaPropertyAccess.Readable | VbaPropertyAccess.Writable,
            new TypeLibCatalogCallableMetadata(0, 0x41, IsComplete: true)
            {
                PropertyAccessorKind = VbaPropertyAccessorKind.Get, IsReturnArray = true,
            });
        var incompleteMember = member with
        {
            Name = "Other", Documentation = null, TypeReference = null, PropertyAccess = VbaPropertyAccess.Unknown,
            Signature = signature with { CallableKind = null, SupportsNamedArguments = null },
            Metadata = member.Metadata! with
            {
                MemberId = -4, FunctionFlags = 0, IsComplete = false,
                PropertyAccessorKind = VbaPropertyAccessorKind.Let, IsReturnArray = false,
            },
        };
        var implemented = new TypeLibCatalogImplementedInterface("Events", 0x20, 3,
            [member, incompleteMember, member], TypeLibCatalogRawTypeKind.Dispatch, IsComplete: false);
        var type = new TypeLibCatalogType("Second", VbaSourceDefinitionKind.Module, text,
            [member, incompleteMember, member, member with { Signature = null, Metadata = null }],
            IsCreatable: true, IsApplicationObject: true, IsBrowsable: false,
            Metadata: new(TypeLibCatalogRawTypeKind.CoClass, 0x102,
                [implemented, implemented with { RawTypeKind = null, IsComplete = true }, implemented], IsComplete: false));
        var metadata = new TypeLibCatalogMetadata("Fixture", [type, type with
        {
            Name = "first", Documentation = null, IsCreatable = false, IsApplicationObject = false,
            IsBrowsable = true, Metadata = null,
        }, type], "ExactProject");

        var actual = new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata);

        var capture = Assert.Single(Directory.GetDirectories(temp.Path));
        using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture, "input.json")));
        var captured = input.RootElement.GetProperty("metadata");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(metadata, JsonOptions), captured));
        var restored = captured.Deserialize<TypeLibCatalogMetadata>(JsonOptions)!;
        Assert.True(JsonElement.DeepEquals(captured, JsonSerializer.SerializeToElement(restored, JsonOptions)));
        Assert.Equal(["Second", "first", "Second"], restored.Types.Select(item => item.Name));
        Assert.Equal(text, restored.Types[0].Members[0].Signature!.Parameters[0].Documentation);
        Assert.Equal(parameter.DefaultExpression, restored.Types[0].Members[0].Signature!.Parameters[0].DefaultExpression);
        var replay = TypeLibReferenceCatalogBuilder.Build(Identity.ReferenceName, restored);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual, JsonOptions),
            JsonSerializer.SerializeToElement(replay, JsonOptions)));
        using var outcome = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture, "outcome.json")));
        Assert.Equal("succeeded", outcome.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(capture, "capture-error.json")));
    }

    [Fact]
    public void OutcomeStorageFailurePreservesTheSuccessfulCatalog()
    {
        using var temp = TempDirectory.Create();
        TypeLibCatalogType[] types = [new("Example", VbaSourceDefinitionKind.Module, "keep", [])];
        var blockingTypes = new PreparedOutcomeFailureList(temp.Path, types);
        var metadata = new TypeLibCatalogMetadata("Fixture", blockingTypes);
        var expected = TypeLibReferenceCatalogBuilder.Build(Identity.ReferenceName, metadata with { Types = types });

        var actual = new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata);

        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, JsonOptions),
            JsonSerializer.SerializeToElement(actual, JsonOptions)));
        AssertOutcomeWriteWasBlocked(temp.Path, blockingTypes);
    }

    [Fact]
    public void OutcomeStorageFailurePreservesTheExactBuilderExceptionAndStack()
    {
        using var temp = TempDirectory.Create();
        var original = new InvalidOperationException("Original builder input failure.");
        var blockingTypes = new PreparedOutcomeFailureList(temp.Path,
            [new("Example", VbaSourceDefinitionKind.Module, null, [])], original);
        var metadata = new TypeLibCatalogMetadata("Fixture", blockingTypes);

        var actual = Record.Exception(() => new TypeLibCatalogBuildEvidence(temp.Path).Build(Identity, metadata));

        Assert.Same(original, actual);
        Assert.Contains(nameof(TypeLibReferenceCatalogBuilder.Build), original.StackTrace);
        Assert.Contains(nameof(PreparedOutcomeFailureList.GetEnumerator), original.StackTrace);
        AssertOutcomeWriteWasBlocked(temp.Path, blockingTypes);
    }

    private static void AssertOutcomeWriteWasBlocked(string root, PreparedOutcomeFailureList types)
    {
        Assert.Equal(1, types.InjectionCount);
        var capture = Assert.Single(Directory.GetDirectories(root));
        Assert.True(File.Exists(Path.Combine(capture, "input.json")));
        Assert.True(File.Exists(Path.Combine(capture, "prepared.json")));
        Assert.True(Directory.Exists(Path.Combine(capture, "outcome.json")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(capture, "outcome.json")));
        using var error = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(capture, "capture-error.json")));
        Assert.Equal("capture-failed", error.RootElement.GetProperty("status").GetString());
    }

    // The prepared receipt is the observable boundary between capture and the real builder.
    // Block only the outcome file, without timers, threads, ACL changes, or a product test hook.
    private sealed class PreparedOutcomeFailureList(string root, IReadOnlyList<TypeLibCatalogType> values,
        Exception? builderFailure = null) : IReadOnlyList<TypeLibCatalogType>
    {
        public int InjectionCount { get; private set; }
        public int Count => values.Count;
        public TypeLibCatalogType this[int index] => values[index];

        public IEnumerator<TypeLibCatalogType> GetEnumerator()
        {
            if (InjectionCount == 0)
            {
                var capture = Directory.GetDirectories(root)
                    .SingleOrDefault(path => File.Exists(Path.Combine(path, "prepared.json")));
                if (capture is not null)
                {
                    Directory.CreateDirectory(Path.Combine(capture, "outcome.json"));
                    InjectionCount++;
                    if (builderFailure is not null) throw builderFailure;
                }
            }
            return values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
