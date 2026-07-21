using System.Collections.Immutable;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Debugging;

/// <summary>
/// Contains one exported VBA source captured after editor saves have completed.
/// </summary>
/// <param name="Path">The absolute exported-source path.</param>
/// <param name="Text">The exact saved source text.</param>
public sealed record DebugSourceFileSnapshot(string Path, string Text);

/// <summary>
/// Identifies one zero-based position in a captured exported VBA source.
/// </summary>
/// <param name="Path">The absolute exported-source path.</param>
/// <param name="Line">The zero-based physical line.</param>
/// <param name="Character">The zero-based UTF-16 character offset on the line.</param>
public sealed record DebugSourcePosition(string Path, int Line, int Character);

/// <summary>
/// Contains the immutable saved source state supplied for one debug launch.
/// </summary>
/// <param name="SchemaVersion">The snapshot wire-contract version.</param>
/// <param name="Sources">All exported VBA sources in the selected document.</param>
/// <param name="ActiveSource">The post-save active source position, when target selection is inferred.</param>
public sealed record DebugSourceSnapshot(
    int SchemaVersion,
    ImmutableArray<DebugSourceFileSnapshot> Sources,
    DebugSourcePosition? ActiveSource)
{
    /// <summary>
    /// Gets the only snapshot wire-contract version supported by this adapter.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the enabled ordinary line breakpoints captured with this saved source state.
    /// </summary>
    public ImmutableArray<DebugSourceBreakpoint> Breakpoints { get; init; } = [];
}

