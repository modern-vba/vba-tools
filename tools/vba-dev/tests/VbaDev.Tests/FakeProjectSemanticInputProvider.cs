using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaTools.Semantics;
using VbaTools.Syntax;

namespace VbaDev.Tests;

/// <summary>Explicit input acquisition for tests of source admission and materialization.</summary>
internal sealed class FakeProjectSemanticInputProvider(IVbaProjectReferenceResolver? resolver = null)
    : IProjectSemanticInputProvider
{
    internal static FakeProjectSemanticInputProvider Empty { get; } = new();

    public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context, CapturedWorkbookTemplate template,
        IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Document.References.Count == 0) return Task.FromResult(VbaProjectSemanticInputs.Empty);
        var names = context.Document.References.Select(reference => reference.Name).ToArray();
        var references = new VbaProjectReferencePlanner(resolver ?? new FakeVbaProjectReferenceResolver())
            .ResolveManifestInputReferences(names);
        var identities = references.ToDictionary(reference => reference.Name, reference =>
            new VbaProjectReferenceCatalogIdentity(reference.Name, reference.Guid, reference.Major, reference.Minor,
                0, "C:/test-acquisition/reference.dll"), VbaReferenceName.Comparer);
        return Task.FromResult(VbaProjectSemanticInputs.Capture(VbaReferenceSelection.Capture(names, null),
            VbaProjectReferenceCatalogSet.Empty, identities: identities));
    }
}
