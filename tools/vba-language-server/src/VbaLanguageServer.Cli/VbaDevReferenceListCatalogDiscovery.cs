using System.Text.Json;
using VbaLanguageServer.Processes;

namespace VbaLanguageServer.SourceModel;

internal sealed class VbaDevReferenceListCatalogDiscoveryFactory
    : IVbaProjectReferenceCatalogDiscovery,
      IVbaProjectReferenceCatalogDiscoveryBatchFactory,
      IVbaProjectReferenceCatalogContextDiscoveryFactory,
      IVbaProjectReferenceCatalogCancellationCleanup
{
    private readonly IVbaProjectReferenceCatalogDiscovery registryDiscovery;
    private readonly VbaDevProcessInvocationRunner processRunner;

    TimeSpan IVbaProjectReferenceCatalogCancellationCleanup.CancellationCleanupTimeout =>
        VbaDevProcessInvocation.DefaultCancellationCleanupTimeout;

    public VbaDevReferenceListCatalogDiscoveryFactory(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery,
        string executablePath)
        : this(
            registryDiscovery,
            executablePath,
            new VbaDevProcessInvocation(executablePath).RunAsync)
    {
    }

    internal VbaDevReferenceListCatalogDiscoveryFactory(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery,
        string executablePath,
        VbaDevProcessInvocationRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(registryDiscovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(processRunner);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "The pinned vba-dev executable path must be absolute.",
                nameof(executablePath));
        }

        this.registryDiscovery = registryDiscovery;
        this.processRunner = processRunner;
    }

    public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
        => registryDiscovery.DiscoverAsync(referenceName, cancellationToken);

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogDiscoveryBatchFactory.CreateBatchDiscovery()
        => CreateRegistryBatchDiscovery();

    IVbaProjectReferenceCatalogDiscovery
        IVbaProjectReferenceCatalogContextDiscoveryFactory.CreateContextDiscovery(
            VbaProjectReferenceCatalogRefreshContext context)
        => new VbaDevReferenceListCatalogDiscovery(
            CreateRegistryBatchDiscovery(),
            context,
            processRunner);

    private IVbaProjectReferenceCatalogDiscovery CreateRegistryBatchDiscovery()
        => registryDiscovery is IVbaProjectReferenceCatalogDiscoveryBatchFactory batchFactory
            ? batchFactory.CreateBatchDiscovery()
            : registryDiscovery;
}

internal sealed class VbaDevReferenceListCatalogDiscovery
    : IVbaProjectReferenceCatalogDiscovery
{
    private readonly IVbaProjectReferenceCatalogDiscovery registryDiscovery;
    private readonly VbaProjectReferenceCatalogRefreshContext context;
    private readonly VbaDevProcessInvocationRunner processRunner;
    private readonly object invocationGate = new();
    private Task<VbaDevReferenceListInvocationResult>? invocation;

    public VbaDevReferenceListCatalogDiscovery(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery,
        VbaProjectReferenceCatalogRefreshContext context,
        VbaDevProcessInvocationRunner processRunner)
    {
        this.registryDiscovery = registryDiscovery;
        this.context = context;
        this.processRunner = processRunner;
    }

    public async Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
        string referenceName,
        CancellationToken cancellationToken = default)
    {
        var registryResult = await registryDiscovery
            .DiscoverAsync(referenceName, cancellationToken)
            .ConfigureAwait(false);
        if (!registryResult.RequiresExternalIdentityResolution)
        {
            return registryResult;
        }

        var invocationResult = await GetInvocationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!invocationResult.IsTrusted)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                invocationResult.ErrorMessage
                    ?? "The vba-dev reference-list invocation was not trusted.");
        }

        var entry = invocationResult.Entries.FirstOrDefault(candidate =>
            VbaProjectReferenceName.AreEquivalent(candidate.Name, referenceName));
        if (entry?.Identity is null)
        {
            if (entry?.Status == "ambiguous")
            {
                return VbaProjectReferenceCatalogDiscoveryResult.Ambiguous(
                    referenceName,
                    registryResult.Identities);
            }

            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                entry?.Message
                    ?? "VbaDev did not return a resolved identity for this configured reference.");
        }

        if (registryDiscovery is not IVbaProjectReferenceCatalogIdentityDiscovery identityDiscovery)
        {
            return VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "The neutral registry discovery cannot load an externally resolved TypeLib identity.");
        }

        return await identityDiscovery.DiscoverIdentityAsync(
                referenceName,
                entry.Identity,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<VbaDevReferenceListInvocationResult> GetInvocationAsync(
        CancellationToken cancellationToken)
    {
        lock (invocationGate)
        {
            invocation ??= InvokeAsync(cancellationToken);
            return invocation;
        }
    }

    private async Task<VbaDevReferenceListInvocationResult> InvokeAsync(
        CancellationToken cancellationToken)
    {
        var projectPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(context.ProjectPath));
        var arguments = new[]
        {
            "reference",
            "list",
            "--project",
            projectPath,
            "--document",
            context.DocumentName,
            "--format",
            "json"
        };

        try
        {
            var processResult = await processRunner(
                    arguments,
                    cancellationToken)
                .ConfigureAwait(false);
            return VbaDevReferenceListContract.Parse(
                processResult,
                projectPath,
                context.DocumentName,
                context.Selection.References);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return VbaDevReferenceListInvocationResult.Untrusted(
                $"VbaDev reference-list invocation failed: {exception.Message}");
        }
    }
}