/// <summary>
/// Resolves one debug target exclusively from a saved source snapshot.
/// </summary>
public sealed class DebugLaunchRequestResolver
{
    /// <summary>
    /// Resolves an explicit or active-position target and returns a launch request bound to the snapshot.
    /// </summary>
    public DebugLaunchRequest Resolve(
        ResolvedProjectContext context,
        DebugSourceSnapshot sourceSnapshot,
        string? moduleName,
        string? procedureName)
    {
        if (sourceSnapshot.SchemaVersion != DebugSourceSnapshot.CurrentSchemaVersion)
        {
            throw new DebugSetupException(
                $"Unsupported sourceSnapshot.schemaVersion '{sourceSnapshot.SchemaVersion}'; " +
                $"expected {DebugSourceSnapshot.CurrentSchemaVersion}.");
        }

        var hasModule = !string.IsNullOrWhiteSpace(moduleName);
        var hasProcedure = !string.IsNullOrWhiteSpace(procedureName);
        if (hasModule != hasProcedure)
        {
            throw new DebugSetupException(
                "The VBA launch request must specify 'module' and 'procedure' together.");
        }

        ValidateSourceMembership(context, sourceSnapshot);
        var parsedSources = sourceSnapshot.Sources
            .Select(source => new ParsedDebugSource(
                source,
                VbaSyntaxTree.ParseModule(new Uri(source.Path).AbsoluteUri, source.Text)))
            .ToArray();
        var target = hasModule
            ? ResolveExplicitTarget(parsedSources, moduleName!, procedureName!)
            : ResolveActiveTarget(parsedSources, sourceSnapshot.ActiveSource);
        var resolvedModuleName = target.Source.SyntaxTree.Module.Identity.Name;
        var resolvedProcedureName = target.Callable.Name;
        var resolvedModuleMatches = parsedSources.Count(source =>
            source.SyntaxTree.Module.Identity.Name.Equals(
                resolvedModuleName,
                StringComparison.OrdinalIgnoreCase));
        if (resolvedModuleMatches != 1)
        {
            throw new DebugSetupException(
                $"VBA debug module '{resolvedModuleName}' is ambiguous in the selected document source snapshot.");
        }

        var resolvedProcedureMatches = target.Source.SyntaxTree.Module.CallableDeclarations.Count(callable =>
            callable.Name.Equals(resolvedProcedureName, StringComparison.OrdinalIgnoreCase));
        if (resolvedProcedureMatches != 1)
        {
            throw new DebugSetupException(
                $"VBA debug procedure '{resolvedModuleName}.{resolvedProcedureName}' is ambiguous " +
                "in the selected document source snapshot.");
        }

        if (target.Source.SyntaxTree.Module.Kind != VbaModuleKind.StandardModule)
        {
            throw new DebugSetupException(
                $"VBA debug module '{resolvedModuleName}' is not a standard module; " +
                "class, form, and document modules cannot contain a debug target.");
        }

        var callable = target.Callable;
        if (!string.Equals(callable.DeclarationKeyword, "Sub", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"VBA debug target '{resolvedModuleName}.{resolvedProcedureName}' is not a Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
        }

        var hasEligibleVisibilityKeyword = string.IsNullOrEmpty(callable.VisibilityKeyword)
            || callable.VisibilityKeyword.Equals("Public", StringComparison.OrdinalIgnoreCase);
        if (callable.Visibility != VbaDeclarationVisibility.Public || !hasEligibleVisibilityKeyword)
        {
            throw new DebugSetupException(
                $"VBA debug target '{resolvedModuleName}.{resolvedProcedureName}' is not public; " +
                "the target must be a public parameterless Sub in a standard module.");
        }

        if (callable.Parameters.Count != 0)
        {
            throw new DebugSetupException(
                $"VBA debug target '{resolvedModuleName}.{resolvedProcedureName}' is not parameterless; " +
                "the target must be a public parameterless Sub in a standard module.");
        }

        if (callable.IsExternal)
        {
            throw new DebugSetupException(
                $"VBA debug target '{resolvedModuleName}.{resolvedProcedureName}' is an external Declare Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
        }

        return new DebugLaunchRequest(
            context,
            new DebugTargetProcedure(resolvedModuleName, resolvedProcedureName),
            sourceSnapshot);
    }

    private static void ValidateSourceMembership(
        ResolvedProjectContext context,
        DebugSourceSnapshot sourceSnapshot)
    {
        var sourceSetPath = Path.GetFullPath(context.DocumentSourceSetPath);
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedSourcePaths = new List<string>(sourceSnapshot.Sources.Length);
        foreach (var source in sourceSnapshot.Sources)
        {
            if (!Path.IsPathFullyQualified(source.Path))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path must be absolute: '{source.Path}'.");
            }

            var sourcePath = Path.GetFullPath(source.Path);
            if (!source.Path.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path is not canonical: '{source.Path}'. Expected '{sourcePath}'.");
            }

            if (!sourcePaths.Add(sourcePath))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot contains duplicate path '{source.Path}'.");
            }

            var relativePath = Path.GetRelativePath(sourceSetPath, sourcePath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path '{source.Path}' is outside selected document source set " +
                    $"'{context.DocumentSourceSetPath}'.");
            }

            if (!DocumentSourceSetLayout.IsVbaSourceFile(sourcePath))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path '{source.Path}' is not a .bas, .cls, or .frm source file.");
            }

