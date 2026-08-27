using System.Text.Json.Nodes;

namespace VbaLanguageServer.Lsp;

internal sealed record VbaSignatureHelpClientCapabilities(
    bool ContextSupport,
    bool ActiveParameterSupport,
    bool NoActiveParameterSupport)
{
    public static VbaSignatureHelpClientCapabilities None { get; } = new(false, false, false);
}

internal sealed record VbaLspClientCapabilities(
    VbaSignatureHelpClientCapabilities SignatureHelp,
    bool DiagnosticRelatedInformation)
{
    public static VbaLspClientCapabilities None { get; } = new(
        VbaSignatureHelpClientCapabilities.None,
        DiagnosticRelatedInformation: false);
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
                    textDocument?["publishDiagnostics"]?["relatedInformation"])));
    }

    private static bool IsTrue(JsonNode? value)
        => value is JsonValue jsonValue
            && jsonValue.TryGetValue<bool>(out var result)
            && result;
}