internal sealed record VbaDevReferenceListEntry(
    string Name,
    string Status,
    VbaProjectReferenceCatalogIdentityKey? Identity,
    string? Message = null);

internal sealed record VbaDevReferenceListInvocationResult(
    bool IsTrusted,
    IReadOnlyList<VbaDevReferenceListEntry> Entries,
    string? ErrorMessage)
{
    public static VbaDevReferenceListInvocationResult Trusted(
        IReadOnlyList<VbaDevReferenceListEntry> entries)
        => new(true, entries, null);

    public static VbaDevReferenceListInvocationResult Untrusted(string errorMessage)
        => new(
            false,
            [],
            errorMessage);
}

internal static class VbaDevReferenceListContract
{
    private const string RequiredSchemaVersion = "1.0";

    private static readonly IReadOnlySet<string> RootProperties = new HashSet<string>(
        [
            "schemaVersion",
            "scope",
            "project",
            "document",
            "mode",
            "complete",
            "warnings",
            "diagnostics",
            "references"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> EntryProperties = new HashSet<string>(
        ["name", "status", "identity", "reasonCode", "candidates", "message"],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> IdentityProperties = new HashSet<string>(
        ["guid", "major", "minor"],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> MessageProperties = new HashSet<string>(
        ["code", "message"],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> UnavailableReasonCodes = new HashSet<string>(
        ["notRegistered", "noUsableIdentity"],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> UnverifiedReasonCodes = new HashSet<string>(
        [
            "excelVbeFailure",
            "probeTimeout",
            "identityReadFailure",
            "cleanupFailure",
            "probeAborted",
            "cancelled"
        ],
        StringComparer.Ordinal);

    public static VbaDevReferenceListInvocationResult Parse(
        VbaDevProcessInvocationResult processResult,
        string expectedProjectPath,
        string expectedDocumentName,
        IReadOnlyList<VbaProjectReference> expectedReferences)
    {
        try
        {
            using var document = JsonDocument.Parse(processResult.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || HasDuplicateKnownProperties(root, RootProperties)
                || !HasString(root, "schemaVersion", RequiredSchemaVersion, StringComparison.Ordinal)
                || !HasString(root, "scope", "project", StringComparison.Ordinal)
                || !HasString(root, "project", expectedProjectPath, StringComparison.OrdinalIgnoreCase)
                || !HasString(root, "document", expectedDocumentName, StringComparison.Ordinal)
                || !HasString(root, "mode", "configured", StringComparison.Ordinal)
                || !TryGetBoolean(root, "complete", out var complete)
                || !complete
                || !TryValidateMessages(root, "warnings", required: true, out _)
                || !TryValidateMessages(root, "diagnostics", required: false, out var diagnosticCount)
                || diagnosticCount != 0
                || !root.TryGetProperty("references", out var references)
                || references.ValueKind != JsonValueKind.Array
                || references.GetArrayLength() != expectedReferences.Count)
            {
                return VbaDevReferenceListInvocationResult.Untrusted(
                    "VbaDev returned an incomplete or context-mismatched reference-list response.");
            }

            var entries = new List<VbaDevReferenceListEntry>(expectedReferences.Count);
            var containsUnverifiedEntry = false;
            for (var index = 0; index < expectedReferences.Count; index++)
            {
                var element = references[index];
                var expectedName = expectedReferences[index].Name;
                if (element.ValueKind != JsonValueKind.Object
                    || HasDuplicateKnownProperties(element, EntryProperties)
                    || !HasString(element, "name", expectedName, StringComparison.OrdinalIgnoreCase)
                    || !TryGetRequiredString(element, "status", out var status)
                    || !TryParseEntry(
                        element,
                        expectedName,
                        status,
                        out var entry,
                        out var isUnverified))
                {
                    return VbaDevReferenceListInvocationResult.Untrusted(
                        "VbaDev returned an invalid configured reference entry.");
                }

                entries.Add(entry);
                containsUnverifiedEntry |= isUnverified;
            }

            return containsUnverifiedEntry
                ? VbaDevReferenceListInvocationResult.Untrusted(
                    "VbaDev returned an unverified configured reference entry.")
                : VbaDevReferenceListInvocationResult.Trusted(entries);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return VbaDevReferenceListInvocationResult.Untrusted(
                $"VbaDev returned malformed reference-list JSON: {exception.Message}");
        }
    }

    private static bool TryParseEntry(
        JsonElement element,
        string expectedName,
        string status,
        out VbaDevReferenceListEntry entry,
        out bool isUnverified)
    {
        entry = default!;
        isUnverified = false;
        switch (status)
        {
            case "resolved":
                if (!element.TryGetProperty("identity", out var identityElement)
                    || !TryParseIdentity(identityElement, out var identity)
                    || HasAnyProperty(element, "reasonCode", "candidates", "message"))
                {
                    return false;
                }

                entry = new VbaDevReferenceListEntry(expectedName, status, identity);
                return true;

            case "ambiguous":
                if (!HasString(
                        element,
                        "reasonCode",
                        "multipleUsableIdentities",
                        StringComparison.Ordinal)
                    || !TryParseCandidates(element, minimumCount: 2, out _)
                    || !TryGetNonBlankString(element, "message", out var ambiguousMessage)
                    || element.TryGetProperty("identity", out _))
                {
                    return false;
                }

                entry = new VbaDevReferenceListEntry(
                    expectedName,
                    status,
                    null,
                    ambiguousMessage);
                return true;

            case "unavailable":
                if (!TryGetRequiredString(element, "reasonCode", out var unavailableReason)
                    || !UnavailableReasonCodes.Contains(unavailableReason)
                    || !TryParseCandidates(element, minimumCount: 0, out var unavailableCandidates)
                    || unavailableReason == "notRegistered" && unavailableCandidates.Count != 0
                    || !TryGetNonBlankString(element, "message", out var unavailableMessage)
                    || element.TryGetProperty("identity", out _))
                {
                    return false;
                }

                entry = new VbaDevReferenceListEntry(
                    expectedName,
                    status,
                    null,
                    unavailableMessage);
                return true;

            case "unverified":
                if (!TryGetRequiredString(element, "reasonCode", out var unverifiedReason)
                    || !UnverifiedReasonCodes.Contains(unverifiedReason)
                    || !TryParseCandidates(element, minimumCount: 0, out _)
                    || !TryGetNonBlankString(element, "message", out var unverifiedMessage)
                    || element.TryGetProperty("identity", out _))
                {
                    return false;
                }

                entry = new VbaDevReferenceListEntry(
                    expectedName,
                    status,
                    null,
                    unverifiedMessage);
                isUnverified = true;
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseCandidates(
        JsonElement element,
        int minimumCount,
        out IReadOnlyList<VbaProjectReferenceCatalogIdentityKey> candidates)
    {
        candidates = [];
        if (!element.TryGetProperty("candidates", out var candidateArray)
            || candidateArray.ValueKind != JsonValueKind.Array
            || candidateArray.GetArrayLength() < minimumCount)
        {
            return false;
        }

        var parsed = new List<VbaProjectReferenceCatalogIdentityKey>(
            candidateArray.GetArrayLength());
        VbaProjectReferenceCatalogIdentityKey? previous = null;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidateElement in candidateArray.EnumerateArray())
        {
            if (!TryParseIdentity(candidateElement, out var candidate))
            {
                return false;
            }

            var key = string.Join("\u001f", candidate.Guid, candidate.MajorVersion, candidate.MinorVersion);
            if (!distinct.Add(key)
                || previous is not null && CompareIdentity(previous, candidate) >= 0)
            {
                return false;
            }

            parsed.Add(candidate);
            previous = candidate;
        }

        candidates = parsed;
        return true;
    }

    private static int CompareIdentity(
        VbaProjectReferenceCatalogIdentityKey left,
        VbaProjectReferenceCatalogIdentityKey right)
    {
        var guidComparison = StringComparer.Ordinal.Compare(left.Guid, right.Guid);
        if (guidComparison != 0)
        {
            return guidComparison;
        }

        var majorComparison = left.MajorVersion.CompareTo(right.MajorVersion);
        return majorComparison != 0
            ? majorComparison
            : left.MinorVersion.CompareTo(right.MinorVersion);
    }

    private static bool TryParseIdentity(
        JsonElement element,
        out VbaProjectReferenceCatalogIdentityKey identity)
    {
        identity = default!;
        if (element.ValueKind != JsonValueKind.Object
            || HasDuplicateKnownProperties(element, IdentityProperties)
            || !element.TryGetProperty("guid", out var guidElement)
            || guidElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(guidElement.GetString(), "D", out var guid)
            || !guid.ToString("D").Equals(guidElement.GetString(), StringComparison.Ordinal)
            || !TryGetUnsignedVersion(element, "major", out var major)
            || !TryGetUnsignedVersion(element, "minor", out var minor))
        {
            return false;
        }

        identity = new VbaProjectReferenceCatalogIdentityKey(
            guid.ToString("D"),
            major,
            minor);
        return true;
    }

    private static bool TryGetUnsignedVersion(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var rawValue = property.GetRawText();
        return rawValue.Length > 0
            && rawValue.All(character => character is >= '0' and <= '9')
            && property.TryGetInt32(out value)
            && value is >= ushort.MinValue and <= ushort.MaxValue;
    }

    private static bool HasString(
        JsonElement element,
        string propertyName,
        string expected,
        StringComparison comparison)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, comparison);

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = "";
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } stringValue)
        {
            return false;
        }

        value = stringValue;
        return true;
    }

    private static bool TryGetNonBlankString(
        JsonElement element,
        string propertyName,
        out string value)
        => TryGetRequiredString(element, propertyName, out value)
            && !string.IsNullOrWhiteSpace(value);

    private static bool HasAnyProperty(JsonElement element, params string[] propertyNames)
        => propertyNames.Any(propertyName => element.TryGetProperty(propertyName, out _));

    private static bool HasDuplicateKnownProperties(
        JsonElement element,
        IReadOnlySet<string> knownProperties)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (knownProperties.Contains(property.Name) && !encountered.Add(property.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBoolean(
        JsonElement element,
        string propertyName,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryValidateMessages(
        JsonElement root,
        string propertyName,
        bool required,
        out int count)
    {
        count = 0;
        if (!root.TryGetProperty(propertyName, out var messages))
        {
            return !required;
        }

        if (messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object
                || HasDuplicateKnownProperties(message, MessageProperties)
                || !message.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(code.GetString())
                || !message.TryGetProperty("message", out var text)
                || text.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text.GetString()))
            {
                return false;
            }

            count++;
        }

        return true;
    }
}
