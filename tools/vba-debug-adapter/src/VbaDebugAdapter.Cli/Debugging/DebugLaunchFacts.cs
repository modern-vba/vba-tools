using VbaTools.Syntax;

namespace VbaDebugAdapter.Debugging;

public sealed record DebugSourcePosition(
    string SourceUri,
    int Line,
    int Character);

public sealed record DebugSourceBreakpoint(
    string SourceUri,
    int EditorLine);

public sealed record DebugTargetProcedure(
    string ModuleName,
    string ProcedureName)
{
    public VbaConditionalCompilationBranchPath ConditionalCompilationPath
    {
        get;
        init;
    } = VbaConditionalCompilationBranchPath.Root;
}
