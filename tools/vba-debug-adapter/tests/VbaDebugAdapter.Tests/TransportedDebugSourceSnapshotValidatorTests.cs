using System.Text.Json;
using VbaDebugAdapter.Build;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class TransportedDebugSourceSnapshotValidatorTests
{
    [Theory]
    [InlineData("empty", "complete source inventory")]
    [InlineData("unsafe-path", "unambiguous Windows path components")]
    [InlineData("duplicate-path", "duplicate path")]
    [InlineData("duplicate-flat-name", "duplicate flat source identity")]
    [InlineData("missing-uri", "unique persistent file URI")]
    [InlineData("mismatched-uri", "persistent relative path")]
    [InlineData("outside-source-set", "outside the persistent source set")]
    [InlineData("invalid-base64", "invalid base64")]
    [InlineData("unsupported-file", "unsupported path")]
    [InlineData("orphan-sidecar", "same-directory form")]
    [InlineData("sidecar-text-metadata", "must not declare text metadata")]
    [InlineData("unordered", "canonical relative-path order")]
    [InlineData("missing-active-source", "active source")]
    [InlineData("duplicate-breakpoint", "duplicate breakpoint")]
    public void SchemaTwoRetainsIndependentInventoryAndIdentityAdmission(
        string invalidEvidence,
        string expectedMessage)
    {
        var source = new TransportedDebugSource(
            "Module1.bas", "file:///C:/persistent/Module1.bas", "windows-1252", "QQ==");
        var secondSource = source with
        {
            RelativePath = "Module2.bas",
            SourceUri = "file:///C:/persistent/Module2.bas"
        };
        var snapshot = new TransportedDebugSourceSnapshot(2, [source]);
        snapshot = invalidEvidence switch
        {
            "empty" => snapshot with { Sources = [] },
            "unsafe-path" => snapshot with
            {
                Sources = [source with { RelativePath = "../Module1.bas" }]
            },
            "duplicate-path" => snapshot with { Sources = [source, source] },
            "duplicate-flat-name" => snapshot with
            {
                Sources = [source, source with
                {
                    RelativePath = "nested/Module1.bas",
                    SourceUri = "file:///C:/persistent/nested/Module1.bas"
                }]
            },
            "missing-uri" => snapshot with { Sources = [source with { SourceUri = null }] },
            "mismatched-uri" => snapshot with
            {
                Sources = [source with { SourceUri = secondSource.SourceUri }]
            },
            "outside-source-set" => snapshot with
            {
                Sources = [source, secondSource with { SourceUri = "file:///C:/outside/Module2.bas" }]
            },
            "invalid-base64" => snapshot with
            {
                Sources = [source with { ContentBase64 = "not-base64" }]
            },
            "unsupported-file" => snapshot with
            {
                Sources = [source with { RelativePath = "Module1.txt" }]
            },
            "orphan-sidecar" => snapshot with
            {
                Sources = [new TransportedDebugSource("Form1.frx", null, null, "AA==")]
            },
            "sidecar-text-metadata" => snapshot with
            {
                Sources = [source with { RelativePath = "Form1.frx" }]
            },
            "unordered" => snapshot with { Sources = [secondSource, source] },
            "missing-active-source" => snapshot with
            {
                ActiveSource = new TransportedDebugSourcePosition(secondSource.SourceUri!, 0, 0)
            },
            "duplicate-breakpoint" => snapshot with
            {
                Breakpoints =
                [
                    new TransportedDebugSourceBreakpoint(source.SourceUri!, 0),
                    new TransportedDebugSourceBreakpoint(source.SourceUri!, 0)
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidEvidence))
        };
        var validator = new TransportedDebugSourceSnapshotValidator(1252);

        var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(932, "utf8", "QQ==")]
    [InlineData(65001, "windows-65001", "QQ==")]
    [InlineData(932, "windows-1252", "QQ==")]
    [InlineData(1252, "windows-932", "QQ==")]
    [InlineData(65001, "utf8", "77u/QQ==")]
    [InlineData(1252, "windows-1252", "77u/QQ==")]
    [InlineData(1252, "utf8bom", "QQ==")]
    [InlineData(1252, "utf16le", "/v8AQQ==")]
    public void EncodingDeclarationMustMatchFixedAcpAndBom(
        int activeCodePage,
        string encoding,
        string bytesBase64)
    {
        var validator = new TransportedDebugSourceSnapshotValidator(activeCodePage);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [new TransportedDebugSource(
                "Module1.bas", "file:///C:/persistent/Module1.bas", encoding, bytesBase64)]);

        Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void OnlyTheExactSnapshotSchemaIsAdmitted(int schemaVersion)
    {
        var validator = new TransportedDebugSourceSnapshotValidator(1252);
        var snapshot = new TransportedDebugSourceSnapshot(
            schemaVersion,
            [new TransportedDebugSource(
                "Module1.bas", "file:///C:/persistent/Module1.bas", "windows-1252", "QQ==")]);

        var error = Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));

        Assert.Contains("schema version", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SourceEncodingCases))]
    public void SourceEncodingCorpusIsAdmittedIndependentlyWithoutChangingBytes(
        string id,
        int activeCodePage,
        string fileName,
        string bytesBase64,
        string? expectedText,
        string? expectedEncoding,
        bool expectedFailure)
    {
        Assert.False(string.IsNullOrWhiteSpace(id));
        var bytes = Convert.FromBase64String(bytesBase64);
        var declaredEncoding = expectedEncoding ?? (
            bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? "utf8bom" :
            bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? "utf16le" :
            bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? "utf16be" :
            activeCodePage == 65001 ? "utf8" : $"windows-{activeCodePage}");
        var validator = new TransportedDebugSourceSnapshotValidator(activeCodePage);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [new TransportedDebugSource(
                fileName,
                $"file:///C:/persistent/{fileName}",
                declaredEncoding,
                bytesBase64)]);

        if (expectedFailure)
        {
            Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));
            return;
        }

        var source = Assert.Single(validator.Validate(snapshot).Sources);
        Assert.Equal(expectedEncoding, source.Encoding);
        Assert.Equal(expectedText, source.Text);
        Assert.Equal(bytes, source.Bytes);
    }

    public static IEnumerable<object?[]> SourceEncodingCases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var corpusPath = Path.Combine(
                directory.FullName, "fixtures", "vba-source-encoding", "cases.json");
            if (File.Exists(corpusPath))
            {
                using var corpus = JsonDocument.Parse(File.ReadAllBytes(corpusPath));
                Assert.Equal(1, corpus.RootElement.GetProperty("schemaVersion").GetInt32());
                foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
                {
                    yield return
                    [
                        item.GetProperty("id").GetString(),
                        item.GetProperty("activeCodePage").GetInt32(),
                        item.GetProperty("fileName").GetString(),
                        item.GetProperty("bytesBase64").GetString(),
                        item.TryGetProperty("expectedText", out var text) ? text.GetString() : null,
                        item.TryGetProperty("expectedEncoding", out var encoding)
                            ? encoding.GetString() : null,
                        item.TryGetProperty("expectedFailure", out var failure) && failure.GetBoolean()
                    ];
                }
                yield break;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The neutral VBA source-encoding corpus was not found.");
    }

    [Fact]
    public void SchemaTwoPreservesDualValidAcpBytesAndTheirAcpText()
    {
        byte[] bytes = [0xc3, 0xa9];
        var validator = new TransportedDebugSourceSnapshotValidator(1252);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "windows-1252",
                    Convert.ToBase64String(bytes))
            ]);

        var validated = validator.Validate(snapshot);

        Assert.Equal(2, validated.SchemaVersion);
        var source = Assert.Single(validated.Sources);
        Assert.Equal("Ã©", source.Text);
        Assert.Equal(bytes, source.Bytes);
        Assert.Equal("windows-1252", source.Encoding);
    }

    [Fact]
    public void NonUtf8AcpRejectsBomlessUtf8DeclarationEvenWhenBytesAreDualValid()
    {
        var validator = new TransportedDebugSourceSnapshotValidator(1252);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "utf8",
                    Convert.ToBase64String([0xc3, 0xa9]))
            ]);

        Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));
    }

    [Theory]
    [InlineData(0x38)]
    [InlineData(0x39)]
    [InlineData(0x2b)]
    [InlineData(0x2f)]
    public void AcpDeclarationCannotReinterpretUnsupportedUtf7Signature(byte signatureSuffix)
    {
        var validator = new TransportedDebugSourceSnapshotValidator(1252);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "windows-1252",
                    Convert.ToBase64String([0x2b, 0x2f, 0x76, signatureSuffix, 0x2d, 0x41]))
            ]);

        Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));
    }

    [Fact]
    public void AcpDeclarationCannotReinterpretTruncatedUtf8Bom()
    {
        var validator = new TransportedDebugSourceSnapshotValidator(1252);
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "windows-1252",
                    Convert.ToBase64String([0xef, 0xbb]))
            ]);

        Assert.Throws<InvalidOperationException>(() => validator.Validate(snapshot));
    }
}
