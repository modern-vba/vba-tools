using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Represents one reference catalog refresh result with its affected document scope.
/// </summary>
/// <param name="Uri">The URI that triggered the refresh work.</param>
/// <param name="DocumentName">The project document name affected by the result.</param>
/// <param name="Result">The catalog refresh result.</param>
internal sealed record ReferenceCatalogRefreshEvent(
    string Uri,
    string DocumentName,
    VbaProjectReferenceCatalogRefreshResult Result);

/// <summary>
/// Represents a reference catalog trace message and whether it should be de-duplicated.
/// </summary>
/// <param name="Message">The log message to publish.</param>
/// <param name="PublishOnce">Whether the message should be de-duplicated by key.</param>
internal sealed record ReferenceCatalogRefreshSessionMessage(
    ReferenceCatalogRefreshLogMessage Message,
    bool PublishOnce);

/// <summary>
/// Represents selections that should be refreshed in the background.
/// </summary>
/// <param name="Uri">The URI that triggered the refresh.</param>
/// <param name="Selections">The affected reference selections.</param>
internal sealed record ReferenceCatalogRefreshPlan(
    string Uri,
    IReadOnlyList<VbaProjectReferenceSelectionContext> Selections);

/// <summary>
/// Owns manifest resolution, preload, and background refresh ordering for reference catalogs.
/// </summary>
internal sealed class ReferenceCatalogRefreshSession
{
    private readonly VbaProjectReferenceCatalogAvailability catalogAvailability;

    public ReferenceCatalogRefreshSession(VbaProjectReferenceCatalogAvailability catalogAvailability)
    {
        this.catalogAvailability = catalogAvailability;
    }

    public IReadOnlyList<ReferenceCatalogRefreshSessionMessage> CreateReferenceSelectionTraceMessages(string uri)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelectionContext(
            uri,
            catalogAvailability.Current,
            out var context,
            out var error))
        {
            return error is null
                ? []
                : [CreateDirectMessage(
                    2,
                    $"Project manifest could not be resolved for reference selection: {error.Message}",
                    $"manifest-error\u001f{uri}\u001f{error.Message}")];
        }

        var messages = new List<ReferenceCatalogRefreshSessionMessage>();
        messages.AddRange(context.Messages.Select(message => CreateDirectMessage(
            message.Type,
            message.Text,
            $"selection\u001f{uri}\u001f{message.Type}\u001f{message.Text}")));

        foreach (var reference in context.ReferenceSelection?.References ?? [])
        {
            var source = catalogAvailability.GetCatalogSource(reference.Name);
            if (source == VbaProjectReferenceCatalogSource.Unavailable)
            {
                continue;
            }

            messages.Add(new ReferenceCatalogRefreshSessionMessage(
                ReferenceCatalogRefreshOutcome.CreateAvailabilityMessage(
                    context.Resolution.DocumentName,
                    reference.Name,
                    source),
                PublishOnce: true));
        }

        return messages;
    }

    public IReadOnlyList<ReferenceCatalogRefreshEvent> PreloadReferenceCatalogs(string uri, string text)
    {
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            return [];
        }

        return selections
            .SelectMany(selectionContext => catalogAvailability
                .PreloadPersistedCatalogs(selectionContext.Selection)
                .Select(result => new ReferenceCatalogRefreshEvent(
                    uri,
                    selectionContext.DocumentName,
                    result)))
            .ToArray();
    }

    public bool TryCreateBackgroundRefreshPlan(
        string uri,
        string text,
        out ReferenceCatalogRefreshPlan plan)
    {
        plan = default!;
        if (!LanguageServerManifestResolution.TryCreateReferenceSelections(uri, text, out var selections))
        {
            return false;
        }

        plan = new ReferenceCatalogRefreshPlan(uri, selections);
        return true;
    }

    public async Task<IReadOnlyList<ReferenceCatalogRefreshEvent>> RefreshAsync(
        ReferenceCatalogRefreshPlan plan,
        CancellationToken cancellationToken)
    {
        var events = new List<ReferenceCatalogRefreshEvent>();
        foreach (var selectionContext in plan.Selections)
        {
            var results = await catalogAvailability.RefreshAsync(selectionContext.Selection, cancellationToken);
            events.AddRange(results.Select(result => new ReferenceCatalogRefreshEvent(
                plan.Uri,
                selectionContext.DocumentName,
                result)));
        }

        return events;
    }

    private static ReferenceCatalogRefreshSessionMessage CreateDirectMessage(
        int type,
        string text,
        string key)
        => new(new ReferenceCatalogRefreshLogMessage(type, text, key), PublishOnce: false);
}
