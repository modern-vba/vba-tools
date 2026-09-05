using System.Collections.Immutable;
using System.Text;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Debugging;

public interface IBreakpointSourceMapper
{
    VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint);
}

public sealed class BreakpointSourceMapper : IBreakpointSourceMapper
{
    public VbeBreakpoint Map(
        DebugSourceSnapshot snapshot,
        DebugSourceBreakpoint breakpoint)
    {
        if (!Uri.TryCreate(breakpoint.SourceUri, UriKind.Absolute, out var breakpointUri) ||
            !breakpointUri.IsFile)
        {
            throw new DebugSetupException(
                $"Debug breakpoint source must be a persistent file URI: '{breakpoint.SourceUri}'.");
        }

        var sourceMatches = snapshot.Sources
            .Where(source => source.SourceUri.Equals(
                breakpointUri.AbsoluteUri,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Debug breakpoint source '{breakpoint.SourceUri}' is not present in the source snapshot."
                : $"Debug breakpoint source '{breakpoint.SourceUri}' is ambiguous in the source snapshot.");
        }

        var source = sourceMatches[0];
        if (!IsExportedVbaSource(source.RelativePath))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source must be an exported .bas, .cls, or .frm file: " +
                $"'{source.RelativePath}'.");
        }

        var parsedSources = snapshot.Sources
            .Where(candidate => IsExportedVbaSource(candidate.RelativePath))
            .Select(ParseSource)
            .ToArray();
        var parsedSource = parsedSources.Single(candidate => candidate.SourceUri.Equals(
            breakpointUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase));
        var syntaxTree = parsedSource.SyntaxTree;
        var identityAttributes = syntaxTree.Module.Attributes
            .Where(attribute => attribute.Name.Equals(
                "VB_Name",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (identityAttributes.Length != 1 ||
            !IsValidModuleIdentity(identityAttributes[0].Value))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source '{source.SourceUri}' does not contain exactly one " +
                "valid exported module identity.");
        }

        var ambiguousIdentity = parsedSources
            .Where(candidate => candidate.ValidModuleIdentity is not null)
            .GroupBy(
                candidate => candidate.ValidModuleIdentity!,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (ambiguousIdentity is not null)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint setup: exported module identity " +
                $"'{ambiguousIdentity.Key}' is ambiguous in the source snapshot.");
        }

        var projection = VbaCodeModuleProjection.Create(syntaxTree);
        if (breakpoint.EditorLine < 0 || breakpoint.EditorLine >= projection.Lines.Count)
        {
            throw new DebugSetupException(
                $"Debug breakpoint line {breakpoint.EditorLine} is outside '{source.SourceUri}'.");
        }

        var projectedLine = projection.Lines[breakpoint.EditorLine];
        var conditionalPath = projectedLine.ConditionalCompilationPath;
        if (conditionalPath is null)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{source.SourceUri}:{breakpoint.EditorLine + 1}': " +
                "the conditional-compilation branch identity is not structurally complete. " +
                "The breakpoint was not relocated.");
        }
        if (projectedLine.Role != VbaCodeModuleLineRole.Code ||
            projectedLine.CodeModuleLine is not int vbideLine ||
            projectedLine.ExecutionKind != VbaPhysicalLineExecutionKind.ExecutableCandidate)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{source.SourceUri}:{breakpoint.EditorLine + 1}': " +
                $"{DescribeInvalidLocation(projectedLine)}. The breakpoint was not relocated.");
        }

        return new VbeBreakpoint(
            breakpoint,
            new VbeCodeModuleSourceMap(
                projection.ModuleName,
                projection.ModuleKind,
                projection.CodeModuleLines.ToImmutableArray()),
            vbideLine)
        {
            ConditionalCompilationPath = conditionalPath
        };
    }

    private static ParsedBreakpointSource ParseSource(DebugSourceFileSnapshot source)
    {
        var syntaxTree = VbaSyntaxTree.ParseModule(source.SourceUri, source.Text);
        var identityAttributes = syntaxTree.Module.Attributes
            .Where(attribute => attribute.Name.Equals(
                "VB_Name",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var validModuleIdentity = identityAttributes.Length == 1 &&
                                  IsValidModuleIdentity(identityAttributes[0].Value)
            ? identityAttributes[0].Value
            : null;
        return new ParsedBreakpointSource(
            source.SourceUri,
            syntaxTree,
            validModuleIdentity);
    }

    private static bool IsExportedVbaSource(string relativePath)
        => new[] { ".bas", ".cls", ".frm" }.Contains(
            Path.GetExtension(relativePath),
            StringComparer.OrdinalIgnoreCase);

    private static bool IsValidModuleIdentity(string name)
        => name.EnumerateRunes().Count() is > 0 and <= 31
            && VbaIdentifier.IsIdentifier(name);

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

    private sealed record ParsedBreakpointSource(
        string SourceUri,
        VbaSyntaxTree SyntaxTree,
        string? ValidModuleIdentity);
}
