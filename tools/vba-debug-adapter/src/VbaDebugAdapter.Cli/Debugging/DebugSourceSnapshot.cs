using System.Collections.Immutable;
using VbaLanguageServer.Syntax;

namespace VbaDebugAdapter.Debugging;

public sealed record DebugSourceFileSnapshot(
    string RelativePath,
    string SourceUri,
    string Text);

public sealed record DebugSourcePosition(
    string SourceUri,
    int Line,
    int Character);

public sealed record DebugSourceBreakpoint(
    string SourceUri,
    int EditorLine);

public sealed record DebugSourceSnapshot(
    int SchemaVersion,
    ImmutableArray<DebugSourceFileSnapshot> Sources,
    DebugSourcePosition? ActiveSource)
{
    public const int CurrentSchemaVersion = 1;

    public ImmutableArray<DebugSourceBreakpoint> Breakpoints { get; init; } = [];
}

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

public sealed record DebugLaunchRequest(
    DebugTargetProcedure Target,
    DebugSourceSnapshot SourceSnapshot);

public sealed class DebugLaunchRequestResolver
{
    public DebugLaunchRequest Resolve(
        DebugSourceSnapshot sourceSnapshot,
        string? moduleName,
        string? procedureName)
    {
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
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

        ValidateSnapshot(sourceSnapshot);
        var parsedSources = sourceSnapshot.Sources
            .Select(source => new ParsedDebugSource(
                source,
                VbaSyntaxTree.ParseModule(source.SourceUri, source.Text)))
            .ToArray();
        var target = hasModule
            ? ResolveExplicitTarget(parsedSources, moduleName!, procedureName!)
            : ResolveActiveTarget(parsedSources, sourceSnapshot.ActiveSource);
        ValidateTargetUniquenessAndEligibility(parsedSources, target);

        if (!VbaConditionalCompilationBranchFacts.TryGetPath(
                target.Source.SyntaxTree,
                target.Callable.Range,
                requireCompleteStructure: true,
                out var conditionalCompilationPath))
        {
            throw new DebugSetupException(
                $"VBA debug target '{target.Source.SyntaxTree.Module.Identity.Name}." +
                $"{target.Callable.Name}' has no complete conditional-compilation branch identity.");
        }

        return new DebugLaunchRequest(
            new DebugTargetProcedure(
                target.Source.SyntaxTree.Module.Identity.Name,
                target.Callable.Name)
            {
                ConditionalCompilationPath = conditionalCompilationPath
            },
            sourceSnapshot);
    }

