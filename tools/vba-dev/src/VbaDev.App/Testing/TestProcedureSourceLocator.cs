using System.Collections.Immutable;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Testing;

/// <summary>
/// Creates and queries immutable indexes for the source admitted into a tested workbook.
/// </summary>
public sealed class TestProcedureSourceLocator
{
    internal ExecutedSourceIndex CreateIndex(
        AdmittedVbaSourceSet admission,
        string admittedSourceRoot,
        string persistentSourceRoot)
        => ExecutedSourceIndex.Create(
            admission,
            admittedSourceRoot,
            persistentSourceRoot);

    internal IReadOnlyList<TestResultRecord> Locate(
        ExecutedSourceIndex index,
        IReadOnlyList<TestResultRecord> results)
        => index.Locate(results);
}

/// <summary>
/// Holds only immutable navigation facts copied from the source capture that produced a workbook.
/// </summary>
internal sealed class ExecutedSourceIndex
{
    private readonly ImmutableArray<ExecutedSourceModule> modules;

    private ExecutedSourceIndex(IEnumerable<ExecutedSourceModule> modules)
    {
        this.modules = modules.ToImmutableArray();
    }

    internal static ExecutedSourceIndex Create(
        AdmittedVbaSourceSet admission,
        string admittedSourceRoot,
        string persistentSourceRoot)
        => new(admission.Sources.Select(source => new ExecutedSourceModule(
            source.Syntax.Module.Identity.Name,
            TryMapPersistentUri(
                admittedSourceRoot,
                persistentSourceRoot,
                source.SourcePath),
            source.Syntax.Module.CallableDeclarations
                .Select(declaration => new ExecutedSourceProcedure(
                    declaration.Name,
                    new TestProcedureSourceRange(
                        new TestProcedureSourcePosition(
                            declaration.Range.Start.Line,
                            declaration.Range.Start.Character),
                        new TestProcedureSourcePosition(
                            declaration.Range.End.Line,
                            declaration.Range.End.Character))))
                .ToImmutableArray())));

    internal IReadOnlyList<TestResultRecord> Locate(
        IReadOnlyList<TestResultRecord> results)
        => results.Select(result => result with
        {
            Location = Resolve(result.Category, result.TestName)
        }).ToArray();

    private TestProcedureSourceLocation? Resolve(
        string moduleName,
        string procedureName)
    {
        var moduleMatches = modules
            .Where(module => module.Name.Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (moduleMatches.Length != 1)
        {
            return null;
        }

        var module = moduleMatches[0];
        var procedureMatches = module.Procedures
            .Where(procedure => procedure.Name.Equals(
                procedureName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (procedureMatches.Length != 1 || module.Uri is null)
        {
            return null;
        }

        return new TestProcedureSourceLocation(
            module.Uri,
            procedureMatches[0].Range);
    }

    private static string? TryMapPersistentUri(
        string admittedSourceRoot,
        string persistentSourceRoot,
        string admittedSourcePath)
    {
        try
        {
            var admittedRoot = Path.GetFullPath(admittedSourceRoot);
            var relativePath = Path.GetRelativePath(
                admittedRoot,
                Path.GetFullPath(admittedSourcePath));
            if (!IsSafeRelativePath(relativePath))
            {
                return null;
            }

            var persistentRoot = Path.GetFullPath(persistentSourceRoot);
            var persistentPath = Path.GetFullPath(Path.Combine(
                persistentRoot,
                relativePath));
            var persistentRelativePath = Path.GetRelativePath(
                persistentRoot,
                persistentPath);
            return IsSafeRelativePath(persistentRelativePath)
                ? new Uri(persistentPath).AbsoluteUri
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return null;
        }
    }

    private static bool IsSafeRelativePath(string relativePath)
        => !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private sealed record ExecutedSourceModule(
        string Name,
        string? Uri,
        ImmutableArray<ExecutedSourceProcedure> Procedures);

    private sealed record ExecutedSourceProcedure(
        string Name,
        TestProcedureSourceRange Range);
}

/// <summary>
/// Identifies an exported VBA test procedure declaration.
/// </summary>
/// <param name="Uri">The exported source file URI.</param>
/// <param name="Range">The half-open declaration-name range.</param>
public sealed record TestProcedureSourceLocation(
    string Uri,
    TestProcedureSourceRange Range);

/// <summary>
/// Represents a half-open exported-source range.
/// </summary>
/// <param name="Start">The inclusive start position.</param>
/// <param name="End">The exclusive end position.</param>
public sealed record TestProcedureSourceRange(
    TestProcedureSourcePosition Start,
    TestProcedureSourcePosition End);

/// <summary>
/// Represents a zero-based UTF-16 source position.
/// </summary>
/// <param name="Line">The zero-based physical line.</param>
/// <param name="Character">The zero-based UTF-16 character offset.</param>
public sealed record TestProcedureSourcePosition(int Line, int Character);
