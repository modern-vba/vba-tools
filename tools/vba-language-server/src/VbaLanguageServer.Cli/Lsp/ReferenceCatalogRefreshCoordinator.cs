using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Publishes reference-selection trace messages and starts background catalog refresh work.
/// </summary>
internal sealed class ReferenceCatalogRefreshCoordinator
{
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectReferenceCatalogRefreshService catalogRefreshService;
    private readonly LspMessageTransport transport;
    private readonly object diagnosticGate = new();
    private readonly HashSet<string> publishedDiagnostics = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a reference catalog refresh coordinator.
    /// </summary>
    /// <param name="referenceCatalogCache">The current reference catalog cache.</param>
    /// <param name="catalogRefreshService">The refresh service for missing catalogs.</param>
    /// <param name="transport">The transport used to publish log messages.</param>
    public ReferenceCatalogRefreshCoordinator(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        VbaProjectReferenceCatalogRefreshService catalogRefreshService,
        LspMessageTransport transport)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        this.catalogRefreshService = catalogRefreshService;
        this.transport = transport;
    }

    /// <summary>
    /// Publishes trace and warning messages for the reference selection that applies to a URI.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for message publication.</param>
    public async Task PublishReferenceSelectionTraceAsync(string uri, CancellationToken cancellationToken)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelectionContext(
            uri,
            referenceCatalogCache.Current,
            out var context,
            out var error))
        {
            if (error is not null)
            {
                await transport.WriteLogMessageAsync(
                    2,
                    $"Project manifest could not be resolved for reference selection: {error.Message}",
                    cancellationToken);
            }

            return;
        }

        foreach (var message in context.Messages)
        {
            await transport.WriteLogMessageAsync(
                message.Type,
                message.Text,
                cancellationToken);
        }

        foreach (var reference in context.ReferenceSelection?.References ?? [])
        {
            var source = referenceCatalogCache.GetCatalogSource(reference.Name);
            if (source == VbaProjectReferenceCatalogSource.Unavailable)
            {
                continue;
            }

            await WriteLogMessageOnceAsync(
                3,
                $"Reference catalog availability: document '{context.Resolution.DocumentName}' reference '{reference.Name}' source={FormatCatalogSource(source)} outcome=available.",
                $"availability\u001f{context.Resolution.DocumentName}\u001f{reference.Name}\u001f{source}",
                cancellationToken);
        }
    }

    /// <summary>
    /// Starts background catalog refresh for reference selections affected by a document text change.
    /// </summary>
    /// <param name="uri">The changed document URI.</param>
    /// <param name="text">The changed document text.</param>
    /// <param name="cancellationToken">A cancellation token for refresh work.</param>
    public void RefreshReferenceCatalogsInBackground(string uri, string text, CancellationToken cancellationToken)
    {
        if (LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            _ = RefreshReferenceCatalogsInBackgroundAsync(uri, selections, cancellationToken);
        }
    }

    private async Task RefreshReferenceCatalogsInBackgroundAsync(
        string uri,
        IReadOnlyList<VbaProjectReferenceSelectionContext> selections,
        CancellationToken cancellationToken)
    {
        foreach (var selectionContext in selections)
        {
            IReadOnlyList<VbaProjectReferenceCatalogRefreshResult> results;
            try
            {
                results = await catalogRefreshService.RefreshAsync(selectionContext.Selection, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var result in results)
            {
                await PublishCatalogRefreshResultAsync(
                    uri,
                    selectionContext.DocumentName,
                    result,
                    cancellationToken);
            }
        }
    }

    private async Task PublishCatalogRefreshResultAsync(
        string uri,
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
    {
        await PublishCatalogRefreshDiagnosticAsync(documentName, result, cancellationToken);

        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.PersistentCacheReadWarning)
        {
            return;
        }

        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.SkippedValidPersistentCache)
        {
            return;
        }

        if (result.Status == VbaProjectReferenceCatalogRefreshStatus.LoadedStalePersistentCache)
        {
            return;
        }

        var discovery = result.DiscoveryResult;
        if (discovery.IsFailure)
        {
            return;
        }

        if (discovery.IsAmbiguous)
        {
            return;
        }

        var identity = discovery.Identities.SingleOrDefault();
        if (identity is not null)
        {
            await transport.WriteLogMessageAsync(
                3,
                $"Reference catalog discovery: document '{documentName}' reference '{result.ReferenceName}' resolved to TypeLib {identity.Guid} {identity.MajorVersion}.{identity.MinorVersion} LCID {identity.Lcid} at {identity.Path}.",
                cancellationToken);
        }

        if (discovery.HasUsableCatalog)
        {
            await transport.WriteLogMessageAsync(
                3,
                $"Reference catalog refresh: document '{documentName}' reference '{result.ReferenceName}' cached {discovery.Catalog!.Definitions.Count} external definitions.",
                cancellationToken);
        }
    }

    private async Task PublishCatalogRefreshDiagnosticAsync(
        string documentName,
        VbaProjectReferenceCatalogRefreshResult result,
        CancellationToken cancellationToken)
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

        await WriteLogMessageOnceAsync(type, message, key, cancellationToken);
    }

    private async Task WriteLogMessageOnceAsync(
        int type,
        string message,
        string key,
        CancellationToken cancellationToken)
    {
        lock (diagnosticGate)
        {
            if (!publishedDiagnostics.Add(key))
            {
                return;
            }
        }

        await transport.WriteLogMessageAsync(type, message, cancellationToken);
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
