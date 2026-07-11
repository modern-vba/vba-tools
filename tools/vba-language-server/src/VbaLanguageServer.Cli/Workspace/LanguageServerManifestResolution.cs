using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal sealed record VbaLanguageServerManifestMessage(int Type, string Text);

internal sealed record VbaProjectReferenceSelectionContext(
    string DocumentName,
    VbaProjectReferenceSelection Selection);

internal sealed record VbaLanguageServerManifestContext(
    VbaProjectResolution Resolution,
    VbaProjectReferenceSelection? ReferenceSelection,
    IReadOnlyList<VbaLanguageServerManifestMessage> Messages);

internal static class LanguageServerManifestResolution
{
    public static VbaLanguageServerManifestContext Create(
        VbaProjectResolution resolution,
        VbaProjectReferenceCatalogSet catalogSet)
    {
        var selection =
            resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && !string.IsNullOrEmpty(resolution.DocumentKind)
                ? VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries)
                : null;

        var messages = selection is null
            ? []
            : CreateReferenceSelectionMessages(resolution, selection, catalogSet);
        return new VbaLanguageServerManifestContext(resolution, selection, messages);
    }

    public static bool TryCreateReferenceSelectionContext(
        string uri,
        VbaProjectReferenceCatalogSet catalogSet,
        out VbaLanguageServerManifestContext context,
        out ProjectManifestException? error)
    {
        context = new VbaLanguageServerManifestContext(
            new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, ""),
            null,
            []);
        error = null;

        try
        {
            var resolution = VbaProjectResolver.Resolve(uri);
            context = Create(resolution, catalogSet);
            return context.ReferenceSelection is not null;
        }
        catch (ProjectManifestException ex)
        {
            error = ex;
            return false;
        }
    }

    public static bool TryCreateReferenceSelections(
        string uri,
        string text,
        out IReadOnlyList<VbaProjectReferenceSelectionContext> selections)
    {
        selections = [];
        if (IsProjectManifestUri(uri))
        {
            try
            {
                var manifest = ProjectManifestReader.Parse(text, uri);
                selections = manifest.Documents
                    .Select(document => new VbaProjectReferenceSelectionContext(
                        document.Key,
                        VbaProjectReferenceSelection.Create(
                            document.Value.Kind,
                            document.Value.References ?? [])))
                    .ToArray();
                return selections.Count > 0;
            }
            catch (ProjectManifestException)
            {
                return false;
            }
        }

        VbaProjectResolution resolution;
        try
        {
            resolution = VbaProjectResolver.Resolve(uri);
        }
        catch (ProjectManifestException)
        {
            return false;
        }

        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrEmpty(resolution.DocumentName)
            || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return false;
        }

        selections =
        [
            new VbaProjectReferenceSelectionContext(
                resolution.DocumentName,
                VbaProjectReferenceSelection.Create(
                    resolution.DocumentKind,
                    resolution.ReferenceEntries))
        ];
        return true;
    }

    private static IReadOnlyList<VbaLanguageServerManifestMessage> CreateReferenceSelectionMessages(
        VbaProjectResolution resolution,
        VbaProjectReferenceSelection selection,
        VbaProjectReferenceCatalogSet catalogSet)
    {
        if (string.IsNullOrEmpty(resolution.DocumentName) || string.IsNullOrEmpty(resolution.DocumentKind))
        {
            return [];
        }

        var messages = new List<VbaLanguageServerManifestMessage>();
        var references = selection.References.Count == 0
            ? "<none>"
            : string.Join(", ", selection.References.Select(reference => reference.Name));
        messages.Add(new VbaLanguageServerManifestMessage(
            3,
            $"VbaProjectReferenceSelection document={resolution.DocumentName} references={references} main={selection.MainVbaProjectReference?.Name ?? "<none>"}"));

        if (selection.MissingExpectedMainReference is not null)
        {
            messages.Add(new VbaLanguageServerManifestMessage(
                2,
                $"Manifest/reference consistency warning: document '{resolution.DocumentName}' kind '{resolution.DocumentKind}' is missing expected main reference '{selection.MissingExpectedMainReference}'. Host definitions will not be activated implicitly."));
        }

        foreach (var referenceName in catalogSet.GetMissingCatalogReferenceNames(selection))
        {
            messages.Add(new VbaLanguageServerManifestMessage(
                2,
                $"Reference catalog availability warning: document '{resolution.DocumentName}' reference '{referenceName}' has no bundled or cached VbaProjectReferenceCatalog metadata. The reference remains active, but external definitions are unavailable."));
        }

        return messages;
    }

    private static bool IsProjectManifestUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null
            && Path.GetFileName(localPath).Equals("project.json", StringComparison.OrdinalIgnoreCase);
    }
}
