using System.Text.Json.Nodes;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaSignatureHelpClientCapabilities(
    bool ContextSupport,
    bool ActiveParameterSupport,
    bool NoActiveParameterSupport)
{
    public static VbaSignatureHelpClientCapabilities None { get; } = new(false, false, false);
}

internal sealed record VbaWorkspaceEditClientCapabilities(
    bool DocumentChanges,
    IReadOnlyList<string> ResourceOperations)
{
    public static VbaWorkspaceEditClientCapabilities None { get; } = new(
        DocumentChanges: false,
        ResourceOperations: []);

    public bool SupportsRenameFile
        => DocumentChanges
            && ResourceOperations.Contains("rename", StringComparer.Ordinal);
}

internal sealed record VbaLspClientCapabilities(
    VbaSignatureHelpClientCapabilities SignatureHelp,
    bool DiagnosticRelatedInformation,
    VbaWorkspaceEditClientCapabilities WorkspaceEdit)
{
    public static VbaLspClientCapabilities None { get; } = new(
        VbaSignatureHelpClientCapabilities.None,
        DiagnosticRelatedInformation: false,
        VbaWorkspaceEditClientCapabilities.None);
}

/// <summary>
/// Shares one immutable initialized-client capability snapshot across LSP boundaries.
/// </summary>
internal sealed class VbaLspClientCapabilityState
{
    private VbaLspClientCapabilities snapshot = VbaLspClientCapabilities.None;

    public VbaLspClientCapabilities Snapshot => Volatile.Read(ref snapshot);

    public void Update(JsonObject initializeParameters)
    {
        var textDocument = initializeParameters["capabilities"]?["textDocument"];
        var workspaceEdit = initializeParameters["capabilities"]?
            ["workspace"]?["workspaceEdit"];
        var signatureHelp = textDocument?["signatureHelp"];
        var signatureInformation = signatureHelp?["signatureInformation"];
        Volatile.Write(
            ref snapshot,
            new VbaLspClientCapabilities(
                new VbaSignatureHelpClientCapabilities(
                    ContextSupport: IsTrue(signatureHelp?["contextSupport"]),
                    ActiveParameterSupport: IsTrue(
                        signatureInformation?["activeParameterSupport"]),
                    NoActiveParameterSupport: IsTrue(
                        signatureInformation?["noActiveParameterSupport"])),
                DiagnosticRelatedInformation: IsTrue(
                    textDocument?["publishDiagnostics"]?["relatedInformation"]),
                new VbaWorkspaceEditClientCapabilities(
                    DocumentChanges: IsTrue(workspaceEdit?["documentChanges"]),
                    ResourceOperations: ReadStringArray(
                        workspaceEdit?["resourceOperations"]))));
    }

    private static bool IsTrue(JsonNode? value)
        => value is JsonValue jsonValue
            && jsonValue.TryGetValue<bool>(out var result)
            && result;

    private static IReadOnlyList<string> ReadStringArray(JsonNode? value)
        => value is JsonArray array
            ? array
                .OfType<JsonValue>()
                .Select(item => item.TryGetValue<string>(out var text)
                    ? text
                    : null)
                .Where(item => item is not null)
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];
}
