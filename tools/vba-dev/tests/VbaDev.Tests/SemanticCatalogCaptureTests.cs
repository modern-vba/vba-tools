using VbaTools.Semantics;
using Xunit;

namespace VbaDev.Tests;

public sealed class SemanticCatalogCaptureTests
{
    [Fact]
    public void AcceptedCatalogKeepsItsCallableAndQualifierFactsAfterTheProviderMutatesItsLists()
    {
        var aliases = new List<string> { "Example" };
        var parameters = new List<VbaCallableParameter>
        {
            new("value", TypeReference: new("Long"), IsByRef: true)
        };
        var definitions = new List<VbaProjectReferenceDefinition>
        {
            new("Example Library", "AcceptValue", VbaSourceDefinitionKind.Procedure,
                Signature: new("Sub AcceptValue(ByRef value As Long)", parameters,
                    CallableKind: VbaCallableKind.Sub),
                GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
        };
        var catalog = VbaProjectReferenceCatalogSet.Empty.WithCatalog(
            new("Example Library", aliases, definitions));
        var selection = VbaReferenceSelection.Capture(["Example Library"], null);
        var expected = catalog.GetActiveDefinitions(selection).Select(Snapshot).ToArray();
        Assert.NotEmpty(expected);

        parameters[0] = parameters[0] with { TypeReference = new("String"), IsByRef = false };
        aliases.Clear();
        definitions.Clear();

        Assert.Equal(expected, catalog.GetActiveDefinitions(selection).Select(Snapshot).ToArray());
    }

    private static string Snapshot(VbaSourceDefinition definition)
        => string.Join("|", definition.Name, definition.ModuleName,
            definition.Signature?.Label,
            string.Join(",", definition.Signature?.Parameters.Select(parameter =>
                $"{parameter.Name}:{parameter.TypeReference?.Name}:{parameter.IsByRef}") ?? []));
}
