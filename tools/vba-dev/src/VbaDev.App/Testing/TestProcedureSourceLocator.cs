using VbaDev.App.Workbooks;
using VbaLanguageServer.Syntax;

namespace VbaDev.App.Testing;

/// <summary>
/// Resolves completed workbook test identities to exported VBA declarations.
/// </summary>
public sealed class TestProcedureSourceLocator
{
    /// <summary>
    /// Adds a source location to each result whose module and procedure resolve uniquely.
    /// </summary>
    /// <param name="sourceSetPath">The document source set to inspect.</param>
    /// <param name="results">The completed workbook test results.</param>
    /// <returns>The results enriched with optional declaration locations.</returns>
    public IReadOnlyList<TestResultRecord> Locate(
        string sourceSetPath,
        IReadOnlyList<TestResultRecord> results)
    {
        IReadOnlyList<string> sourcePaths;
        try
        {
            sourcePaths = DocumentSourceSetLayout.EnumerateVbaSourcePaths(sourceSetPath);
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        var modules = new List<ParsedTestModule>();
        foreach (var sourcePath in sourcePaths)
        {
            try
            {
                modules.Add(ParseModule(sourcePath));
            }
            catch (IOException)
            {
                // Source navigation is optional metadata and must not replace a completed outcome.
            }
            catch (UnauthorizedAccessException)
            {
                // Source navigation is optional metadata and must not replace a completed outcome.
            }
        }

        return results
            .Select(result => result with
            {
                Location = Resolve(modules, result.Category, result.TestName)
            })
            .ToArray();
    }

    private static ParsedTestModule ParseModule(string sourcePath)
    {
        var uri = new Uri(Path.GetFullPath(sourcePath)).AbsoluteUri;
        var tree = VbaSyntaxTree.ParseModule(uri, File.ReadAllText(sourcePath));
        return new ParsedTestModule(uri, tree);
    }

    private static TestProcedureSourceLocation? Resolve(
        IReadOnlyList<ParsedTestModule> modules,
        string moduleName,
        string procedureName)
    {
        var moduleMatches = modules
            .Where(module => module.Tree.Module.Identity.Name.Equals(
                moduleName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (moduleMatches.Length != 1)
        {
            return null;
        }

        var procedureMatches = moduleMatches[0].Tree.Module.CallableDeclarations
            .Where(declaration => declaration.Name.Equals(
                procedureName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (procedureMatches.Length != 1)
        {
            return null;
        }

        var range = procedureMatches[0].Range;
        return new TestProcedureSourceLocation(
            moduleMatches[0].Uri,
            new TestProcedureSourceRange(
                new TestProcedureSourcePosition(range.Start.Line, range.Start.Character),
                new TestProcedureSourcePosition(range.End.Line, range.End.Character)));
    }

    private sealed record ParsedTestModule(string Uri, VbaSyntaxTree Tree);
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
