using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ClassLifecycleCompletionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("cLa")]
    public void Sub_name_offers_fixed_prefix_without_environment_evidence(string fragment)
    {
        var result = Complete("Private Sub " + fragment + "|");

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Class_", candidate.Label);
        Assert.Equal("Class Lifecycle", candidate.Detail);
        Assert.Equal(VbaCompletionCandidateKind.ContractPrefix, candidate.Kind);
        Assert.True(candidate.RetriggerCompletion);
    }

    [Theory]
    [InlineData("Class_", "Initialize", "Terminate")]
    [InlineData("cLaSs_iN", "Initialize")]
    [InlineData("CLASS_tEr", "Terminate")]
    public void Members_present_canonical_contracts_but_edit_only_the_suffix(
        string fragment,
        params string[] names)
    {
        const string introducer = "Private Sub ";
        var result = Complete(introducer + fragment + "|");

        Assert.Equal(names.Select(name => "Class_" + name),
            result.Candidates.Select(candidate => candidate.Label));
        foreach (var candidate in result.Candidates)
        {
            var name = candidate.Label["Class_".Length..];
            Assert.Equal("Lifecycle Handler", candidate.Detail);
            Assert.Equal(VbaCompletionCandidateKind.ContractMemberName, candidate.Kind);
            Assert.False(candidate.RetriggerCompletion);
            Assert.Equal(name, candidate.FilterText);
            var edit = Assert.IsType<VbaTextEdit>(candidate.TextEdit);
            Assert.Equal(introducer.Length + "Class_".Length, edit.Range.Start.Character);
            Assert.Equal(introducer.Length + fragment.Length, edit.Range.End.Character);
            Assert.Equal(name, edit.NewText);
            Assert.Equal(introducer + fragment[.."Class_".Length] + name,
                (introducer + fragment)[..edit.Range.Start.Character] + edit.NewText);
            var signature = Assert.Single(candidate.SignaturePresentations);
            Assert.Equal("Sub Class_" + name + "()", signature.Label);
            Assert.False(signature.IsConditional);
            var documentation = Assert.Single(signature.DocumentationVariants);
            Assert.Contains(name == "Initialize" ? "created" : "destroyed", documentation);
            if (name == "Terminate")
            {
                Assert.Contains("not guaranteed", documentation);
                Assert.Contains("abnormally", documentation);
            }
        }
    }

    [Theory]
    [InlineData("Sub ")]
    [InlineData("Public Sub ")]
    [InlineData("Private Sub ")]
    [InlineData("Friend Sub ")]
    [InlineData("Static Sub ")]
    [InlineData("Public Static Sub ")]
    [InlineData("Private Static Sub ")]
    public void Every_shared_Sub_name_slot_admits_the_fixed_contract(string introducer)
    {
        Assert.Equal("Class_", Assert.Single(Complete(introducer + "|").Candidates).Label);
    }

    [Theory]
    [InlineData("bas", "Private Sub Class_|")]
    [InlineData("frm", "Private Sub Class_|")]
    [InlineData("cls", "Private Function Class_|")]
    [InlineData("cls", "Private Property Get Class_|")]
    [InlineData("cls", "Private Property Let Class_|")]
    [InlineData("cls", "Private Property Set Class_|")]
    [InlineData("cls", "Private Sub Run()\nClass_|\nEnd Sub")]
    [InlineData("cls", "Private Sub Run()\nDim value As Class_|\nEnd Sub")]
    [InlineData("cls", "' Private Sub Class_|")]
    public void Other_modules_callables_and_code_positions_have_no_lifecycle_candidates(
        string extension,
        string source)
    {
        Assert.DoesNotContain(Complete(source, extension).Candidates,
            candidate => candidate.Kind is VbaCompletionCandidateKind.ContractPrefix
                or VbaCompletionCandidateKind.ContractMemberName);
    }

    [Theory]
    [InlineData("Private Sub class_initialize()\nEnd Sub", "Class_", "Class_Terminate")]
    [InlineData("Private CLASS_TERMINATE As Long", "Class_", "Class_Initialize")]
    [InlineData("Private Class_Initialize As Long\nPrivate class_terminate As Long", "", "")]
    [InlineData("Private Class_Initialize As Long\nPrivate class_terminate As Long", "Class_", "")]
    public void Same_scope_occupied_names_filter_members_and_empty_prefixes(
        string declarations,
        string fragment,
        string expected)
    {
        var result = Complete(declarations + "\nPrivate Sub " + fragment + "|");
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries),
            result.Candidates.Select(candidate => candidate.Label));
    }

    [Theory]
    [InlineData("")]
    [InlineData("()")]
    public void Edited_handler_does_not_collide_with_itself(string parameterList)
    {
        var result = Complete("Private Sub cLaSs_InItIaLiZe|" + parameterList + "\nEnd Sub");
        Assert.Equal("Class_Initialize", Assert.Single(result.Candidates).Label);
    }

    [Theory]
    [InlineData("#If VBA7 Then\nPrivate Sub Class_Initialize()\nEnd Sub\nPrivate Sub Class_|\nEnd Sub\n#End If", true)]
    [InlineData("#If VBA7 Then\nPrivate Sub Class_Initialize()\nEnd Sub\n#Else\nPrivate Sub Class_|\nEnd Sub\n#End If", true)]
    [InlineData("Private Sub Class_Initialize()\nEnd Sub\n#If VBA7 Then\nPrivate Sub Class_|\nEnd Sub\n#End If", false)]
    [InlineData("#If VBA7 Then\nPrivate Sub Class_Initialize()\nEnd Sub\n#End If\nPrivate Sub Class_|", false)]
    public void Conditional_collisions_use_the_shared_family_policy_without_origin_markers(
        string source,
        bool initializeAvailable)
    {
        var result = Complete(source);
        Assert.Equal(initializeAvailable ? ["Class_Initialize", "Class_Terminate"] : new[] { "Class_Terminate" },
            result.Candidates.Select(candidate => candidate.Label));
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal("Lifecycle Handler", candidate.Detail);
            Assert.False(Assert.Single(candidate.SignaturePresentations).IsConditional);
        });
    }

    [Fact]
    public void Fixed_and_WithEvents_origins_coalesce_without_losing_contracts()
    {
        const string uri = "file:///C:/work/Worker.cls";
        const string declarations = "Private WithEvents Class As Publisher\nPrivate Sub ";
        VbaCompletionResult At(string fragment)
            => VbaSemanticInventoryFixture.Create(new Dictionary<string, string>
            {
                ["file:///C:/work/Publisher.cls"] =
                    "Public Event Initialize(ByVal value As Long)\nPublic Event Changed()",
                [uri] = declarations + fragment
            }).GetCompletionResult(uri, 1, "Private Sub ".Length + fragment.Length);

        var prefix = Assert.Single(At("").Candidates);
        Assert.Equal("Class_", prefix.Label);
        Assert.Equal("Multiple Contracts", prefix.Detail);
        var members = At("Class_").Candidates;
        Assert.Equal(["Class_Changed", "Class_Initialize", "Class_Terminate"],
            members.Select(candidate => candidate.Label).Order(StringComparer.Ordinal));
        var initialize = Assert.Single(members, candidate => candidate.Label == "Class_Initialize");
        Assert.Equal("Multiple Contracts", initialize.Detail);
        Assert.Equal(2, initialize.SignaturePresentations.Count);
        Assert.Contains(initialize.SignaturePresentations, signature => signature.Label == "Sub Class_Initialize()");
        Assert.Contains(initialize.SignaturePresentations, signature => signature.Label == "Event Initialize(value As Long)");
        Assert.Equal("Initialize", initialize.TextEdit!.NewText);
    }

    private static VbaCompletionResult Complete(
        string markedSource,
        string extension = "cls")
    {
        var uri = "file:///C:/work/Worker." + extension;
        var marker = markedSource.IndexOf('|');
        Assert.True(marker >= 0);
        Assert.Equal(marker, markedSource.LastIndexOf('|'));
        var prefix = markedSource[..marker];
        var source = markedSource.Remove(marker, 1);
        var inventory = VbaSemanticInventoryFixture.Create(
            new Dictionary<string, string> { [uri] = source });
        return inventory.GetCompletionResult(
            uri,
            prefix.Count(character => character == '\n'),
            marker - prefix.LastIndexOf('\n') - 1);
    }
}
