namespace VbaLanguageServer.SourceModel;

internal static partial class VbaCallablePresentation
{
    internal static VbaCallableSignature Assemble(
        VbaCallablePresentationShape shape,
        VbaCallableSignature metadata)
        => VbaCallableSignaturePresentation.Assemble(shape, metadata);

    internal static VbaCallableParameter PresentParameter(VbaCallableParameter parameter)
        => VbaCallableSignaturePresentation.PresentParameter(parameter);
}
