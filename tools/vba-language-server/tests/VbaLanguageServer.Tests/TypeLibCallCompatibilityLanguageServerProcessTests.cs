using System.Text.Json;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class TypeLibCallCompatibilityLanguageServerProcessTests
{
    [Fact]
    public async Task Persisted_dispatch_input_contracts_accept_direct_string_and_concrete_object_arguments()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-dispatch-input-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory("vba-ls-dispatch-input-cache-").FullName;
        try
        {
            const string referenceName = "Fixture Library";
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectName = "DispatchInputProject",
                primaryDocument = "Book1",
                documents = new
                {
                    Book1 = new
                    {
                        kind = "excel",
                        sourcePath = "src/Book1",
                        templatePath = "src/Book1/Book1.xlsm",
                        binPath = "bin/Book1/Book1.xlsm",
                        publishPath = "publish/Book1/Book1.xlsm",
                        commonModules = Array.Empty<object>(),
                        references = new[] { new { name = referenceName, requested = true } }
                    }
                }
            }));
            var catalog = TypeLibReferenceCatalogBuilder.Build(referenceName,
                new TypeLibCatalogMetadata("Fixture",
                [
                    new TypeLibCatalogType("Store", VbaSourceDefinitionKind.Class,
                        Documentation: "Fixture Store input contracts.",
                        Members:
                        [
                            CreateInputMember("Add", 1, VbaCallableKind.Sub, null,
                                ("key", "Variant"), ("item", "Variant")),
                            CreateInputMember("Exists", 2, VbaCallableKind.Function, "Boolean",
                                ("key", "Variant")),
                            CreateInputMember("Remove", 3, VbaCallableKind.Sub, null,
                                ("key", "Variant")),
                            CreateInputMember("AcceptObject", 4, VbaCallableKind.Sub, null,
                                ("item", "Object"))
                        ],
                        Metadata: new TypeLibCatalogTypeMetadata(TypeLibCatalogRawTypeKind.Dispatch,
                            TypeFlags: 0, ImplementedInterfaces: []))
                ]));
            var store = new VbaProjectReferenceCatalogPersistentStore(cacheRoot);
            store.Save(new VbaProjectReferenceCatalogPersistentEntry(
                new VbaProjectReferenceCatalogIdentity(referenceName,
                    "{39000000-0000-0000-0000-000000000001}", 1, 0, 0,
                    Path.Combine(projectRoot, "Fixture.tlb")),
                catalog));
            Assert.Equal(VbaProjectReferenceCatalogPersistentLoadStatus.Current,
                store.Load(referenceName).Status);

            var payloadUri = new Uri(Path.Combine(projectRoot, "src", "Book1", "Payload.cls")).AbsoluteUri;
            const string payloadText = "VERSION 1.0 CLASS\nAttribute VB_Name = \"Payload\"\n";
            var callerUri = new Uri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas")).AbsoluteUri;
            var callerLines = new[]
            {
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    Dim values As Fixture.Store",
                "    Dim key As String",
                "    Dim item As Payload",
                "    Dim found As Boolean",
                "    values.Add key, item",
                "    found = values.Exists(key)",
                "    values.Remove key",
                "    values.AcceptObject item",
                "End Sub"
            };
            var callerText = string.Join('\n', callerLines);
            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot,
                enableProjectDiagnosticsSynchronization: true);
            await process.InitializeAsync();
            await process.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri = payloadUri, languageId = "vba", version = 1, text = payloadText }
            });
            await process.SendNotificationAsync("textDocument/didOpen", new
            {
                textDocument = new { uri = callerUri, languageId = "vba", version = 1, text = callerText }
            });
            await process.WaitForLogTextAsync(
                "reference 'Fixture Library' source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");

            var checkpoint = process.CaptureProjectDiagnosticsCheckpoint();
            await process.SendNotificationAsync("textDocument/didChange", new
            {
                textDocument = new { uri = callerUri, version = 2 },
                contentChanges = new[] { new { text = callerText } }
            });
            var notification = await process.WaitForProjectDiagnosticsSettledAsync(
                callerUri, expectedVersion: 2, checkpoint);
            Assert.DoesNotContain(
                notification.GetProperty("params").GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleCallArgumentList"
                    || diagnostic.GetProperty("message").GetString()
                        ?.Contains("ByRef type", StringComparison.Ordinal) == true);

            var requestId = 2;
            foreach (var (line, memberName) in new[]
            {
                (6, "Add"), (7, "Exists"), (8, "Remove"), (9, "AcceptObject")
            })
            {
                var hover = await process.SendRequestAsync(requestId++, "textDocument/hover", new
                {
                    textDocument = new { uri = callerUri },
                    position = new { line, character = callerLines[line].IndexOf(memberName, StringComparison.Ordinal) }
                });
                var markdown = hover.GetProperty("result").GetProperty("contents").GetProperty("value").GetString();
                Assert.Contains($"Fixture.Store.{memberName} input contract.", markdown, StringComparison.Ordinal);
                Assert.Contains($"{memberName}(ByRef ", markdown, StringComparison.Ordinal);
            }
            await process.ShutdownAsync(requestId);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static TypeLibCatalogMember CreateInputMember(
        string name,
        int memberId,
        VbaCallableKind kind,
        string? returnType,
        params (string Name, string Type)[] parameters)
    {
        var documentation = $"Fixture.Store.{name} input contract.";
        var signature = new VbaCallableSignature(name,
            parameters.Select(parameter => new VbaCallableParameter(parameter.Name,
                TypeReference: new VbaTypeReference(parameter.Type), IsByRef: true)
            {
                TypeLibPassing = new VbaTypeLibParameterPassing(VbaTypeLibParameterDirection.Input, 1)
            }).ToArray(),
            Documentation: documentation,
            CallableKind: kind,
            SupportsNamedArguments: true)
        {
            PassingConvention = VbaCallablePassingConvention.AutomationDispatch
        };
        return new TypeLibCatalogMember(name, VbaSourceDefinitionKind.Procedure,
            documentation, signature,
            TypeReference: returnType is null ? null : new VbaTypeReference(returnType),
            Metadata: new TypeLibCatalogCallableMetadata(memberId, FunctionFlags: 0)
            {
                IsReturnArray = returnType is null ? null : false
            });
    }
}
