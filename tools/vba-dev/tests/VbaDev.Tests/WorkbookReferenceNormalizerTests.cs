using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookReferenceNormalizerTests
{
    [Theory]
    [InlineData(false, false, "Visual Basic For Applications")]
    [InlineData(false, true, "Visual Basic For Applications")]
    [InlineData(true, false, "Visual Basic For Applications")]
    [InlineData(true, true, "Visual Basic For Applications")]
    [InlineData(false, false, "vIsUaL bAsIc FoR aPpLiCaTiOnS")]
    [InlineData(false, true, "vIsUaL bAsIc FoR aPpLiCaTiOnS")]
    [InlineData(true, false, "vIsUaL bAsIc FoR aPpLiCaTiOnS")]
    [InlineData(true, true, "vIsUaL bAsIc FoR aPpLiCaTiOnS")]
    [InlineData(false, false, " \tVisual Basic For Applications\r\n")]
    [InlineData(false, true, " \tVisual Basic For Applications\r\n")]
    [InlineData(true, false, " \tVisual Basic For Applications\r\n")]
    [InlineData(true, true, " \tVisual Basic For Applications\r\n")]
    public async Task NormalizationRetainsStandardLibraryWithoutWarningOrRemoval(
        bool asynchronous, bool isRemovable, string name)
    {
        var standardLibrary = new WorkbookReference(name, IsRemovable: isRemovable);
        var session = new ReferenceSession(standardLibrary);
        var normalizer = new WorkbookReferenceNormalizer(
            new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver()));

        var warnings = asynchronous
            ? await normalizer.NormalizeAsync(session, "Book1", [], CancellationToken.None)
            : normalizer.Normalize(session, "Book1", []);

        Assert.Empty(warnings);
        Assert.Empty(session.RemovalAttempts);
        Assert.Equal([standardLibrary], session.GetReferences());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalizationPreservesOtherReferenceWarningsAndReconcilesSelection(bool asynchronous)
    {
        var standardLibrary = new WorkbookReference("Visual Basic For Applications", false);
        var protectedLibrary = new WorkbookReference("Protected Library", false);
        var failedRemoval = new WorkbookReference("Rejected Removal", true);
        var obsolete = new WorkbookReference("Obsolete Library", true);
        var selected = new WorkbookReference("Selected Library", true);
        var session = new ReferenceSession(standardLibrary, protectedLibrary, failedRemoval, obsolete, selected);
        session.RejectedRemovals.Add(failedRemoval.Name);
        var missing = new ResolvedVbaProjectReference(
            "Missing Library", "11111111-1111-1111-1111-111111111111", 1, 0);
        var normalizer = new WorkbookReferenceNormalizer(
            new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver(missing)));
        VbaProjectReference[] desired = [new(selected.Name), new(missing.Name)];

        var warnings = asynchronous
            ? await normalizer.NormalizeAsync(session, "Book1", desired, CancellationToken.None)
            : normalizer.Normalize(session, "Book1", desired);

        Assert.Equal(
            ["[WARN] VbaProjectReferences (Book1/Protected Library): Unlisted protected reference remains.",
             "[WARN] VbaProjectReferences (Book1/Rejected Removal): Unlisted protected reference remains."],
            warnings);
        Assert.Equal([failedRemoval.Name, obsolete.Name], session.RemovalAttempts);
        Assert.Equal(
            [standardLibrary, protectedLibrary, failedRemoval, selected,
             new WorkbookReference(missing.Name, true, Guid: missing.Guid, Major: missing.Major, Minor: missing.Minor)],
            session.GetReferences());
    }

    private sealed class ReferenceSession(params WorkbookReference[] references)
        : IWorkbookBuildSession, IWorkbookGenerationSession
    {
        private readonly List<WorkbookReference> currentReferences = [.. references];
        public List<string> RemovalAttempts { get; } = [];
        public HashSet<string> RejectedRemovals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<WorkbookReference> GetReferences() => currentReferences.ToArray();

        public bool RemoveReference(string referenceName)
        {
            RemovalAttempts.Add(referenceName);
            if (RejectedRemovals.Contains(referenceName))
            {
                return false;
            }

            return currentReferences.RemoveAll(reference =>
                reference.IsRemovable && reference.Name == referenceName) > 0;
        }

        public void AddReference(ResolvedVbaProjectReference reference)
            => currentReferences.Add(new WorkbookReference(
                reference.Name, true, Guid: reference.Guid, Major: reference.Major, Minor: reference.Minor));

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => Task.FromResult(GetReferences());

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
            => Task.FromResult(RemoveReference(referenceName));

        public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
        {
            AddReference(reference);
            return Task.CompletedTask;
        }

        public string GetProjectName() => throw new NotSupportedException();
        public IReadOnlyList<WorkbookModule> GetModules() => throw new NotSupportedException();
        public void RemoveModule(string moduleName) => throw new NotSupportedException();
        public void ImportModule(VbeImportSourceFile sourceFile) => throw new NotSupportedException();
        public VbeImportVerificationReport VerifyImportedModules() => throw new NotSupportedException();
        public void Save() => throw new NotSupportedException();
        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
