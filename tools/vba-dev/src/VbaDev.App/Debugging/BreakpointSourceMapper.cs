using System.Collections.Immutable;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies one enabled ordinary source breakpoint captured for a debug launch.
/// </summary>
/// <param name="SourcePath">The absolute exported-source path.</param>
/// <param name="EditorLine">The zero-based physical editor line.</param>
public sealed record DebugSourceBreakpoint(string SourcePath, int EditorLine);

/// <summary>
/// Contains the exact projected contents and identity of one generated VBIDE code module.
/// </summary>
/// <param name="ModuleName">The generated VBA module identity.</param>
/// <param name="ModuleKind">The exported VBA module kind.</param>
/// <param name="CodeLines">Every exact line expected in the generated code module.</param>
public sealed record VbeCodeModuleSourceMap(
    string ModuleName,
    VbaModuleKind ModuleKind,
    ImmutableArray<string> CodeLines);

/// <summary>
/// Identifies one source breakpoint mapped to an exact generated VBIDE line.
/// </summary>
/// <param name="Source">The captured source breakpoint.</param>
/// <param name="SourceMap">The complete generated-module source map.</param>
/// <param name="VbideLine">The one-based VBIDE code-module line.</param>
public sealed record VbeBreakpoint(
    DebugSourceBreakpoint Source,
    VbeCodeModuleSourceMap SourceMap,
    int VbideLine)
{
    /// <summary>
    /// Gets the exact structural conditional-compilation path containing the source line.
    /// </summary>
    public VbaConditionalCompilationBranchPath ConditionalCompilationPath { get; init; } =
        VbaConditionalCompilationBranchPath.Root;

    /// <summary>
    /// Gets the generated VBA module identity.
    /// </summary>
    public string ModuleName => SourceMap.ModuleName;

    /// <summary>
    /// Gets the exact code text expected at the mapped breakpoint line.
    /// </summary>
    public string ExpectedCodeLine => SourceMap.CodeLines[VbideLine - 1];
}

/// <summary>
/// Maps saved exported-source breakpoint positions to exact VBIDE code-module positions.
/// </summary>
public interface IBreakpointSourceMapper
{
    /// <summary>
    /// Maps one captured source breakpoint without relocating it to another source line.
    /// </summary>
    VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint);
}

/// <summary>
/// Maps exported-source breakpoints through the shared syntax-core projection.
/// </summary>
public sealed class BreakpointSourceMapper : IBreakpointSourceMapper
{
    /// <inheritdoc />
    public VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint)
    {
        if (!Path.IsPathFullyQualified(breakpoint.SourcePath))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source path must be absolute: '{breakpoint.SourcePath}'.");
        }

        var sourcePath = Path.GetFullPath(breakpoint.SourcePath);
        var sourceMatches = snapshot.Sources
            .Where(source => Path.GetFullPath(source.Path).Equals(
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Debug breakpoint source '{breakpoint.SourcePath}' is not present in the saved source snapshot."
                : $"Debug breakpoint source '{breakpoint.SourcePath}' is ambiguous in the saved source snapshot.");
        }

        var source = sourceMatches[0];
        if (!IsExportedVbaSource(source.Path))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source must be an exported .bas, .cls, or .frm file: '{source.Path}'.");
        }

        var parsedSources = snapshot.Sources
            .Where(candidate => IsExportedVbaSource(candidate.Path))
            .Select(ParseSource)
            .ToArray();
        var parsedSource = parsedSources.Single(candidate => candidate.Path.Equals(
            sourcePath,
            StringComparison.OrdinalIgnoreCase));
        var syntaxTree = parsedSource.SyntaxTree;
        var identityAttributes = syntaxTree.Module.Attributes
            .Where(attribute => attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (identityAttributes.Length != 1
            || !IsValidModuleIdentity(identityAttributes[0].Value))
        {
            throw new DebugSetupException(
                $"Debug breakpoint source '{source.Path}' does not contain exactly one valid exported module identity.");
        }

        var ambiguousIdentity = parsedSources
            .Where(candidate => candidate.ValidModuleIdentity is not null)
            .GroupBy(
                candidate => candidate.ValidModuleIdentity!,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (ambiguousIdentity is not null)
        {
            var conflictingPaths = ambiguousIdentity
                .Select(candidate => candidate.Path)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            throw new DebugSetupException(
                $"Invalid breakpoint setup: exported module identity '{ambiguousIdentity.Key}' is ambiguous " +
                $"in the saved source snapshot: {string.Join(", ", conflictingPaths)}.");
        }

        var projection = VbaCodeModuleProjection.Create(syntaxTree);
        if (breakpoint.EditorLine < 0 || breakpoint.EditorLine >= projection.Lines.Count)
        {
            throw new DebugSetupException(
                $"Debug breakpoint line {breakpoint.EditorLine} is outside '{source.Path}'.");
        }

        var projectedLine = projection.Lines[breakpoint.EditorLine];
        var conditionalCompilationPath = projectedLine.ConditionalCompilationPath;
        if (conditionalCompilationPath is null)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{source.Path}:{breakpoint.EditorLine + 1}': " +
                "the conditional-compilation branch identity is not structurally complete. " +
                "The breakpoint was not relocated.");
        }

        if (projectedLine.Role != VbaCodeModuleLineRole.Code
            || projectedLine.CodeModuleLine is not int vbideLine
            || projectedLine.ExecutionKind != VbaPhysicalLineExecutionKind.ExecutableCandidate)
        {
            throw new DebugSetupException(
                $"Invalid breakpoint at '{source.Path}:{breakpoint.EditorLine + 1}': " +
                $"{DescribeInvalidLocation(projectedLine)}. The breakpoint was not relocated.");
        }

        var sourceMap = new VbeCodeModuleSourceMap(
            projection.ModuleName,
            projection.ModuleKind,
            projection.CodeModuleLines.ToImmutableArray());
        return new VbeBreakpoint(breakpoint, sourceMap, vbideLine)
        {
            ConditionalCompilationPath = conditionalCompilationPath
        };
    }

    private static bool IsValidModuleIdentity(string name)
        => !string.IsNullOrWhiteSpace(name)
            && char.IsLetter(name[0])
            && name.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsExportedVbaSource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedBreakpointSource ParseSource(DebugSourceFileSnapshot source)
    {
        var path = Path.GetFullPath(source.Path);
        var syntaxTree = VbaSyntaxTree.ParseModule(new Uri(path).AbsoluteUri, source.Text);
        var identityAttributes = syntaxTree.Module.Attributes
            .Where(attribute => attribute.Name.Equals("VB_Name", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var validModuleIdentity = identityAttributes.Length == 1
            && IsValidModuleIdentity(identityAttributes[0].Value)
                ? identityAttributes[0].Value
                : null;
        return new ParsedBreakpointSource(path, syntaxTree, validModuleIdentity);
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

    private sealed record ParsedBreakpointSource(
        string Path,
        VbaSyntaxTree SyntaxTree,
        string? ValidModuleIdentity);
}