    private static void ValidateSnapshot(DebugSourceSnapshot sourceSnapshot)
    {
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedPaths = new List<string>(sourceSnapshot.Sources.Length);
        foreach (var source in sourceSnapshot.Sources)
        {
            var relativePath = ValidateRelativePath(source.RelativePath);
            if (!relativePaths.Add(relativePath))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot contains duplicate path '{relativePath}'.");
            }
            if (!flatNames.Add(Path.GetFileName(relativePath)))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot contains duplicate flat source identity " +
                    $"'{Path.GetFileName(relativePath)}'.");
            }

            var extension = Path.GetExtension(relativePath);
            if (!new[] { ".bas", ".cls", ".frm" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path '{relativePath}' is not a .bas, .cls, or .frm source file.");
            }
            if (!Uri.TryCreate(source.SourceUri, UriKind.Absolute, out var uri) || !uri.IsFile)
            {
                throw new DebugSetupException(
                    $"Debug source snapshot path '{relativePath}' requires a persistent file URI.");
            }
            if (!sourceUris.Add(uri.AbsoluteUri))
            {
                throw new DebugSetupException(
                    $"Debug source snapshot contains duplicate URI '{source.SourceUri}'.");
            }
            orderedPaths.Add(relativePath);
        }

        if (!orderedPaths.SequenceEqual(
                orderedPaths.OrderBy(path => path, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new DebugSetupException(
                "Debug source snapshot sources must be supplied in canonical relative-path order.");
        }
    }

    private static string ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new DebugSetupException(
                $"Debug source snapshot path must be relative: '{relativePath}'.");
        }
        var portablePath = relativePath.Replace('\\', '/');
        var segments = portablePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new DebugSetupException(
                $"Debug source snapshot path must be a safe descendant: '{relativePath}'.");
        }
        return portablePath;
    }

    private static void ValidateTargetUniquenessAndEligibility(
        IReadOnlyList<ParsedDebugSource> parsedSources,
        ResolvedDebugTarget target)
    {
        var moduleName = target.Source.SyntaxTree.Module.Identity.Name;
        var procedureName = target.Callable.Name;
        if (parsedSources.Count(source => source.SyntaxTree.Module.Identity.Name.Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new DebugSetupException(
                $"VBA debug module '{moduleName}' is ambiguous in the selected document source snapshot.");
        }
        if (target.Source.SyntaxTree.Module.CallableDeclarations.Count(callable =>
                callable.Name.Equals(procedureName, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new DebugSetupException(
                $"VBA debug procedure '{moduleName}.{procedureName}' is ambiguous " +
                "in the selected document source snapshot.");
        }
        if (target.Source.SyntaxTree.Module.Kind != VbaModuleKind.StandardModule)
        {
            throw new DebugSetupException(
                $"VBA debug module '{moduleName}' is not a standard module; " +
                "class, form, and document modules cannot contain a debug target.");
        }
        if (!string.Equals(
                target.Callable.DeclarationKeyword,
                "Sub",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not a Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        var publicKeyword = string.IsNullOrEmpty(target.Callable.VisibilityKeyword) ||
            target.Callable.VisibilityKeyword.Equals("Public", StringComparison.OrdinalIgnoreCase);
        if (target.Callable.Visibility != VbaDeclarationVisibility.Public || !publicKeyword)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not public; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        if (target.Callable.Parameters.Count != 0)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is not parameterless; " +
                "the target must be a public parameterless Sub in a standard module.");
        }
        if (target.Callable.IsExternal)
        {
            throw new DebugSetupException(
                $"VBA debug target '{moduleName}.{procedureName}' is an external Declare Sub; " +
                "the target must be a public parameterless Sub in a standard module.");
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
            .Where(callable => callable.Name.Equals(
                procedureName,
                StringComparison.OrdinalIgnoreCase))
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
                "The VBA launch request requires 'sourceSnapshot.activeSource' " +
                "when module and procedure are omitted.");
        }

        var sourceMatches = parsedSources
            .Where(source => source.Source.SourceUri.Equals(
                activeSource.SourceUri,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (sourceMatches.Length != 1)
        {
            throw new DebugSetupException(sourceMatches.Length == 0
                ? $"Active VBA source '{activeSource.SourceUri}' is not present in the selected document source snapshot."
                : $"Active VBA source '{activeSource.SourceUri}' is ambiguous in the selected document source snapshot.");
        }

        var source = sourceMatches[0];
        var lines = source.SyntaxTree.SourceText.Lines;
        if (activeSource.Line < 0 || activeSource.Line >= lines.Count)
        {
            throw new DebugSetupException(
                $"Active VBA source line {activeSource.Line} is outside '{activeSource.SourceUri}'.");
        }
        var line = lines[activeSource.Line];
        if (activeSource.Character < 0 || activeSource.Character > line.Text.Length)
        {
            throw new DebugSetupException(
                $"Active VBA source character {activeSource.Character} is outside line " +
                $"{activeSource.Line} in '{activeSource.SourceUri}'.");
        }

        var offset = line.StartOffset + activeSource.Character;
        var callableMatches = source.SyntaxTree.Module.CallableDeclarations
            .Where(callable => callable.BlockRange.Start.Offset <= offset &&
                               offset <= callable.BlockRange.End.Offset)
            .ToArray();
        if (callableMatches.Length != 1)
        {
            throw new DebugSetupException(callableMatches.Length == 0
                ? $"Active VBA position {activeSource.Line}:{activeSource.Character} is not inside a procedure in '{activeSource.SourceUri}'."
                : $"Active VBA position {activeSource.Line}:{activeSource.Character} is ambiguous in '{activeSource.SourceUri}'.");
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
