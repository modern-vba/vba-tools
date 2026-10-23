namespace VbaDebugAdapter.Debugging;

internal enum VbeNativeFailureReason
{
    BindingChanged, SourceMapMismatch, WrongPane, WrongSelection,
    CommandMissing, WrongCommand, CommandDisabled, InvocationFailed
}

internal sealed record VbeNativeFailureFacts(
    VbeNativeFailureReason Reason, string Operation,
    string? SourceUri, int? EditorLine, string? ModuleName, int? VbeLine);
