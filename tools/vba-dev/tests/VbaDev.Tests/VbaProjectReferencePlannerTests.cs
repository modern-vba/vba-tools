using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaProjectReferencePlannerTests
{
    [Fact]
    public void ManifestInputResolutionFailsClosedWhenTheRegistryCatalogIsIncomplete()
    {
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Known Partial Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0))
        {
            Complete = false,
            Diagnostic = new TypeLibRegistryCatalogDiagnostic(
                "registryCatalogIncomplete",
                "TypeLib registry enumeration did not complete.")
        };
        var planner = new VbaProjectReferencePlanner(resolver);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            planner.ResolveManifestInputReferences(["Known Partial Library"]));

        Assert.Contains("registryCatalogIncomplete", exception.Message, StringComparison.Ordinal);
    }
}
