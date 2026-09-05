using System.Text;
using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectDiskInventoryTests
{
    private const string InitialText =
        "Attribute VB_Name = \"Module1\"\n"
        + "Public Sub One()\n"
        + "End Sub\n";
    private const string ChangedSameLengthText =
        "Attribute VB_Name = \"Module1\"\n"
        + "Public Sub Two()\n"
        + "End Sub\n";

    [Fact]
    public void Disk_source_cache_and_invalidation_use_document_identity()
    {
        var identityType = typeof(VbaDocumentIdentity);
        var invalidateSource = typeof(IVbaProjectDiskInventory)
            .GetMethod("InvalidateSource");
        Assert.NotNull(invalidateSource);
        Assert.Equal(
            identityType,
            Assert.Single(invalidateSource.GetParameters()).ParameterType);

        foreach (var (ownerType, fieldName) in new[]
        {
            (typeof(VbaFileSystemProjectDiskInventory), "sourceCache"),
            (typeof(VbaFileSystemProjectDiskInventory), "activeLoads"),
            (typeof(VbaFileSystemProjectDiskInventory), "publicationGenerations"),
            (typeof(VbaProjectSourceDocumentCache), "states")
        })
        {
            var field = ownerType.GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.Equal(
                identityType,
                field.FieldType.GetGenericArguments()[0]);
        }
    }

    [Fact]
    public void Disk_reconciliation_source_facts_carry_document_identity()
    {
        foreach (var factType in new[]
        {
            typeof(VbaProjectDiskSource),
            typeof(VbaProjectDiskSourceFailure),
            typeof(VbaProjectDiskKnownSource),
            typeof(VbaProjectDiskManifest)
        })
        {
            Assert.Equal(
                typeof(VbaDocumentIdentity),
                factType.GetProperty("DocumentIdentity")?.PropertyType);
        }
    }

    [Fact]
    public void Disk_inventory_boundary_uses_typed_document_collections()
    {
        var identityType = typeof(VbaDocumentIdentity);
        var inventoryType = typeof(IVbaProjectDiskInventory);
        Assert.Equal(
            identityType,
            inventoryType.GetMethod("ContainsSource")!
                .GetParameters()[1].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<VbaDocumentIdentity, bool>),
            inventoryType.GetMethod("ContainsSource")!
                .GetParameters()[2].ParameterType);

        var captureColdSources = inventoryType.GetMethod(
            "CaptureColdSources")!;
        Assert.Equal(
            typeof(IReadOnlyCollection<VbaDocumentIdentity>),
            captureColdSources.GetParameters()[1].ParameterType);
        Assert.Equal(
            typeof(IReadOnlySet<VbaDocumentIdentity>),
            captureColdSources.GetParameters()[2].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<VbaDocumentIdentity, bool>),
            captureColdSources.GetParameters()[3].ParameterType);

        var captureWatchedSource = inventoryType.GetMethod(
            "CaptureWatchedSource")!;
        Assert.Equal(
            identityType,
            captureWatchedSource.GetParameters()[1].ParameterType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<VbaDocumentIdentity, bool>),
            captureWatchedSource.GetParameters()[2].ParameterType);

        Assert.Equal(
            typeof(IReadOnlyList<VbaDocumentIdentity>),
            typeof(VbaProjectDiskObservationRequest)
                .GetProperty("OpenSourceIdentities")?.PropertyType);
    }

    [Fact]
    public void Cold_capture_reuses_decoded_text_when_metadata_is_unchanged()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        var second = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(1, fileSystem.SourceReadCount);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
    }

    [Fact]
    public void Cold_capture_uses_acp_for_ambiguous_bomless_utf8_bytes()
    {
        const string sourceText =
            "Attribute VB_Name = \"Module1\"\n"
            + "Public Sub Café()\n"
            + "End Sub\n";
        var fileSystem = new MutableSourceFileSystem(sourceText);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var source = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(sourceText.Replace("Café", "CafÃ©"), source.Text);
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16-le")]
    [InlineData("utf-16-be")]
    public void Cold_capture_decodes_recognized_unicode_bom_strictly(
        string encodingName)
    {
        const string sourceText =
            "Attribute VB_Name = \"Module1\"\n"
            + "Public Sub 日本語()\n"
            + "End Sub\n";
        Encoding encoding = encodingName switch
        {
            "utf-8" => new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: true,
                throwOnInvalidBytes: true),
            "utf-16-le" => new UnicodeEncoding(
                bigEndian: false,
                byteOrderMark: true,
                throwOnInvalidBytes: true),
            "utf-16-be" => new UnicodeEncoding(
                bigEndian: true,
                byteOrderMark: true,
                throwOnInvalidBytes: true),
            _ => throw new InvalidOperationException(
                $"Unknown test encoding: {encodingName}")
        };
        var fileSystem = new MutableSourceFileSystem(
            [.. encoding.GetPreamble(), .. encoding.GetBytes(sourceText)]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var source = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(sourceText, source.Text);
    }

    [Fact]
    public void Cold_capture_uses_injected_windows_acp_without_utf8_probing()
    {
        const string sourceText =
            "Attribute VB_Name = \"Module1\"\n"
            + "Public Sub Café()\n"
            + "End Sub\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fileSystem = new MutableSourceFileSystem(
            Encoding.GetEncoding(1252).GetBytes(sourceText));
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var source = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(sourceText, source.Text);
    }

    [Fact]
    public void Cold_capture_reports_invalid_closed_source_without_substituting_text()
    {
        var fileSystem = new MutableSourceFileSystem([0xC3, 0x28]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        var failure = Assert.Single(capture.Failures);
        Assert.Equal(new Uri(fileSystem.Path).AbsoluteUri, failure.Uri);
        Assert.Contains(fileSystem.Path, failure.DiagnosticMessage);
        Assert.Contains("UTF-8", failure.DiagnosticMessage);
    }

    [Fact]
    public void Cold_capture_does_not_read_or_decode_an_open_source_candidate()
    {
        var fileSystem = new MutableSourceFileSystem([0xC3, 0x28]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001));
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(fileSystem.Path)!);
        var sourceIdentity = IdentifyLocalDocument(fileSystem.Path);

        var capture = inventory.CaptureColdSources(
            resolution,
            candidateSourceIdentities: [sourceIdentity],
            excludedSourceIdentities:
                new HashSet<VbaDocumentIdentity>(),
            manifestBarrierOverrides:
                new Dictionary<VbaDocumentIdentity, bool>(),
            CancellationToken.None);

        Assert.Empty(capture.Sources);
        Assert.Empty(capture.Failures);
        Assert.Equal(0, fileSystem.SourceReadCount);
        Assert.Contains(
            IdentifyLocalDocument(fileSystem.Path),
            capture.OwnedCandidateSourceIdentities);
    }

    [Fact]
    public void Acp_65001_has_no_second_legacy_decoding_path()
    {
        var fileSystem = new MutableSourceFileSystem([0xE9]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 65001));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Fact]
    public void Non_windows_policy_has_no_implicit_legacy_fallback()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fileSystem = new MutableSourceFileSystem(
            Encoding.GetEncoding(932).GetBytes("Public Sub 日本語()\nEnd Sub\n"));
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 932));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Public Sub Ascii()\nEnd Sub\n")]
    [InlineData("Public Sub 日本語()\nEnd Sub\n")]
    public void Non_windows_policy_rejects_all_bomless_source(string sourceText)
    {
        var fileSystem = new MutableSourceFileSystem(sourceText);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        var failure = Assert.Single(capture.Failures);
        Assert.Contains("BOM", failure.DiagnosticMessage);
        Assert.Contains(fileSystem.Path, failure.DiagnosticMessage);
    }

    [Theory]
    [InlineData(932)]
    [InlineData(1252)]
    [InlineData(65001)]
    public void Windows_acp_authority_accepts_empty_bomless_source(int activeCodePage)
    {
        var fileSystem = new MutableSourceFileSystem("");
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: activeCodePage));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Failures);
        Assert.Equal("", Assert.Single(capture.Sources).Text);
    }

    [Theory]
    [InlineData("utf-8", "")]
    [InlineData("utf-8", "' 日本語 😀\n")]
    [InlineData("utf-16-le", "")]
    [InlineData("utf-16-le", "' 日本語 😀\n")]
    [InlineData("utf-16-be", "")]
    [InlineData("utf-16-be", "' 日本語 😀\n")]
    public void Non_windows_policy_accepts_supported_bom_source(string encodingName, string sourceText)
    {
        Encoding encoding = encodingName switch
        {
            "utf-8" => new UTF8Encoding(true, true),
            "utf-16-le" => new UnicodeEncoding(false, true, true),
            "utf-16-be" => new UnicodeEncoding(true, true, true),
            _ => throw new InvalidOperationException($"Unknown encoding: {encodingName}")
        };
        var fileSystem = new MutableSourceFileSystem(
            [.. encoding.GetPreamble(), .. encoding.GetBytes(sourceText)]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 1252));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Failures);
        Assert.Equal(sourceText, Assert.Single(capture.Sources).Text);
    }

    [Fact]
    public void Cp932_source_decodes_only_when_cp932_is_the_injected_acp()
    {
        const string sourceText = "Public Sub 日本語()\nEnd Sub\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sourceBytes = Encoding.GetEncoding(932).GetBytes(sourceText);
        var cp932FileSystem = new MutableSourceFileSystem(sourceBytes);
        var unicodeOnlyFileSystem = new MutableSourceFileSystem(sourceBytes);

        var cp932Capture = CaptureColdSources(
            new VbaFileSystemProjectDiskInventory(
                cp932FileSystem,
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: true,
                    activeCodePage: 932)),
            cp932FileSystem.Path);
        var unicodeOnlyCapture = CaptureColdSources(
            new VbaFileSystemProjectDiskInventory(
                unicodeOnlyFileSystem,
                new DiskSourceDecoding(
                    hasWindowsAcpAuthority: false,
                    activeCodePage: 932)),
            unicodeOnlyFileSystem.Path);

        Assert.Equal(sourceText, Assert.Single(cp932Capture.Sources).Text);
        Assert.Empty(unicodeOnlyCapture.Sources);
        Assert.Single(unicodeOnlyCapture.Failures);
    }

    [Fact]
    public void Invalid_bom_selected_unicode_never_falls_back_to_the_acp()
    {
        var fileSystem = new MutableSourceFileSystem(
            [0xFF, 0xFE, 0x00, 0xD8]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Theory]
    [InlineData("2B2F7638")]
    [InlineData("2B2F7639")]
    [InlineData("2B2F762B")]
    [InlineData("2B2F762F")]
    [InlineData("EF")]
    [InlineData("EFBB")]
    [InlineData("FF")]
    [InlineData("FE")]
    [InlineData("00")]
    [InlineData("0000")]
    [InlineData("0000FE")]
    [InlineData("2B")]
    [InlineData("2B2F")]
    [InlineData("2B2F76")]
    public void Unsupported_or_truncated_bom_never_becomes_acp_text(string bytesHex)
    {
        var fileSystem = new MutableSourceFileSystem(Convert.FromHexString(bytesHex));
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Theory]
    [InlineData("utf-32-le")]
    [InlineData("utf-32-be")]
    public void Unsupported_utf32_bom_never_falls_through_to_utf16_or_acp(
        string encodingName)
    {
        byte[] sourceBytes = encodingName switch
        {
            "utf-32-le" =>
                [0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00],
            "utf-32-be" =>
                [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41],
            _ => throw new InvalidOperationException(
                $"Unknown test encoding: {encodingName}")
        };
        var fileSystem = new MutableSourceFileSystem(sourceBytes);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Fact]
    public void Invalid_injected_acp_sequence_never_uses_replacement_text()
    {
        var fileSystem = new MutableSourceFileSystem([0x81]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 932));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
    }

    [Fact]
    public void Noncanonical_acp_bytes_never_become_decoded_source()
    {
        var fileSystem = new MutableSourceFileSystem([0x87, 0x90]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 932));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Single(capture.Failures);
        Assert.Equal(0, inventory.Count);
    }

    public static IEnumerable<object[]> SourceEncodingCases()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "vba-source-encoding",
            "cases.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [item.GetProperty("id").GetString()!, item.GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(SourceEncodingCases))]
    public void Closed_source_conforms_to_neutral_byte_decoding_corpus(string id, string caseJson)
    {
        using var document = JsonDocument.Parse(caseJson);
        var item = document.RootElement;
        var bytes = Convert.FromBase64String(item.GetProperty("bytesBase64").GetString()!);
        var fileSystem = new MutableSourceFileSystem(bytes);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: item.GetProperty("activeCodePage").GetInt32()));

        var capture = CaptureColdSources(inventory, fileSystem.Path);

        if (item.TryGetProperty("expectedFailure", out var failure) && failure.GetBoolean())
        {
            Assert.Empty(capture.Sources);
            Assert.Single(capture.Failures);
            return;
        }

        // VBE projection failures do not reject valid language-server Unicode.
        Assert.True(capture.Failures.Count == 0, $"Encoding case '{id}' must decode without source failures.");
        Assert.Equal(item.GetProperty("expectedText").GetString(), Assert.Single(capture.Sources).Text);
    }

    [Fact]
    public void Content_identity_uses_decoded_text_instead_of_source_bytes()
    {
        const string sourceText = "Public Sub 日本語()\nEnd Sub\n";
        var fileSystem = new MutableSourceFileSystem(sourceText);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 65001));
        var utf8 = CaptureSingleColdSource(inventory, fileSystem.Path);
        var utf16 = new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true);

        fileSystem.ReplaceSource(
            [.. utf16.GetPreamble(), .. utf16.GetBytes(sourceText)]);
        inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
        var utf16Le = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(utf8.ContentIdentity, utf16Le.ContentIdentity);
    }

    [Fact]
    public void Invalidation_forces_cold_capture_to_read_source_again()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);

        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);
        inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
        var second = CaptureSingleColdSource(inventory, fileSystem.Path);

        Assert.Equal(2, fileSystem.SourceReadCount);
        Assert.Equal(first.DocumentIdentity, second.DocumentIdentity);
        Assert.NotEqual(first.ContentIdentity, second.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, second.Text);
    }

    [Fact]
    public async Task Reconciliation_reads_and_detects_changed_content_with_unchanged_metadata()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);

        var scan = await inventory.ObserveReconciliationAsync(
            CreateObservationRequest(first),
            CancellationToken.None);
        var observed = Assert.Single(scan.Sources);

        Assert.Equal(2, fileSystem.SourceReadCount);
        Assert.Equal(first.Metadata, observed.Metadata);
        Assert.NotEqual(
            first.ContentIdentity,
            observed.ContentIdentity);
        Assert.Equal(ChangedSameLengthText, observed.Text);
    }

    [Fact]
    public async Task Reconciliation_applies_the_shared_decoder_with_unchanged_metadata()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(1252);
        const string initialText = "Public Sub Café()\nEnd Sub\n";
        const string changedText = "Public Sub Cafè()\nEnd Sub\n";
        var fileSystem = new MutableSourceFileSystem(
            encoding.GetBytes(initialText));
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 1252));
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        fileSystem.ReplaceSource(
            encoding.GetBytes(changedText),
            advanceMetadata: false);

        var scan = await inventory.ObserveReconciliationAsync(
            CreateObservationRequest(first),
            CancellationToken.None);

        Assert.Equal(changedText, Assert.Single(scan.Sources).Text);
        Assert.Equal(2, fileSystem.SourceReadCount);
    }

    [Fact]
    public async Task Reconciliation_reports_invalid_source_without_reusing_cached_text()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 65001));
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);
        fileSystem.ReplaceSource(
            [0xC3, 0x28],
            advanceMetadata: false);

        var scan = await inventory.ObserveReconciliationAsync(
            CreateObservationRequest(first),
            CancellationToken.None);

        Assert.Empty(scan.Sources);
        Assert.Single(scan.Failures);
        Assert.Equal(0, inventory.Count);
    }

    [Fact]
    public async Task Reconciliation_does_not_read_or_decode_an_open_source()
    {
        var fileSystem = new MutableSourceFileSystem([0xC3, 0x28]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001));
        var request = new VbaProjectDiskObservationRequest(
            new VbaProjectDiskProjectScope(
                IdentifyAuthority(new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    Path.GetDirectoryName(fileSystem.Path)!)),
                VbaProjectResolutionKind.AdHoc,
                Path.GetDirectoryName(fileSystem.Path)!),
            manifestCandidates: [],
            barrierOverrides: [],
            observedManifestBarrierIdentities: [])
        {
            OpenSourceIdentities = [IdentifyLocalDocument(fileSystem.Path)]
        };

        var scan = await inventory.ObserveReconciliationAsync(
            request,
            CancellationToken.None);

        Assert.Empty(scan.Sources);
        Assert.Empty(scan.Failures);
        Assert.Equal(0, fileSystem.SourceReadCount);
    }

    [Fact]
    public void Content_identity_is_stable_for_equal_text_and_changes_for_changed_text()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var first = CaptureSingleColdSource(inventory, fileSystem.Path);

        inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
        var equalText = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);
        fileSystem.ReplaceSource(
            ChangedSameLengthText,
            advanceMetadata: false);
        inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
        var changedText = CaptureSingleColdSource(
            inventory,
            fileSystem.Path);

        Assert.Equal(
            first.ContentIdentity,
            equalText.ContentIdentity);
        Assert.NotEqual(
            first.ContentIdentity,
            changedText.ContentIdentity);
    }

    [Fact]
    public async Task Invalidated_older_load_cannot_overwrite_a_newer_cached_source()
    {
        var fileSystem = new BlockingSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var olderLoad = Task.Factory.StartNew(
            () => CaptureSingleColdSource(
                inventory,
                fileSystem.Path),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await fileSystem.FirstReadStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            fileSystem.ReplaceSource(ChangedSameLengthText);
            inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
            var newer = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            fileSystem.ReleaseFirstRead();
            _ = await olderLoad.WaitAsync(TimeSpan.FromSeconds(10));
            var retained = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            Assert.Equal(
                newer.ContentIdentity,
                retained.ContentIdentity);
            Assert.Equal(ChangedSameLengthText, retained.Text);
            Assert.Equal(1, inventory.Count);
        }
        finally
        {
            fileSystem.ReleaseFirstRead();
        }
    }

    [Fact]
    public async Task Older_parallel_load_cannot_overwrite_a_newer_cached_source_when_metadata_is_unchanged()
    {
        var fileSystem = new BlockingSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var olderLoad = Task.Factory.StartNew(
            () => CaptureSingleColdSource(
                inventory,
                fileSystem.Path),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await fileSystem.FirstReadStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));

            fileSystem.ReplaceSource(
                ChangedSameLengthText,
                advanceMetadata: false);
            var newer = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            fileSystem.ReleaseFirstRead();
            _ = await olderLoad.WaitAsync(TimeSpan.FromSeconds(10));
            var retained = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            Assert.Equal(
                newer.ContentIdentity,
                retained.ContentIdentity);
            Assert.Equal(ChangedSameLengthText, retained.Text);
            Assert.Equal(1, inventory.Count);
        }
        finally
        {
            fileSystem.ReleaseFirstRead();
        }
    }

    [Fact]
    public async Task Invalidated_older_decode_failure_cannot_remove_newer_cached_source()
    {
        var fileSystem = new BlockingSourceFileSystem([0xC3, 0x28]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: true,
                activeCodePage: 65001));

        var olderLoad = Task.Factory.StartNew(
            () => CaptureColdSources(inventory, fileSystem.Path),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            await fileSystem.FirstReadStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            fileSystem.ReplaceSource(
                "OK",
                advanceMetadata: false);
            inventory.InvalidateSource(IdentifyLocalDocument(fileSystem.Path));
            var newer = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            fileSystem.ReleaseFirstRead();
            var older = await olderLoad.WaitAsync(
                TimeSpan.FromSeconds(10));
            var retained = CaptureSingleColdSource(
                inventory,
                fileSystem.Path);

            Assert.Single(older.Failures);
            Assert.Equal(
                newer.ContentIdentity,
                retained.ContentIdentity);
            Assert.Equal("OK", retained.Text);
            Assert.Equal(2, fileSystem.SourceReadCount);
            Assert.Equal(1, inventory.Count);
        }
        finally
        {
            fileSystem.ReleaseFirstRead();
        }
    }

    [Fact]
    public void Cold_capture_ignores_source_deleted_between_metadata_and_read()
    {
        var fileSystem = new DeletedDuringReadFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);

        var capture = CaptureColdSources(
            inventory,
            fileSystem.Path);

        Assert.Empty(capture.Sources);
        Assert.Equal(0, inventory.Count);
    }

    [Fact]
    public void Watched_capture_reads_one_owned_source_without_enumerating_the_project()
    {
        var fileSystem = new MutableSourceFileSystem(InitialText);
        var inventory = new VbaFileSystemProjectDiskInventory(fileSystem);
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(fileSystem.Path)!);

        var source = inventory.CaptureWatchedSource(
            resolution,
            IdentifyLocalDocument(fileSystem.Path),
            new Dictionary<VbaDocumentIdentity, bool>(),
            out var failure,
            CancellationToken.None);

        Assert.NotNull(source);
        Assert.Null(failure);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(1, fileSystem.SourceReadCount);
        Assert.Equal(InitialText, source.Text);
    }

    [Fact]
    public void Watched_capture_reports_invalid_source_without_returning_text()
    {
        var fileSystem = new MutableSourceFileSystem([0xC3, 0x28]);
        var inventory = new VbaFileSystemProjectDiskInventory(
            fileSystem,
            new DiskSourceDecoding(
                hasWindowsAcpAuthority: false,
                activeCodePage: 65001));
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(fileSystem.Path)!);

        var source = inventory.CaptureWatchedSource(
            resolution,
            IdentifyLocalDocument(fileSystem.Path),
            new Dictionary<VbaDocumentIdentity, bool>(),
            out var failure,
            CancellationToken.None);

        Assert.Null(source);
        Assert.NotNull(failure);
        Assert.Equal(new Uri(fileSystem.Path).AbsoluteUri, failure.Uri);
    }

    private static VbaDocumentIdentity IdentifyLocalDocument(string path)
    {
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyLocalDocumentPath(
                path,
                out var identity));
        return identity;
    }

    private static VbaProjectAuthorityIdentity IdentifyAuthority(
        VbaProjectResolution resolution)
    {
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                resolution,
                out var identity));
        return identity;
    }

    private static VbaProjectDiskSource CaptureSingleColdSource(
        VbaFileSystemProjectDiskInventory inventory,
        string sourcePath)
        => Assert.Single(
            CaptureColdSources(
                inventory,
                sourcePath).Sources);

    private static VbaProjectDiskColdSourceCapture CaptureColdSources(
        VbaFileSystemProjectDiskInventory inventory,
        string sourcePath)
    {
        var resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            Path.GetDirectoryName(sourcePath)!);
        var capture = inventory.CaptureColdSources(
            resolution,
            candidateSourceIdentities: [],
            excludedSourceIdentities:
                new HashSet<VbaDocumentIdentity>(),
            manifestBarrierOverrides:
                new Dictionary<VbaDocumentIdentity, bool>(),
            CancellationToken.None);
        return capture;
    }

    private static VbaProjectDiskObservationRequest
        CreateObservationRequest(VbaProjectDiskSource source)
        => new(
            new VbaProjectDiskProjectScope(
                IdentifyAuthority(new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    Path.GetDirectoryName(source.FullPath)!)),
                VbaProjectResolutionKind.AdHoc,
                Path.GetDirectoryName(source.FullPath)!),
            manifestCandidates: [],
            barrierOverrides: [],
            observedManifestBarrierIdentities: []);

    private class MutableSourceFileSystem : IVbaProjectFileSystem
    {
        private readonly object gate = new();
        private byte[] sourceBytes;
        private VbaProjectSourceFileMetadata metadata;

        public MutableSourceFileSystem(string source)
            : this(Encoding.UTF8.GetBytes(source))
        {
        }

        public MutableSourceFileSystem(byte[] source)
        {
            Path = System.IO.Path.GetFullPath("Module1.bas");
            sourceBytes = source.ToArray();
            metadata = new VbaProjectSourceFileMetadata(
                sourceBytes.LongLength,
                LastWriteTimeUtcTicks: 1);
        }

        public string Path { get; }

        public int SourceReadCount { get; protected set; }

        public int EnumerationCount { get; private set; }

        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            EnumerationCount++;
            return searchPattern.Equals(
                "*.bas",
                StringComparison.OrdinalIgnoreCase)
                ? [Path]
                : [];
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata captured)
        {
            lock (gate)
            {
                captured = metadata;
                return true;
            }
        }

        public string ReadManifestText(string path) => "";

        public virtual byte[] ReadSourceBytes(string path)
        {
            lock (gate)
            {
                SourceReadCount++;
                return sourceBytes.ToArray();
            }
        }

        public void ReplaceSource(
            string source,
            bool advanceMetadata = true)
            => ReplaceSource(
                Encoding.UTF8.GetBytes(source),
                advanceMetadata);

        public void ReplaceSource(
            byte[] source,
            bool advanceMetadata = true)
        {
            lock (gate)
            {
                sourceBytes = source.ToArray();
                metadata = new VbaProjectSourceFileMetadata(
                    sourceBytes.LongLength,
                    advanceMetadata
                        ? metadata.LastWriteTimeUtcTicks + 1
                        : metadata.LastWriteTimeUtcTicks);
            }
        }

        protected byte[] CaptureSourceBytes()
        {
            lock (gate)
            {
                SourceReadCount++;
                return sourceBytes.ToArray();
            }
        }
    }

    private sealed class BlockingSourceFileSystem
        : MutableSourceFileSystem
    {
        private readonly ManualResetEventSlim releaseFirstRead =
            new(false);
        private int readCount;

        public BlockingSourceFileSystem(string source)
            : base(source)
        {
        }

        public BlockingSourceFileSystem(byte[] source)
            : base(source)
        {
        }

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override byte[] ReadSourceBytes(string path)
        {
            var captured = CaptureSourceBytes();
            if (Interlocked.Increment(ref readCount) == 1)
            {
                FirstReadStarted.TrySetResult();
                releaseFirstRead.Wait();
            }

            return captured;
        }

        public void ReleaseFirstRead()
            => releaseFirstRead.Set();
    }

    private sealed class DeletedDuringReadFileSystem
        : MutableSourceFileSystem
    {
        public DeletedDuringReadFileSystem(string source)
            : base(source)
        {
        }

        public override byte[] ReadSourceBytes(string path)
            => throw new FileNotFoundException(
                "Source was deleted during capture.",
                path);
    }
}
