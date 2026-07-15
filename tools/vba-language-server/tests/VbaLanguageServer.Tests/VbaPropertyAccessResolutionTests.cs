using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaPropertyAccessResolutionTests
{
    [Fact]
    public void SourcePropertyGetAndLetCoalesceForMemberCompletionAndResolution()
    {
        const string classUri = "file:///C:/work/Example.cls";
        const string workerUri = "file:///C:/work/Worker.bas";
        var classText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Example\"",
            "Public Property Get Value() As String",
            "End Property",
            "Public Property Let Value(ByVal AssignedValue As String)",
            "End Property"
        ]);
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim item As Example",
            "    item.",
            "    result = item.Value",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [classUri] = classText,
            [workerUri] = workerText
        });

        var sourceAccessors = index.GetDocumentDefinitions(classUri)
            .Where(definition => definition.Name == "Value")
            .OrderBy(definition => definition.Range.Start.Line)
            .ToArray();
        var completionDefinition = Assert.Single(
            index.GetCompletionDefinitions(workerUri, 3, "    item.".Length),
            definition => definition.Name == "Value");
        var resolvedDefinition = index.ResolveSourceDefinition(
            workerUri,
            4,
            "    result = item.Value".IndexOf("Value", StringComparison.Ordinal));

        Assert.Equal(2, sourceAccessors.Length);
        Assert.Equal(VbaPropertyAccess.Readable, sourceAccessors[0].PropertyAccess);
        Assert.Equal(VbaPropertyAccess.Writable, sourceAccessors[1].PropertyAccess);
        Assert.Equal(
            VbaPropertyAccess.Readable | VbaPropertyAccess.Writable,
            completionDefinition.PropertyAccess);
        Assert.Equal("String", completionDefinition.TypeReference?.Name);
        Assert.Equal(
            VbaPropertyAccess.Readable | VbaPropertyAccess.Writable,
            resolvedDefinition?.PropertyAccess);
    }

    [Fact]
    public void ReferencePropertyGetAndPutCoalesceWithinTheSameOwner()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string referenceName = "Generated Library";
        var currentDocument = new VbaSourceDocument(currentUri, "", "Worker", []);
        var resolver = new VbaNameResolutionService(
            [currentDocument],
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference(referenceName)]),
            VbaProjectReferenceCatalogSet.Empty,
            [
                ReferenceDefinition(referenceName, "Value", "GeneratedType", VbaPropertyAccess.Readable),
                ReferenceDefinition(referenceName, "Value", "GeneratedType", VbaPropertyAccess.Writable)
            ]);

        var member = Assert.Single(
            resolver.GetMembersOfType(currentDocument, "GeneratedType", referenceName),
            definition => definition.Name == "Value");

        Assert.Equal(VbaPropertyAccess.Readable | VbaPropertyAccess.Writable, member.PropertyAccess);
        Assert.Equal(
            VbaPropertyAccess.Readable | VbaPropertyAccess.Writable,
            resolver.ResolveMember(currentDocument, "GeneratedType", referenceName, "Value")?.PropertyAccess);
    }

    [Theory]
    [InlineData(VbaPropertyAccess.Readable)]
    [InlineData(VbaPropertyAccess.Unknown)]
    public void DuplicateOrUnknownPropertyAccessorsRemainAmbiguous(VbaPropertyAccess access)
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string classUri = "file:///C:/work/Example.cls";
        var currentDocument = new VbaSourceDocument(currentUri, "", "Worker", []);
        var classDocument = new VbaSourceDocument(classUri, "", "Example", [
            SourceDefinition("Value", classUri, "Example", 1, access),
            SourceDefinition("Value", classUri, "Example", 3, access)
        ]);
        var resolver = new VbaNameResolutionService(
            [currentDocument, classDocument],
            referenceSelection: null,
            referenceCatalogs: VbaProjectReferenceCatalogSet.Empty);

        Assert.DoesNotContain(
            resolver.GetMembersOfType(currentDocument, "Example", referenceName: null),
            definition => definition.Name == "Value");
        Assert.Null(resolver.ResolveMember(
            currentDocument,
            "Example",
            referenceName: null,
            memberName: "Value"));
    }

    private static VbaSourceDefinition SourceDefinition(
        string name,
        string uri,
        string moduleName,
        int line,
        VbaPropertyAccess access)
    {
        var range = new VbaRange(new VbaPosition(line, 0), new VbaPosition(line, name.Length));
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, name, range),
            new VbaDefinitionLocation(uri, range),
            name,
            VbaSourceDefinitionKind.Property,
            VbaSourceDefinitionVisibility.Public,
            moduleName,
            PropertyAccess: access);
    }

    private static VbaSourceDefinition ReferenceDefinition(
        string referenceName,
        string name,
        string parentTypeName,
        VbaPropertyAccess access)
    {
        var range = new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, name.Length));
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForProjectReference(
                referenceName,
                parentTypeName,
                VbaSourceDefinitionKind.Property,
                name),
            new VbaDefinitionLocation(
                $"{VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix}{Uri.EscapeDataString(referenceName)}/{Uri.EscapeDataString(name)}",
                range),
            name,
            VbaSourceDefinitionKind.Property,
            VbaSourceDefinitionVisibility.Public,
            referenceName,
            ParentTypeName: parentTypeName,
            PropertyAccess: access);
    }
}
