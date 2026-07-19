namespace VbaLanguageServer.Syntax;

internal readonly record struct VbaSourceOrigin(
    int Line,
    int Utf16Offset);

internal sealed record VbaModuleParseContext(
    VbaModuleKind Kind,
    VbaModuleIdentitySyntax Identity,
    IReadOnlyList<VbaModuleAttributeSyntax> Attributes,
    IReadOnlyList<VbaModuleOptionSyntax> Options,
    int CodeStartLine);

internal sealed record VbaModuleMemberSourceWindow(
    string Text,
    VbaSourceOrigin Origin,
    VbaModuleParseContext ModuleContext,
    int MemberStartLine,
    int MemberEndLine,
    int MemberStartOffset,
    int MemberEndOffset)
{
    public int MemberUtf16Length => MemberEndOffset - MemberStartOffset;
}
