using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class EffectiveDeclaredTypeTests
{
    [Theory]
    [InlineData("rhs As", "", "InsufficientEvidence", "Known")]
    [InlineData("rhs", "As", "Known", "InsufficientEvidence")]
    [InlineData("rhs As MissingType", "As MissingType", "ExplicitlyUnresolved", "ExplicitlyUnresolved")]
    public void Unfinished_parameter_and_return_clauses_are_not_implicit_types(
        string parameter, string returnClause, string expectedParameterState, string expectedReturnState)
    {
        const string uri = "file:///C:/work/Incomplete.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = $"Attribute VB_Name = \"Incomplete\"\nDefLng R\nPublic Function Result({parameter}) {returnClause}\nEnd Function"
        });
        var names = new VbaNameResolutionService(documents.Values.ToArray(), null, VbaProjectReferenceCatalogSet.Empty);
        var definition = Assert.Single(documents[uri].Definitions,
            candidate => candidate.Name == "Result" && candidate.Kind == VbaSourceDefinitionKind.Procedure);

        Assert.Equal(expectedParameterState, names.EffectiveDeclaredTypes.Get(definition, 0).State.ToString());
        Assert.Equal(expectedReturnState, names.EffectiveDeclaredTypes.Get(definition).State.ToString());
    }

    [Theory]
    [InlineData("DefLng R\nPublic rhs", "Known", "Long")]
    [InlineData("DefLng R\nPublic rhs As String", "Known", "String")]
    [InlineData("DefLng R\nPublic rhs%", "Known", "Integer")]
    [InlineData("Public rhs", "Known", "Variant")]
    [InlineData("Public rhs As MissingType", "ExplicitlyUnresolved", "MissingType")]
    [InlineData("Public rhs As", "InsufficientEvidence", null)]
    [InlineData("#If FEATURE Then\nDefLng R\n#End If\nPublic rhs", "InsufficientEvidence", null)]
    [InlineData("Public rhs\nDefLng R", "Known", "Variant")]
    [InlineData("#If FEATURE Then\nDefStr R\n#End If\nDefLng R\nPublic rhs", "Known", "Long")]
    [InlineData("#If FEATURE Then\nPublic rhs", "InsufficientEvidence", null)]
    public void Written_types_defaults_and_incomplete_evidence_remain_distinct(
        string declaration, string expectedState, string? expectedType)
    {
        const string uri = "file:///C:/work/EffectiveEvidence.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Attribute VB_Name = \"EffectiveEvidence\"\n" + declaration
        });
        var names = new VbaNameResolutionService(documents.Values.ToArray(), null,
            VbaProjectReferenceCatalogSet.Empty);
        var definition = Assert.Single(documents[uri].Definitions,
            candidate => candidate.Name.Equals("rhs", StringComparison.OrdinalIgnoreCase));

        var result = names.EffectiveDeclaredTypes.Get(definition);

        Assert.Equal(expectedState, result.State.ToString());
        Assert.Equal(expectedType, result.Reference?.Name);
    }

    [Theory]
    [InlineData("Function", "End Function", "Known", "Long")]
    [InlineData("Property Get", "End Property", "Known", "Long")]
    [InlineData("Property Let", "End Property", "NoReturnType", null)]
    [InlineData("Property Set", "End Property", "NoReturnType", null)]
    [InlineData("Sub", "End Sub", "NoReturnType", null)]
    [InlineData("Event", "", "NoReturnType", null)]
    public void Parameter_slots_and_return_presence_are_independent(
        string kind, string terminator, string expectedState, string? expectedReturn)
    {
        const string uri = "file:///C:/work/Owner.cls";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = $"VERSION 1.0 CLASS\nAttribute VB_Name = \"Owner\"\nDefLng R\nPublic {kind} Result(rhs)\n{terminator}"
        });
        var names = new VbaNameResolutionService(documents.Values.ToArray(), null, VbaProjectReferenceCatalogSet.Empty);
        var declaration = Assert.Single(documents[uri].Definitions,
            candidate => candidate.Name == "Result" && candidate.Kind != VbaSourceDefinitionKind.Parameter);
        Assert.Equal("Long", names.EffectiveDeclaredTypes.Get(declaration, 0).Reference?.Name);
        var result = names.EffectiveDeclaredTypes.Get(declaration);
        Assert.Equal(expectedState, result.State.ToString());
        Assert.Equal(expectedReturn, result.Reference?.Name);
    }

    [Fact]
    public void Physical_conditional_variants_keep_their_own_directives_and_identities()
    {
        const string uri = "file:///C:/work/Variants.bas";
        const string otherUri = "file:///C:/work/Other.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Attribute VB_Name = \"Variants\"\n#If FEATURE Then\nDefLng R\nPublic rhs\n#Else\nDefStr R\nPublic rhs\n#End If",
            [otherUri] = "Attribute VB_Name = \"Other\"\nPublic rhs"
        });
        var names = new VbaNameResolutionService(documents.Values.ToArray(), null, VbaProjectReferenceCatalogSet.Empty);
        var variants = documents[uri].Definitions.Where(definition => definition.Name == "rhs").ToArray();
        Assert.Equal(2, variants.Length);
        Assert.NotEqual(variants[0].Identity, variants[1].Identity);
        Assert.Equal(new[] { "Long", "String" }, variants.Select(definition => names.EffectiveDeclaredTypes.Get(definition).Reference?.Name));
        var other = Assert.Single(documents[otherUri].Definitions, definition => definition.Name == "rhs");
        Assert.Equal("Variant", names.EffectiveDeclaredTypes.Get(other).Reference?.Name);
    }

    [Fact]
    public void Missing_external_parameter_and_return_metadata_receive_no_source_defaults()
    {
        var range = new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, 6));
        var foreign = new VbaSourceDefinition(
            VbaDefinitionIdentity.ForProjectReference("Library", "Owner", VbaSourceDefinitionKind.Procedure, "Result"),
            new VbaDefinitionLocation("vba-reference:///Library/Owner/Result", range),
            "Result", VbaSourceDefinitionKind.Procedure, VbaSourceDefinitionVisibility.Public, "Library",
            Signature: new VbaCallableSignature("Function Result(rhs)", [new VbaCallableParameter("rhs")], CallableKind: VbaCallableKind.Function),
            CallableKind: VbaCallableKind.Function);
        var names = new VbaNameResolutionService([], null, VbaProjectReferenceCatalogSet.Empty);

        Assert.Equal(VbaEffectiveDeclaredTypeState.InsufficientEvidence, names.EffectiveDeclaredTypes.Get(foreign).State);
        Assert.Equal(VbaEffectiveDeclaredTypeState.InsufficientEvidence, names.EffectiveDeclaredTypes.Get(foreign, 0).State);
        Assert.DoesNotContain("Variant", names.EffectiveDeclaredTypes.Project(foreign).Signature!.Label);
    }

    [Fact]
    public void Comma_separated_declarations_keep_their_own_type_and_array_shape()
    {
        const string uri = "file:///C:/work/Declarators.bas";
        var documents = VbaSemanticInventoryFixture.ProjectSourceDocuments(new Dictionary<string, string>
        {
            [uri] = "Attribute VB_Name = \"Declarators\"\nDefLng R\nPublic rhs(), other As String, rest"
        });
        var names = new VbaNameResolutionService(documents.Values.ToArray(), null, VbaProjectReferenceCatalogSet.Empty);
        var variables = documents[uri].Definitions.Where(definition => definition.Kind == VbaSourceDefinitionKind.Variable).ToArray();
        Assert.Equal(new[] { "Long", "String", "Long" }, variables.Select(definition => names.EffectiveDeclaredTypes.Get(definition).Reference?.Name));
        Assert.Equal(new[] { true, false, false }, variables.Select(definition => definition.IsArray));
    }
}
