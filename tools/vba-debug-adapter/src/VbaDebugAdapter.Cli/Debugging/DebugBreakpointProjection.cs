using System.Collections.Immutable;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Debugging;

/// <summary>
/// Maps editor lines from one already parsed VBA source into VBE code-module lines.
/// </summary>
internal sealed class DebugBreakpointProjection
{
    private readonly VbaCodeModuleProjection projection;
    private readonly VbeCodeModuleSourceMap sourceMap;

    private DebugBreakpointProjection(VbaCodeModuleProjection projection)
    {
        this.projection = projection;
        sourceMap = new VbeCodeModuleSourceMap(
            projection.ModuleName,
            projection.ModuleKind,
            projection.CodeModuleLines.ToImmutableArray());
    }

    internal static DebugBreakpointProjection Create(VbaSyntaxTree syntaxTree)
    {
        ArgumentNullException.ThrowIfNull(syntaxTree);
        return new DebugBreakpointProjection(VbaCodeModuleProjection.Create(syntaxTree));
    }

    internal VbeBreakpoint Map(DebugSourceBreakpoint breakpoint)
    {
        ArgumentNullException.ThrowIfNull(breakpoint);
        if (breakpoint.EditorLine < 0 || breakpoint.EditorLine >= projection.Lines.Count)
        {
            throw new DebugSetupException(
                $"Debug breakpoint line {breakpoint.EditorLine} is outside '{breakpoint.SourceUri}'.");
        }

        var projectedLine = projection.Lines[breakpoint.EditorLine];
        var conditionalPath = projectedLine.ConditionalCompilationPath;
        if (conditionalPath is null)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{breakpoint.SourceUri}:{breakpoint.EditorLine + 1}': " +
                "the conditional-compilation branch identity is not structurally complete. " +
                "The breakpoint was not relocated.");
        }
        if (projectedLine.Role != VbaCodeModuleLineRole.Code ||
            projectedLine.CodeModuleLine is not int vbideLine ||
            projectedLine.ExecutionKind != VbaPhysicalLineExecutionKind.ExecutableCandidate)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{breakpoint.SourceUri}:{breakpoint.EditorLine + 1}': " +
                $"{DescribeInvalidLocation(projectedLine)}. The breakpoint was not relocated.");
        }

        return new VbeBreakpoint(breakpoint, sourceMap, vbideLine)
        {
            ConditionalCompilationPath = conditionalPath
        };
    }

    private static string DescribeInvalidLocation(VbaCodeModuleLineProjection line)
        => line.ExecutionKind switch
        {
            VbaPhysicalLineExecutionKind.Blank => "the physical source line is blank",
            VbaPhysicalLineExecutionKind.Comment => "the physical source line is comment-only",
            VbaPhysicalLineExecutionKind.DeclarationOnly => "the physical source line is declaration-only",
            VbaPhysicalLineExecutionKind.ProcedureBoundary => "the physical source line is a procedure boundary",
            VbaPhysicalLineExecutionKind.Continuation => "the physical source line is a non-executable continuation",
            VbaPhysicalLineExecutionKind.LabelOnly => "the physical source line is label-only",
            VbaPhysicalLineExecutionKind.Directive => "the physical source line is a conditional-compilation directive",
            VbaPhysicalLineExecutionKind.ExportMetadata => "the physical source line is export-only metadata",
            VbaPhysicalLineExecutionKind.Malformed => "the physical source line contains malformed syntax",
            _ => "the physical source line is not proven executable"
        };
}
