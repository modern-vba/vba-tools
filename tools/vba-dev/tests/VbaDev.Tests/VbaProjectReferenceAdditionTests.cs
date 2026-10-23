using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaProjectReferenceAdditionTests
{
    [Fact]
    public async Task PreparationOwnsCopiesAndPlanningUsesLatestOrderWithoutMergingGuidAliases()
    {
        var requirements = new List<string> { "Alpha", "Beta" };
        var document = ProjectDocument.CreateExcel("Book");
        var results = new List<ResolvedVbaProjectReference> { Reference("ALPHA"), Reference("Beta") };
        var resolver = new DelegateResolver(names =>
        {
            requirements.Clear();
            document.References.Add(new("Beta"));
            return Batch(names, results);
        });
        var evidence = await Prepare(document, requirements, resolver);
        results.Clear();
        document.References.Clear();
        var plan = evidence.PlanAddition("Book", document, Root, ["Beta", "Alpha"],
            VbaProjectReferenceAddition.Intent.Explicit);
        Assert.Equal(["Alpha", "Beta"], evidence.Requirements.ToArray());
        Assert.Equal(["Beta", "ALPHA"], plan.Selection.Select(reference => reference.Name));
        Assert.All(plan.Selection, reference => Assert.True(reference.Requested));
    }

    [Fact]
    public async Task StillRequiredRemovedSelectionsFailButDiscardedEvidenceDoesNotValidateTemplate()
    {
        var document = ProjectDocument.CreateExcel("Book", references: [new("Selected", false)]);
        var evidence = await Prepare(document, ["Selected", "Alpha"],
            new FakeVbaProjectReferenceResolver(Reference("Alpha")));
        document.References.Clear();
        Assert.Equal(VbaProjectReferenceAddition.Reason.SelectionChanged,
            Assert.Throws<VbaProjectReferenceAddition.PlanException>(() => evidence.PlanAddition(
                "Book", document, Root, ["Selected"], VbaProjectReferenceAddition.Intent.Explicit)).FailureReason);
        var latest = document with { TemplatePath = "different.xlsm" };
        latest.References.Add(new("alpha", false));
        var plan = evidence.PlanAddition("Book", latest, Root, ["ALPHA"],
            VbaProjectReferenceAddition.Intent.Explicit);
        Assert.Equal("promoted", Assert.Single(plan.Changes).Status);
        Assert.Equal("alpha", Assert.Single(plan.Selection).Name);
        Assert.True(Assert.Single(plan.Selection).Requested);
    }

    [Fact]
    public async Task EvidenceCannotCrossDocumentsOrJustifyNewNames()
    {
        var document = ProjectDocument.CreateExcel("Book");
        var evidence = await Prepare(document, ["Alpha"], new FakeVbaProjectReferenceResolver(Reference("Alpha")));
        Assert.Equal(VbaProjectReferenceAddition.Reason.DocumentChanged,
            Assert.Throws<VbaProjectReferenceAddition.PlanException>(() => evidence.PlanAddition(
                "Other", document, Root, ["Alpha"], VbaProjectReferenceAddition.Intent.Explicit)).FailureReason);
        Assert.Equal(VbaProjectReferenceAddition.Reason.MissingEvidence,
            Assert.Throws<VbaProjectReferenceAddition.PlanException>(() => evidence.PlanAddition(
                "Book", document, Root, ["Beta"], VbaProjectReferenceAddition.Intent.Explicit)).FailureReason);
        Assert.Empty(document.References);
    }

    [Fact]
    public async Task ResolverCannotSubstituteAnotherName()
    {
        var resolver = new DelegateResolver(names => new(true, [], null,
            [new(names[0], "Wrong", true, [Reference("Wrong")])]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Prepare(ProjectDocument.CreateExcel("Book"), ["Alpha"], resolver));
        Assert.Contains("requested name 'Alpha'", error.Message);
    }

    [Fact]
    public async Task PreparationCopiesCallerRequirementsBeforeResolverCanMutateThem()
    {
        var names = new List<string> { "Alpha" };
        var resolver = new DelegateResolver(requested =>
        {
            names[0] = "Foreign";
            return Batch(requested, [Reference("Alpha")]);
        });
        var evidence = await Prepare(ProjectDocument.CreateExcel("Book"), names, resolver);
        Assert.Equal(["Alpha"], evidence.Requirements.ToArray());
    }

    private static readonly string Root = Path.GetFullPath(Path.GetTempPath());
    private static ResolvedVbaProjectReference Reference(string name)
        => new(name, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 1, 0);
    private static VbaProjectReferenceResolutionBatch Batch(
        IReadOnlyList<string> names, IReadOnlyList<ResolvedVbaProjectReference> references)
        => new(true, [], null, names.Select((name, index) =>
            new VbaProjectReferenceNameResolution(name, references[index].Name, true, [references[index]])).ToArray());
    private static Task<VbaProjectReferenceAddition> Prepare(ProjectDocument document,
        IReadOnlyList<string> names, IVbaProjectReferenceResolver resolver)
        => VbaProjectReferenceAddition.PrepareEvidenceAsync("Book", document,
            Path.GetFullPath(document.TemplatePath, Root), names,
            new VbaProjectReferencePlanner(resolver), new Paths(), CancellationToken.None);
    private sealed class Paths : IFileSystemPathIdentityResolver
    {
        public FileSystemPathIdentity Resolve(string path) => new(Path.GetFullPath(path), path, null);
    }
    private sealed class DelegateResolver(
        Func<IReadOnlyList<string>, VbaProjectReferenceResolutionBatch> resolve) : IVbaProjectReferenceResolver
    {
        public VbaProjectReferenceResolutionBatch Resolve(IReadOnlyList<string> names) => resolve(names);
        public VbaProjectReferenceResolutionBatch ResolveAvailable() => throw new NotSupportedException();
    }
}
