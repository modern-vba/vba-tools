using System.Text;
using System.Text.Json;
using VbaDev.App.HostClasses;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class HostClassCommandTests
{
    [Fact]
    public void JsonListUsesThePrimaryDocumentAndReturnsCanonicalRequestContext()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new EmptyHostClassInspectionAutomation());

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        AssertJsonPropertyNames(
            output,
            "schemaVersion",
            "project",
            "document",
            "sourceTemplate",
            "classEnumerationComplete",
            "complete",
            "classes",
            "diagnostics",
            "warnings");
        Assert.Equal("1.0", output.GetProperty("schemaVersion").GetString());
        Assert.Equal(Path.GetFullPath(root), output.GetProperty("project").GetString());
        Assert.Equal("Book1", output.GetProperty("document").GetString());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "src", "Book1", "Book1.xlsm")),
            output.GetProperty("sourceTemplate").GetString());
        Assert.True(output.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.True(output.GetProperty("complete").GetBoolean());
        Assert.Empty(output.GetProperty("classes").EnumerateArray());
        Assert.Empty(output.GetProperty("diagnostics").EnumerateArray());
        Assert.Empty(output.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void ExplicitDocumentSelectorPreservesManifestDeclaredCasing()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new EmptyHostClassInspectionAutomation());

        var result = application.Run(
            ["host-class", "list", "--document", "book1", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Book1", parsed.RootElement.GetProperty("document").GetString());
        Assert.EndsWith(
            Path.Combine("src", "Book1", "Book1.xlsm"),
            parsed.RootElement.GetProperty("sourceTemplate").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeProjectSelectorUsesTheCommandWorkingDirectoryAndSelectsASecondDocument()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        const string secondDocument = "ReportBook";
        var secondTemplate = Path.Combine(
            root,
            "src",
            secondDocument,
            $"{secondDocument}.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(secondTemplate)!);
        File.WriteAllText(secondTemplate, "second template", new UTF8Encoding(false));
        var manifestStore = new JsonProjectManifestStore();
        var manifest = manifestStore.Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        manifest.Documents[secondDocument] = ProjectDocument.CreateExcel(secondDocument);
        manifestStore.Save(root, manifest);
        var automation = new StubHostClassInspectionAutomation(
            HostClassInspectionBatch.CreateComplete([]));
        var application = CommandLineTestFactory.Create(
            temp.Path,
            hostClassInspectionAutomation: automation);

        var result = application.Run(
            [
                "host-class",
                "list",
                "--project",
                "Project",
                "--document",
                "reportbook",
                "--format",
                "json"
            ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(Path.GetFullPath(root), parsed.RootElement.GetProperty("project").GetString());
        Assert.Equal(secondDocument, parsed.RootElement.GetProperty("document").GetString());
        Assert.Equal(
            Path.GetFullPath(secondTemplate),
            parsed.RootElement.GetProperty("sourceTemplate").GetString());
        Assert.Equal(
            Path.GetFullPath(secondTemplate),
            Assert.Single(automation.Requests).SourceTemplatePath);
    }

    [Fact]
    public void InvalidFormatIsRejectedBeforeHostClassInspectionStarts()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var automation = new StubHostClassInspectionAutomation(
            HostClassInspectionBatch.CreateComplete([]));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: automation);

        var result = application.Run(
            ["host-class", "list", "--format", "yaml"]);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("\"schemaVersion\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("yaml", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("json", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.Requests);
    }

    [Fact]
    public void JsonListReturnsAResolvedClassWithItsInspectedEventSignature()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var inspectedClass = new ResolvedHostClassInspectionEntry(
            new HostClassIdentity("ThisWorkbook", HostClassComponentKind.Document),
            "Workbook",
            [
                new HostEventSignature(
                    "BeforeClose",
                    [
                        new HostEventParameter(
                            "Cancel",
                            new IntrinsicHostEventTypeReference("Boolean"),
                            HostEventPassingMechanism.ByRef,
                            HostEventArrayShape.Scalar,
                            Optional: false,
                            ParamArray: false)
                    ],
                    "Occurs before the workbook closes.",
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([inspectedClass])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classEntry = Assert.Single(parsed.RootElement.GetProperty("classes").EnumerateArray());
        AssertJsonPropertyNames(
            classEntry,
            "identity",
            "status",
            "intrinsicEventSourceName",
            "events");
        Assert.Equal("resolved", classEntry.GetProperty("status").GetString());
        var identity = classEntry.GetProperty("identity");
        AssertJsonPropertyNames(identity, "name", "kind");
        Assert.Equal("ThisWorkbook", identity.GetProperty("name").GetString());
        Assert.Equal("document", identity.GetProperty("kind").GetString());
        Assert.Equal("Workbook", classEntry.GetProperty("intrinsicEventSourceName").GetString());
        var inspectedEvent = Assert.Single(classEntry.GetProperty("events").EnumerateArray());
        AssertJsonPropertyNames(
            inspectedEvent,
            "name",
            "parameters",
            "documentation",
            "authoringAvailable",
            "existingHandlerRecognizable");
        Assert.Equal("BeforeClose", inspectedEvent.GetProperty("name").GetString());
        Assert.Equal("Occurs before the workbook closes.", inspectedEvent.GetProperty("documentation").GetString());
        Assert.True(inspectedEvent.GetProperty("authoringAvailable").GetBoolean());
        Assert.True(inspectedEvent.GetProperty("existingHandlerRecognizable").GetBoolean());
        var parameter = Assert.Single(inspectedEvent.GetProperty("parameters").EnumerateArray());
        AssertJsonPropertyNames(
            parameter,
            "name",
            "type",
            "passing",
            "arrayShape",
            "optional",
            "paramArray");
        Assert.Equal("Cancel", parameter.GetProperty("name").GetString());
        Assert.Equal("byRef", parameter.GetProperty("passing").GetString());
        Assert.Equal("scalar", parameter.GetProperty("arrayShape").GetString());
        Assert.False(parameter.GetProperty("optional").GetBoolean());
        Assert.False(parameter.GetProperty("paramArray").GetBoolean());
        var type = parameter.GetProperty("type");
        AssertJsonPropertyNames(type, "kind", "name");
        Assert.Equal("intrinsic", type.GetProperty("kind").GetString());
        Assert.Equal("Boolean", type.GetProperty("name").GetString());
    }

    [Fact]
    public void TextAndJsonPreserveParameterOrderAndInspectedCallableMetadata()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [
                new HostEventSignature(
                    "Sample",
                    [
                        new HostEventParameter(
                            "Values",
                            new IntrinsicHostEventTypeReference("Long"),
                            HostEventPassingMechanism.ByRef,
                            HostEventArrayShape.Array,
                            Optional: false,
                            ParamArray: false),
                        new HostEventParameter(
                            "Flag",
                            new IntrinsicHostEventTypeReference("Boolean"),
                            HostEventPassingMechanism.ByVal,
                            HostEventArrayShape.Scalar,
                            Optional: true,
                            ParamArray: false),
                        new HostEventParameter(
                            "Arguments",
                            new IntrinsicHostEventTypeReference("Variant"),
                            HostEventPassingMechanism.ByRef,
                            HostEventArrayShape.Array,
                            Optional: false,
                            ParamArray: true)
                    ],
                    Documentation: null,
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var jsonResult = application.Run(["host-class", "list", "--format", "json"]);
        var textResult = application.Run(["host-class", "list"]);

        Assert.Equal(0, jsonResult.ExitCode);
        using var parsed = JsonDocument.Parse(jsonResult.StandardOutput);
        var parameters = parsed.RootElement.GetProperty("classes")[0]
            .GetProperty("events")[0]
            .GetProperty("parameters")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(["Values", "Flag", "Arguments"], parameters.Select(parameter =>
            parameter.GetProperty("name").GetString()));
        Assert.Equal(["array", "scalar", "array"], parameters.Select(parameter =>
            parameter.GetProperty("arrayShape").GetString()));
        Assert.Equal([false, true, false], parameters.Select(parameter =>
            parameter.GetProperty("optional").GetBoolean()));
        Assert.Equal([false, false, true], parameters.Select(parameter =>
            parameter.GetProperty("paramArray").GetBoolean()));
        Assert.Equal(0, textResult.ExitCode);
        var valuesIndex = textResult.StandardOutput.IndexOf(
            "ByRef Values() As Long",
            StringComparison.Ordinal);
        var flagIndex = textResult.StandardOutput.IndexOf(
            "Optional ByVal Flag As Boolean",
            StringComparison.Ordinal);
        var argumentsIndex = textResult.StandardOutput.IndexOf(
            "ParamArray ByRef Arguments() As Variant",
            StringComparison.Ordinal);
        Assert.True(valuesIndex >= 0);
        Assert.True(flagIndex > valuesIndex);
        Assert.True(argumentsIndex > flagIndex);
    }

    [Fact]
    public void JsonListReturnsAnExactUnverifiedEntryWhenTheEventSourceNameCannotBeRead()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var failedClass = new UnverifiedHostClassInspectionEntry(
            new HostClassIdentity("Sheet1", HostClassComponentKind.Document),
            HostClassInspectionFailureReason.IntrinsicEventSourceNameReadFailure,
            "The VBE intrinsic Event source name could not be read.");
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                new HostClassInspectionBatch(
                    ClassEnumerationComplete: true,
                    Classes: [failedClass])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.True(parsed.RootElement.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.False(parsed.RootElement.GetProperty("complete").GetBoolean());
        var classEntry = Assert.Single(parsed.RootElement.GetProperty("classes").EnumerateArray());
        AssertJsonPropertyNames(classEntry, "identity", "status", "reasonCode", "message");
        Assert.Equal("unverified", classEntry.GetProperty("status").GetString());
        Assert.Equal(
            "intrinsicEventSourceNameReadFailure",
            classEntry.GetProperty("reasonCode").GetString());
        Assert.Equal(
            "The VBE intrinsic Event source name could not be read.",
            classEntry.GetProperty("message").GetString());
    }

    [Fact]
    public void JsonListSerializesPortableTypeLibParameterIdentity()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var inspectedClass = CreateClassWithParameterType(
            new TypeLibHostEventTypeReference(
                "Range",
                Guid.Parse("00020813-0000-0000-c000-000000000046"),
                MajorVersion: 1,
                MinorVersion: 9,
                Lcid: 0));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([inspectedClass])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var type = parsed.RootElement
            .GetProperty("classes")[0]
            .GetProperty("events")[0]
            .GetProperty("parameters")[0]
            .GetProperty("type");
        AssertJsonPropertyNames(
            type,
            "kind",
            "name",
            "libraryGuid",
            "majorVersion",
            "minorVersion",
            "lcid");
        Assert.Equal("typeLib", type.GetProperty("kind").GetString());
        Assert.Equal("Range", type.GetProperty("name").GetString());
        Assert.Equal("00020813-0000-0000-c000-000000000046", type.GetProperty("libraryGuid").GetString());
        Assert.Equal(1, type.GetProperty("majorVersion").GetInt32());
        Assert.Equal(9, type.GetProperty("minorVersion").GetInt32());
        Assert.Equal(0, type.GetProperty("lcid").GetInt32());
    }

    [Fact]
    public void JsonListKeepsAnOpaqueParameterTypeAsResolvedEvidence()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var inspectedClass = CreateClassWithParameterType(
            new UnresolvedHostEventTypeReference("MysteryWidget"));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([inspectedClass])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
        var classEntry = parsed.RootElement.GetProperty("classes")[0];
        Assert.Equal("resolved", classEntry.GetProperty("status").GetString());
        var type = classEntry
            .GetProperty("events")[0]
            .GetProperty("parameters")[0]
            .GetProperty("type");
        AssertJsonPropertyNames(type, "kind", "displayName");
        Assert.Equal("unresolved", type.GetProperty("kind").GetString());
        Assert.Equal("MysteryWidget", type.GetProperty("displayName").GetString());
    }

    [Fact]
    public void JsonListCarriesOptionalBaseTypeProvenanceWithoutChangingTheEventSurface()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var projected = CreateClassWithParameterType(new IntrinsicHostEventTypeReference("Boolean"));
        projected = projected with
        {
            BaseTypeProvenance = new HostClassBaseTypeProvenance(
                "_Worksheet",
                Guid.Parse("00020813-0000-0000-c000-000000000046"),
                MajorVersion: 1,
                MinorVersion: 9,
                Lcid: 0)
        };
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classEntry = parsed.RootElement.GetProperty("classes")[0];
        Assert.Equal("resolved", classEntry.GetProperty("status").GetString());
        Assert.Single(classEntry.GetProperty("events").EnumerateArray());
        var provenance = classEntry.GetProperty("baseTypeProvenance");
        AssertJsonPropertyNames(
            provenance,
            "name",
            "libraryGuid",
            "majorVersion",
            "minorVersion",
            "lcid");
        Assert.Equal("_Worksheet", provenance.GetProperty("name").GetString());
        Assert.Equal("00020813-0000-0000-c000-000000000046", provenance.GetProperty("libraryGuid").GetString());
    }

    [Fact]
    public void JsonListOmitsDuplicateClassIdentitiesAndContinuesWithUniqueClasses()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var duplicateFirst = CreateResolvedClass("Sheet1", HostClassComponentKind.Document, "Worksheet");
        var duplicateSecond = CreateResolvedClass("sheet1", HostClassComponentKind.Document, "Worksheet");
        var unique = CreateResolvedClass("UserForm1", HostClassComponentKind.Form, "UserForm");
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([duplicateFirst, unique, duplicateSecond])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.False(output.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.False(output.GetProperty("complete").GetBoolean());
        var classEntry = Assert.Single(output.GetProperty("classes").EnumerateArray());
        Assert.Equal("UserForm1", classEntry.GetProperty("identity").GetProperty("name").GetString());
        Assert.Equal("form", classEntry.GetProperty("identity").GetProperty("kind").GetString());
        var diagnostic = Assert.Single(output.GetProperty("diagnostics").EnumerateArray());
        AssertJsonPropertyNames(diagnostic, "code", "message");
        Assert.Equal("classEnumerationFailure", diagnostic.GetProperty("code").GetString());
        Assert.Contains("Sheet1", diagnostic.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HostClassInspectionFailureReason.EventEnumerationFailure, "eventEnumerationFailure")]
    [InlineData(HostClassInspectionFailureReason.IntrinsicEventSourceNameReadFailure, "intrinsicEventSourceNameReadFailure")]
    [InlineData(HostClassInspectionFailureReason.SignatureReadFailure, "signatureReadFailure")]
    [InlineData(HostClassInspectionFailureReason.AvailabilityReadFailure, "availabilityReadFailure")]
    [InlineData(HostClassInspectionFailureReason.InspectionTimeout, "inspectionTimeout")]
    [InlineData(HostClassInspectionFailureReason.InspectionAborted, "inspectionAborted")]
    [InlineData(HostClassInspectionFailureReason.Cancelled, "cancelled")]
    [InlineData(HostClassInspectionFailureReason.InspectionFailure, "inspectionFailure")]
    public void JsonListUsesTheClosedStableClassFailureVocabulary(
        HostClassInspectionFailureReason reason,
        string expectedCode)
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var failedClass = new UnverifiedHostClassInspectionEntry(
            new HostClassIdentity("Sheet1", HostClassComponentKind.Document),
            reason,
            "Inspection did not produce a complete class.");
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                new HostClassInspectionBatch(true, [failedClass])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classEntry = Assert.Single(parsed.RootElement.GetProperty("classes").EnumerateArray());
        Assert.Equal(expectedCode, classEntry.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void JsonListUsesCanonicalClassAndEventOrderWithoutStatusPartitioning()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var betaDocument = CreateResolvedClass(
            "beta",
            HostClassComponentKind.Document,
            "Worksheet",
            [CreateEvent("zeta"), CreateEvent("Alpha"), CreateEvent("beta")]);
        var failedDocument = new UnverifiedHostClassInspectionEntry(
            new HostClassIdentity("Alpha2", HostClassComponentKind.Document),
            HostClassInspectionFailureReason.SignatureReadFailure,
            "Signature inspection failed.");
        var alphaDocument = CreateResolvedClass("alpha", HostClassComponentKind.Document, "Worksheet");
        var form = CreateResolvedClass("AForm", HostClassComponentKind.Form, "UserForm");
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                new HostClassInspectionBatch(true, [form, betaDocument, failedDocument, alphaDocument])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classes = parsed.RootElement.GetProperty("classes").EnumerateArray().ToArray();
        Assert.Equal(
            ["alpha", "Alpha2", "beta", "AForm"],
            classes.Select(entry => entry.GetProperty("identity").GetProperty("name").GetString()));
        Assert.Equal(
            ["resolved", "unverified", "resolved", "resolved"],
            classes.Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal(
            ["Alpha", "beta", "zeta"],
            classes[2].GetProperty("events").EnumerateArray()
                .Select(entry => entry.GetProperty("name").GetString()));
    }

    [Fact]
    public void JsonListCoalescesEquivalentSameNameEventObservations()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var change = CreateEvent("Change");
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [change, change]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var events = parsed.RootElement.GetProperty("classes")[0].GetProperty("events").EnumerateArray();
        var inspectedEvent = Assert.Single(events);
        Assert.Equal("Change", inspectedEvent.GetProperty("name").GetString());
    }

    [Fact]
    public void JsonListRejectsConflictingSameNameEventContractsWithoutPartialEventData()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var withoutParameters = CreateEvent("Change");
        var withParameter = withoutParameters with
        {
            Parameters =
            [
                new HostEventParameter(
                    "Cancel",
                    new IntrinsicHostEventTypeReference("Boolean"),
                    HostEventPassingMechanism.ByRef,
                    HostEventArrayShape.Scalar,
                    Optional: false,
                    ParamArray: false)
            ]
        };
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [withoutParameters, withParameter]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classEntry = Assert.Single(parsed.RootElement.GetProperty("classes").EnumerateArray());
        AssertJsonPropertyNames(classEntry, "identity", "status", "reasonCode", "message");
        Assert.Equal("unverified", classEntry.GetProperty("status").GetString());
        Assert.Equal("eventEnumerationFailure", classEntry.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void JsonListRejectsConflictingSameNameEventAvailability()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var authorable = CreateEvent("Change");
        var recognizableOnly = authorable with { AuthoringAvailable = false };
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [authorable, recognizableOnly]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var classEntry = Assert.Single(parsed.RootElement.GetProperty("classes").EnumerateArray());
        Assert.Equal("unverified", classEntry.GetProperty("status").GetString());
        Assert.Equal("eventEnumerationFailure", classEntry.GetProperty("reasonCode").GetString());
        Assert.False(classEntry.TryGetProperty("events", out _));
    }

    [Fact]
    public void JsonListPreservesInspectedFalseFalseAvailabilityAsResolvedEvidence()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var structuralOnly = CreateEvent("RemoteChange") with
        {
            AuthoringAvailable = false,
            ExistingHandlerRecognizable = false
        };
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [structuralOnly]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
        var inspectedEvent = parsed.RootElement
            .GetProperty("classes")[0]
            .GetProperty("events")[0];
        Assert.False(inspectedEvent.GetProperty("authoringAvailable").GetBoolean());
        Assert.False(inspectedEvent.GetProperty("existingHandlerRecognizable").GetBoolean());
    }

    [Fact]
    public void JsonListPrefersOneDocumentedWholeObservationForPresentationOnlyDifferences()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var withoutDocumentation = CreateEvent("Change");
        var documented = withoutDocumentation with
        {
            Name = "change",
            Documentation = "Retained documented observation."
        };
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [withoutDocumentation, documented]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var inspectedEvent = Assert.Single(
            parsed.RootElement.GetProperty("classes")[0].GetProperty("events").EnumerateArray());
        Assert.Equal("change", inspectedEvent.GetProperty("name").GetString());
        Assert.Equal(
            "Retained documented observation.",
            inspectedEvent.GetProperty("documentation").GetString());
    }

    [Fact]
    public void JsonListPreservesSuccessWhenOnlyTheInspectionWorkspaceIsRetained()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var retainedPath = Path.GetFullPath(temp.CreateDirectory("retained-inspection"));
        var completion = new HostClassInspectionCompletion(
            HostClassInspectionBatch.CreateComplete([]),
            [
                new HostClassInspectionWarning(
                    "inspectionWorkspaceRetained",
                    $"Retained inspection workspace: {retainedPath}")
            ]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(completion));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
        var warning = Assert.Single(parsed.RootElement.GetProperty("warnings").EnumerateArray());
        AssertJsonPropertyNames(warning, "code", "message");
        Assert.Equal("inspectionWorkspaceRetained", warning.GetProperty("code").GetString());
        Assert.Contains(retainedPath, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseProofFailureEmitsNoProjectionOutput()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var retainedPath = Path.GetFullPath(temp.CreateDirectory("retained-inspection"));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new ThrowingHostClassInspectionAutomation(
                new WorkbookAutomationCleanupException(
                    $"The owned Excel process release could not be proved; retained workspace: {retainedPath}")));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("release could not be proved", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void SourcePreparationFailureEmitsNoProjectionOutput()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var sourceTemplate = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        var retainedPath = Path.GetFullPath(temp.CreateDirectory("retained-preparation"));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new ThrowingHostClassInspectionAutomation(
                new HostClassInspectionPreparationException(
                    sourceTemplate,
                    retainedPath,
                    workspaceRetained: true,
                    innerException: new IOException("The private copy could not be prepared."))));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(sourceTemplate, result.StandardError, StringComparison.Ordinal);
        Assert.Contains(retainedPath, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ListSuppliesTheEstablishedStageDeadlinesAndManifestWorkbookOpenOverride()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, workbookOpenTimeoutSeconds: 41);
        var automation = new StubHostClassInspectionAutomation(
            HostClassInspectionBatch.CreateComplete([]));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: automation);

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        var request = Assert.Single(automation.Requests);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeouts.ExcelProcessStart);
        Assert.Equal(TimeSpan.FromSeconds(41), request.Timeouts.WorkbookOpen);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeouts.CooperativeCleanup);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Timeouts.ClassEnumeration);
        Assert.Equal(TimeSpan.FromSeconds(60), request.Timeouts.ClassInspection);
    }

    [Fact]
    public void JsonListReturnsTheReleasedTerminalPartialResultAfterCooperativeCancellation()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var resolved = CreateResolvedClass("Sheet1", HostClassComponentKind.Document, "Worksheet");
        var current = CreateUnverifiedClass(
            "Sheet2",
            HostClassComponentKind.Document,
            HostClassInspectionFailureReason.Cancelled,
            "Inspection was cancelled.");
        var remaining = CreateUnverifiedClass(
            "UserForm1",
            HostClassComponentKind.Form,
            HostClassInspectionFailureReason.Cancelled,
            "Inspection was cancelled.");
        var completion = HostClassInspectionCompletion.Create(
            HostClassInspectionBatch.CreateCancelled(
                classEnumerationComplete: true,
                [resolved, current, remaining],
                "Host-class inspection was cancelled."));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(completion));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.True(output.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.False(output.GetProperty("complete").GetBoolean());
        Assert.Equal(
            ["resolved", "unverified", "unverified"],
            output.GetProperty("classes").EnumerateArray()
                .Select(entry => entry.GetProperty("status").GetString()));
        Assert.Equal(
            ["cancelled", "cancelled"],
            output.GetProperty("classes").EnumerateArray()
                .Where(entry => entry.GetProperty("status").GetString() == "unverified")
                .Select(entry => entry.GetProperty("reasonCode").GetString()));
        var diagnostic = Assert.Single(output.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("operationCancelled", diagnostic.GetProperty("code").GetString());
    }

    [Fact]
    public void JsonListReportsCausalAndAbortedClassesWhenInspectionStateBecomesUntrusted()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var resolved = CreateResolvedClass("Sheet1", HostClassComponentKind.Document, "Worksheet");
        var causal = CreateUnverifiedClass(
            "Sheet2",
            HostClassComponentKind.Document,
            HostClassInspectionFailureReason.InspectionTimeout,
            "Class inspection exceeded its deadline.");
        var later = CreateUnverifiedClass(
            "UserForm1",
            HostClassComponentKind.Form,
            HostClassInspectionFailureReason.InspectionAborted,
            "Inspection stopped after shared state became untrusted.");
        var completion = HostClassInspectionCompletion.Create(
            HostClassInspectionBatch.CreateInspectionStateUntrusted(
                classEnumerationComplete: true,
                [resolved, causal, later],
                "The shared Excel/VBIDE inspection state became untrusted."));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(completion));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var output = parsed.RootElement;
        Assert.True(output.GetProperty("classEnumerationComplete").GetBoolean());
        Assert.False(output.GetProperty("complete").GetBoolean());
        Assert.Equal(
            ["resolved", "inspectionTimeout", "inspectionAborted"],
            output.GetProperty("classes").EnumerateArray().Select(entry =>
                entry.GetProperty("status").GetString() == "resolved"
                    ? "resolved"
                    : entry.GetProperty("reasonCode").GetString()));
        var diagnostic = Assert.Single(output.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("inspectionStateUntrusted", diagnostic.GetProperty("code").GetString());
    }

    [Fact]
    public void UntrustedTerminalOutcomeCannotReportCompleteEvenWhenEveryRetainedClassIsResolved()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var resolved = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet");
        var completion = HostClassInspectionCompletion.Create(
            HostClassInspectionBatch.CreateInspectionStateUntrusted(
                classEnumerationComplete: true,
                [resolved],
                "Shared inspection state became untrusted after the retained result."));
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(completion));

        var result = application.Run(["host-class", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.False(parsed.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "inspectionStateUntrusted",
            Assert.Single(parsed.RootElement.GetProperty("diagnostics").EnumerateArray())
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public void ListDefaultsToCanonicalHumanReadableProjectionText()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet",
            [CreateEvent("zeta"), CreateEvent("Alpha")]);
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("Document: Book1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  document Sheet1 [resolved]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("    Intrinsic Event source: Worksheet", result.StandardOutput, StringComparison.Ordinal);
        var alphaIndex = result.StandardOutput.IndexOf("      Alpha", StringComparison.Ordinal);
        var zetaIndex = result.StandardOutput.IndexOf("      zeta", StringComparison.Ordinal);
        Assert.True(alphaIndex >= 0);
        Assert.True(zetaIndex > alphaIndex);
        Assert.DoesNotContain("\"schemaVersion\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TextListIncludesOptionalBaseTypeProvenance()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var projected = CreateResolvedClass(
            "Sheet1",
            HostClassComponentKind.Document,
            "Worksheet") with
        {
            BaseTypeProvenance = new HostClassBaseTypeProvenance(
                "_Worksheet",
                Guid.Parse("00020813-0000-0000-c000-000000000046"),
                MajorVersion: 1,
                MinorVersion: 9,
                Lcid: 0)
        };
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(
                HostClassInspectionBatch.CreateComplete([projected])));

        var result = application.Run(["host-class", "list"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "Base type: _Worksheet (00020813-0000-0000-c000-000000000046 1.9 LCID 0)",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TextListMakesIncompleteEnumerationAndDiagnosticsVisible()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var batch = new HostClassInspectionBatch(false, [])
        {
            Diagnostics =
            [
                new HostClassInspectionDiagnostic(
                    "classEnumerationFailure",
                    "The complete identity set could not be enumerated.")
            ]
        };
        var application = CommandLineTestFactory.Create(
            root,
            hostClassInspectionAutomation: new StubHostClassInspectionAutomation(batch));

        var result = application.Run(["host-class", "list"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Class enumeration complete: false", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Complete: false", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Diagnostics:", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "classEnumerationFailure: The complete identity set could not be enumerated.",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    private static string CreateProject(TempDirectory temp, int? workbookOpenTimeoutSeconds = null)
    {
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(
            Path.Combine(root, "src", "Book1", "Book1.xlsm"),
            "template",
            new UTF8Encoding(false));
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        if (workbookOpenTimeoutSeconds is not null)
        {
            manifest = manifest with
            {
                CommandDefaults = new CommandDefaults(
                    ExcelAutomation: new ExcelAutomationCommandDefaults(workbookOpenTimeoutSeconds))
            };
        }

        new JsonProjectManifestStore().Save(root, manifest);
        return root;
    }

    private static ResolvedHostClassInspectionEntry CreateClassWithParameterType(
        HostEventTypeReference type)
        => new(
            new HostClassIdentity("Sheet1", HostClassComponentKind.Document),
            "Worksheet",
            [
                new HostEventSignature(
                    "Change",
                    [
                        new HostEventParameter(
                            "Target",
                            type,
                            HostEventPassingMechanism.ByVal,
                            HostEventArrayShape.Scalar,
                            Optional: false,
                            ParamArray: false)
                    ],
                    Documentation: null,
                    AuthoringAvailable: true,
                    ExistingHandlerRecognizable: true)
            ]);

    private static ResolvedHostClassInspectionEntry CreateResolvedClass(
        string name,
        HostClassComponentKind kind,
        string eventSourceName,
        IReadOnlyList<HostEventSignature>? events = null)
        => new(new HostClassIdentity(name, kind), eventSourceName, events ?? []);

    private static UnverifiedHostClassInspectionEntry CreateUnverifiedClass(
        string name,
        HostClassComponentKind kind,
        HostClassInspectionFailureReason reason,
        string message)
        => new(new HostClassIdentity(name, kind), reason, message);

    private static HostEventSignature CreateEvent(string name)
        => new(
            name,
            [],
            Documentation: null,
            AuthoringAvailable: true,
            ExistingHandlerRecognizable: true);

    private static void AssertJsonPropertyNames(JsonElement element, params string[] expectedNames)
        => Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private sealed class EmptyHostClassInspectionAutomation : IHostClassInspectionAutomation
    {
        public Task<HostClassInspectionCompletion> InspectAsync(
            HostClassInspectionRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(HostClassInspectionCompletion.Create(HostClassInspectionBatch.CreateComplete([])));
    }

    private sealed class StubHostClassInspectionAutomation : IHostClassInspectionAutomation
    {
        private readonly HostClassInspectionCompletion completion;

        public List<HostClassInspectionRequest> Requests { get; } = [];

        public StubHostClassInspectionAutomation(HostClassInspectionBatch batch)
            : this(HostClassInspectionCompletion.Create(batch))
        {
        }

        public StubHostClassInspectionAutomation(HostClassInspectionCompletion completion)
        {
            this.completion = completion;
        }

        public Task<HostClassInspectionCompletion> InspectAsync(
            HostClassInspectionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(completion);
        }
    }

    private sealed class ThrowingHostClassInspectionAutomation(Exception exception)
        : IHostClassInspectionAutomation
    {
        public Task<HostClassInspectionCompletion> InspectAsync(
            HostClassInspectionRequest request,
            CancellationToken cancellationToken)
            => Task.FromException<HostClassInspectionCompletion>(exception);
    }
}