            orderedSourcePaths.Add(sourcePath);
        }

        var sortedSourcePaths = orderedSourcePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!orderedSourcePaths.SequenceEqual(sortedSourcePaths, StringComparer.Ordinal))
        {
            throw new DebugSetupException(
                "Debug source snapshot sources must be supplied in canonical path order.");
        }

        var diskPaths = DocumentSourceSetLayout
            .EnumerateVbaSourcePaths(sourceSetPath)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flatIdentityCollisions = DocumentSourceSetLayout.FindSourceFileNameCollisions(diskPaths);
        if (flatIdentityCollisions.Count != 0)
        {
            var collisions = flatIdentityCollisions.Select(collision =>
                $"'{collision.FileName}': {string.Join(", ", collision.SourcePaths)}");
            throw new DebugSetupException(
                "Selected document source set contains duplicate flat source file identity: " +
                string.Join("; ", collisions));
        }

        var unexpectedPaths = sourcePaths
            .Where(path => !diskPaths.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpectedPaths.Length != 0)
        {
            throw new DebugSetupException(
                $"Debug source snapshot path(s) are not present in the selected document source set: " +
                string.Join(", ", unexpectedPaths));
        }

        var missingPaths = diskPaths
            .Where(path => !sourcePaths.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPaths.Length != 0)
        {
            throw new DebugSetupException(
                $"Debug source snapshot is missing selected document source path(s): " +
                string.Join(", ", missingPaths));
        }
    }

    private static ResolvedDebugTarget ResolveExplicitTarget(
        IReadOnlyList<ParsedDebugSource> parsedSources,
        string moduleName,
        string procedureName)
    {
        var moduleMatches = parsedSources
            .Where(source => source.SyntaxTree.Module.Identity.Name.Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (moduleMatches.Length != 1)
        {
            throw new DebugSetupException(moduleMatches.Length == 0
                ? $"VBA debug module '{moduleName}' was not found in the selected document source snapshot."
                : $"VBA debug module '{moduleName}' is ambiguous in the selected document source snapshot.");
        }

        var callableMatches = moduleMatches[0].SyntaxTree.Module.CallableDeclarations
            .Where(callable => callable.Name.Equals(procedureName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (callableMatches.Length != 1)
        {
            throw new DebugSetupException(callableMatches.Length == 0
                ? $"VBA debug procedure '{moduleName}.{procedureName}' was not found in the selected document source snapshot."
                : $"VBA debug procedure '{moduleName}.{procedureName}' is ambiguous in the selected document source snapshot.");
        }

        return new ResolvedDebugTarget(moduleMatches[0], callableMatches[0]);
    }

    private static ResolvedDebugTarget ResolveActiveTarget(
        IReadOnlyList<ParsedDebugSource> parsedSources,
        DebugSourcePosition? activeSource)
    {
        if (activeSource is null)
        {
            throw new DebugSetupException(
                "The VBA launch request requires 'sourceSnapshot.activeSource' when module and procedure are omitted.");
        }

        if (!Path.IsPathFullyQualified(activeSource.Path))
        {
            throw new DebugSetupException(
                $"Active VBA source path must be absolute: '{activeSource.Path}'.");
        }

        var activePath = Path.GetFullPath(activeSource.Path);
        if (!activeSource.Path.Equals(activePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"Active VBA source path is not canonical: '{activeSource.Path}'. Expected '{activePath}'.");
        }

        var sourceMatches = parsedSources
            .Where(source => Path.GetFullPath(source.Source.Path).Equals(
                activePath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Active VBA source '{activeSource.Path}' is not present in the selected document source snapshot."
                : $"Active VBA source '{activeSource.Path}' is ambiguous in the selected document source snapshot.");
        }

        var source = sourceMatches[0];
        var lines = source.SyntaxTree.SourceText.Lines;
        if (activeSource.Line < 0 || activeSource.Line >= lines.Count)
        {
            throw new DebugSetupException(
                $"Active VBA source line {activeSource.Line} is outside '{activeSource.Path}'.");
        }

        var line = lines[activeSource.Line];
        if (activeSource.Character < 0 || activeSource.Character > line.Text.Length)
        {
            throw new DebugSetupException(
                $"Active VBA source character {activeSource.Character} is outside line {activeSource.Line} in '{activeSource.Path}'.");
        }

        var offset = line.StartOffset + activeSource.Character;
        var callableMatches = source.SyntaxTree.Module.CallableDeclarations
            .Where(callable => callable.BlockRange.Start.Offset <= offset && offset <= callable.BlockRange.End.Offset)
            .ToArray();
        if (callableMatches.Length != 1)
        {
            throw new DebugSetupException(callableMatches.Length == 0
                ? $"Active VBA position {activeSource.Line}:{activeSource.Character} is not inside a procedure in '{activeSource.Path}'."
                : $"Active VBA position {activeSource.Line}:{activeSource.Character} is ambiguous in '{activeSource.Path}'.");
        }

        return new ResolvedDebugTarget(source, callableMatches[0]);
    }

    private sealed record ParsedDebugSource(
        DebugSourceFileSnapshot Source,
        VbaSyntaxTree SyntaxTree);

    private sealed record ResolvedDebugTarget(
        ParsedDebugSource Source,
        VbaCallableDeclarationSyntax Callable);
}
