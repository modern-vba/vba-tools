using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Lsp;

internal sealed record ReferenceCatalogRefreshLogMessage(int Type, string Text, string Key);

/// <summary>
/// Creates user-visible reference catalog refresh messages from refresh results.
/// </summary>
internal static class ReferenceCatalogRefreshOutcome
{
    public static ReferenceCatalogRefreshLogMessage CreateAvailabilityMessage(
        string? documentName,
        string referenceName,
        VbaProjectReferenceCatalogSource source)
        => new(
            3,
            $"Reference catalog availability: document '{documentName}' reference '{referenceName}' source={FormatCatalogSource(source)} outcome=available.",
            $"availability\u001f{documentName}\u001f{referenceName}\u001f{source}");

    public static ReferenceCatalogRefreshLogMessage CreateDiagnosticMessage(
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result)
    {
        var outcome = FormatRefreshOutcome(result);
        var warning = FormatRefreshWarning(result);
        var message =
            $"Reference catalog refresh diagnostics: document '{documentName}' reference '{result.ReferenceName}' source={FormatCatalogSource(result.Source)} outcome={outcome} phase={result.Phase} expensiveMetadata={FormatBoolean(result.ExpensiveMetadataRan)} elapsedMs={FormatElapsedMilliseconds(result.Elapsed)}{warning}.";
        var type = outcome is "failed" or "ambiguous" or "cache-read-warning" ? 2 : 3;
        var key = string.Join(
            "\u001f",
            "refresh",
            documentName,
            result.ReferenceName,
            FormatCatalogSource(result.Source),
            outcome,
            result.Phase,
            result.WarningMessage ?? result.DiscoveryResult.ErrorMessage ?? "");
        return new ReferenceCatalogRefreshLogMessage(type, message, key);
    }

    public static IReadOnlyList<ReferenceCatalogRefreshLogMessage> CreateDiscoveryMessages(
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result)
    {
        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.PersistentCacheReadWarning
            || result.Status == VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache
            || result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache
            || result.DiscoveryResult.IsFailure
            || result.DiscoveryResult.IsAmbiguous)
        {
            return [];
        }

        var messages = new List<ReferenceCatalogRefreshLogMessage>();
        var discovery = result.DiscoveryResult;
        var identity = discovery.Identities.SingleOrDefault();
        if (identity is not null)
        {
            messages.Add(new ReferenceCatalogRefreshLogMessage(
                3,
                $"Reference catalog discovery: document '{documentName}' reference '{result.ReferenceName}' resolved to TypeLib {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion} LCID {identity.Lcid} at {identity.Path}.",
                $"discovery\u001f{documentName}\u001f{result.ReferenceName}\u001f{identity.Guid}\u001f{identity.MajorVersion}\u001f{identity.MinorVersion}\u001f{identity.Lcid}\u001f{identity.Path}"));
        }

        if (discovery.HasUsableCatalog)
        {
            messages.Add(new ReferenceCatalogRefreshLogMessage(
                3,
                $"Reference catalog refresh: document '{documentName}' reference '{result.ReferenceName}' cached {discovery.Catalog!.Definitions.Count} external definitions.",
                $"cached\u001f{documentName}\u001f{result.ReferenceName}\u001f{discovery.Catalog!.Definitions.Count}"));
        }

        return messages;
    }

    private static string FormatCatalogSource(VbaProjectReferenceCatalogSource source)
        => source switch
        {
            VbaProjectReferenceCatalogSource.Bundled => "bundled",
            VbaProjectReferenceCatalogSource.Persisted => "persisted",
            VbaProjectReferenceCatalogSource.StalePersisted => "stale-persisted",
            VbaProjectReferenceCatalogSource.Generated => "generated",
            _ => "unavailable"
        };

    private static string FormatRefreshOutcome(VbaProjectReferenceCatalogRefreshResult result)
    {
        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache)
        {
            return "skipped";
        }

        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache)
        {
            return "stale";
        }

        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.PersistentCacheReadWarning)
        {
            return "cache-read-warning";
        }

        if (result.DiscoveryResult.IsFailure)
        {
            return "failed";
        }

        if (result.DiscoveryResult.IsAmbiguous)
        {
            return "ambiguous";
        }

        return result.DiscoveryResult.HasUsableCatalog ? "refreshed" : "skipped";
    }

    private static string FormatRefreshWarning(VbaProjectReferenceCatalogRefreshResult result)
    {
        var warning = result.WarningMessage ?? result.DiscoveryResult.ErrorMessage;
        if (string.IsNullOrWhiteSpace(warning) && result.DiscoveryResult.IsAmbiguous)
        {
            warning = $"Reference matched {result.DiscoveryResult.Identities.Count} TypeLib candidates.";
        }

        return string.IsNullOrWhiteSpace(warning)
            ? ""
            : $" warning=non-fatal: {warning.ReplaceLineEndings(" ")}";
    }

    private static string FormatBoolean(bool value)
        => value ? "true" : "false";

    private static long FormatElapsedMilliseconds(TimeSpan elapsed)
        => Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds));
}
