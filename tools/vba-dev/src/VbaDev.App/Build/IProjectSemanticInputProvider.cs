using VbaDev.App.Projects;
using VbaTools.Semantics;
using VbaTools.Syntax;

namespace VbaDev.App.Build;

/// <summary>Acquires required semantic evidence for the invocation's captured inputs.</summary>
public interface IProjectSemanticInputProvider
{
    Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context,
        CapturedWorkbookTemplate template, IReadOnlyList<VbaSyntaxTree> sources,
        CancellationToken cancellationToken);
}
