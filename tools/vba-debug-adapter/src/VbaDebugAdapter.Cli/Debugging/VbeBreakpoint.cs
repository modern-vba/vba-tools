using System.Collections.Immutable;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Debugging;

public sealed record VbeCodeModuleSourceMap(
    string ModuleName,
    VbaModuleKind ModuleKind,
    ImmutableArray<string> CodeLines);

public sealed record VbeBreakpoint(
    DebugSourceBreakpoint Source,
    VbeCodeModuleSourceMap SourceMap,
    int VbideLine)
{
    public VbaConditionalCompilationBranchPath ConditionalCompilationPath { get; init; } =
        VbaConditionalCompilationBranchPath.Root;

    public string ModuleName => SourceMap.ModuleName;

    public string ExpectedCodeLine => SourceMap.CodeLines[VbideLine - 1];
}
