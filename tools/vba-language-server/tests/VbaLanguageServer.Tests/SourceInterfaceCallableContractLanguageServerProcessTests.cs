using System.Text.Json;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceInterfaceCallableContractLanguageServerProcessTests
{
    [Fact]
    public async Task TypeLib_coclass_is_not_offered_as_an_Implements_contract()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-completion-coclass-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-completion-coclass-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Publisher",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Sub Publisher_";
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements Publisher",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = declaration.Length }
                });
            Assert.Empty(completion.GetProperty("result").EnumerateArray());

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeLib_interface_Sub_reports_the_required_contract()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-validation-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-validation-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync(new
            {
                textDocument = new
                {
                    publishDiagnostics = new { relatedInformation = true }
                }
            });

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });

            var notification = await process.WaitForDiagnosticsAsync(uri);
            var diagnostic = Assert.Single(
                notification
                    .GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                candidate => candidate.GetProperty("code").GetString()
                    == "validation.interfaceMemberNotImplemented");
            Assert.Contains(
                "Required contract: Sub IRunner_Run().",
                diagnostic.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            if (diagnostic.TryGetProperty("relatedInformation", out var related))
            {
                Assert.DoesNotContain(
                    related.EnumerateArray(),
                    item => item.GetProperty("location")
                        .GetProperty("uri")
                        .GetString()!
                        .StartsWith(
                            "vba-reference://",
                            StringComparison.OrdinalIgnoreCase));
            }

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TypeLib_interface_implementation_has_no_source_Prepare_Rename_projection()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-prepare-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-prepare-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run()",
                    [],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process =
                await LanguageServerProcessHarness.StartAsync(
                    referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner",
                "Private Sub IRunner_Run()",
                "End Sub"
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var prepare = await SendPositionRequestAsync(
                process,
                2,
                "textDocument/prepareRename",
                uri,
                text,
                "IRunner_Run");
            Assert.False(
                prepare.TryGetProperty("error", out var prepareError),
                prepareError.ToString());
            Assert.Equal(
                JsonValueKind.Null,
                prepare.GetProperty("result").ValueKind);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TypeLib_interface_parameter_keeps_its_reference_type_owner()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-type-owner-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-type-owner-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run(ByVal value As Payload)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: new VbaTypeReference("Payload"),
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "Payload",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [],
                            IsCreatable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.CoClass,
                                TypeFlags: 0,
                                ImplementedInterfaces: [])),
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync(new
            {
                textDocument = new
                {
                    publishDiagnostics = new { relatedInformation = true }
                }
            });

            var payloadPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Payload.cls");
            var payloadUri = new Uri(payloadPath).AbsoluteUri;
            const string payloadText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Payload"
                """;
            File.WriteAllText(payloadPath, payloadText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(payloadUri, payloadText));

            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string workerText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements IRunner
                Private Sub IRunner_Run(ByVal value As Payload)
                End Sub
                """;
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == workerUri);
            Assert.Contains(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeLib_parameter_type_does_not_invent_Variant()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-parameter-type-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-parameter-type-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run(value)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: null,
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string declaration = "Private Sub IRunner_";
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner",
                declaration
            ]);
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            var documentation = item.GetProperty("documentation")
                .GetProperty("value").GetString();
            Assert.Contains(
                "Sub IRunner_Run(value)",
                documentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Variant",
                documentation,
                StringComparison.Ordinal);

            workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner",
                "Private Sub IRunner_Run(ByVal value As Long)",
                "End Sub"
            ]);
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 3 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == workerUri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeLib_result_type_does_not_invent_Variant()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-result-type-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-result-type-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var readMember = new TypeLibCatalogMember(
                "Read",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Function Read()",
                    [],
                    CallableKind: VbaCallableKind.Function),
                TypeReference: null,
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0)
                {
                    IsReturnArray = false
                });
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IReader",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [readMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string declaration = "Private Function IReader_";
            var workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IReader",
                declaration
            ]);
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForDiagnosticsAsync(workerUri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            await process.WaitForDiagnosticsAsync(workerUri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri = workerUri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            var documentation = item.GetProperty("documentation")
                .GetProperty("value").GetString();
            Assert.Contains(
                "Function IReader_Read()",
                documentation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Variant",
                documentation,
                StringComparison.Ordinal);

            workerText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IReader",
                "Private Function IReader_Read() As Long",
                "End Function"
            ]);
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 3 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == workerUri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Known_TypeLib_array_result_is_preserved_in_contract_completion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-array-result-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-array-result-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var valuesMember = new TypeLibCatalogMember(
                "Values",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Function Values() As String()",
                    [],
                    CallableKind: VbaCallableKind.Function),
                TypeReference: new VbaTypeReference("String"),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0)
                {
                    IsReturnArray = true
                });
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IArray",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [valuesMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Function IArray_";
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IArray",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("IArray_Values", item.GetProperty("label").GetString());
            Assert.Contains(
                "Function IArray_Values() As String()",
                item.GetProperty("documentation").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IArray",
                "Private Function IArray_Values() As String",
                "End Function"
            ]);
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 3 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == uri);
            Assert.Contains(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeLib_result_array_metadata_keeps_only_the_Function_name()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-result-array-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-result-array-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var valuesMember = new TypeLibCatalogMember(
                "Values",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Function Values() As String",
                    [],
                    CallableKind: VbaCallableKind.Function),
                TypeReference: new VbaTypeReference("String"),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IArray",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [valuesMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Function IArray_";
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IArray",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("IArray_Values", item.GetProperty("label").GetString());
            Assert.False(item.TryGetProperty("documentation", out _));

            text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IArray",
                "Private Function IArray_Values() As String()",
                "End Function"
            ]);
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 3 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == uri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_TypeLib_parameter_passing_does_not_invent_ByRef()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-passing-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-passing-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run(value As Long)",
                    [
                        new VbaCallableParameter(
                            "value",
                            TypeReference: new VbaTypeReference("Long"),
                            IsByRef: null)
                    ],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string workerText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements IRunner
                Private Sub IRunner_Run(ByVal value As Long)
                End Sub
                """;
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == workerUri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_TypeLib_optional_default_metadata_remains_indeterminate()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-default-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-unknown-default-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run([ByVal value As Long])",
                    [
                        new VbaCallableParameter(
                            "value",
                            IsOptional: true,
                            TypeReference: new VbaTypeReference("Long"),
                            IsByRef: false)
                    ],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var workerPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var workerUri = new Uri(workerPath).AbsoluteUri;
            const string workerText = """
                VERSION 1.0 CLASS
                Attribute VB_Name = "Worker"
                Implements IRunner
                Private Sub IRunner_Run(Optional ByVal value As Long = 1)
                End Sub
                """;
            File.WriteAllText(workerPath, workerText);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(workerUri, workerText));
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri = workerUri, version = 2 },
                    contentChanges = new[] { new { text = workerText } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == workerUri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(2);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Incomplete_TypeLib_signature_keeps_the_known_interface_member_name()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-incomplete-signature-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-typelib-incomplete-signature-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var runMember = new TypeLibCatalogMember(
                "Run",
                VbaSourceDefinitionKind.Procedure,
                Documentation: null,
                new VbaCallableSignature(
                    "Sub Run(value)",
                    [new VbaCallableParameter("value")],
                    CallableKind: VbaCallableKind.Sub),
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0,
                    IsComplete: false));
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "IRunner",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [runMember],
                            IsCreatable: false,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Sub IRunner_";
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("IRunner_Run", item.GetProperty("label").GetString());
            Assert.Equal(
                "Interface Member",
                item.GetProperty("detail").GetString());
            Assert.False(item.TryGetProperty("documentation", out _));

            text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Worker\"",
                "Implements IRunner",
                "Private Sub IRunner_Run(ByVal value As Long, ByVal extra As String)",
                "End Sub"
            ]);
            var checkpoint = process.TranscriptCheckpoint;
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 3 },
                    contentChanges = new[] { new { text } }
                });
            var notification = await process.WaitForMessageAsync(
                checkpoint,
                message => message.TryGetProperty("method", out var method)
                    && method.GetString() == "textDocument/publishDiagnostics"
                    && message.GetProperty("params").GetProperty("uri").GetString()
                        == uri);
            Assert.DoesNotContain(
                notification.GetProperty("params")
                    .GetProperty("diagnostics")
                    .EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString()
                    == "validation.incompatibleInterfaceMemberSignature");

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TypeLib_Property_Get_invoke_kind_supplies_interface_completion()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-completion-typelib-").FullName;
        var cacheRoot = Directory.CreateTempSubdirectory(
            "vba-ls-interface-completion-typelib-cache-").FullName;
        try
        {
            const string referenceName = "Generated Interfaces";
            WriteReferenceCatalogProjectManifest(projectRoot, referenceName);
            var itemProperty = new TypeLibCatalogMember(
                "Item",
                VbaSourceDefinitionKind.Property,
                "Gets the item.",
                Signature: null,
                TypeReference: new VbaTypeReference("Long"),
                PropertyAccess: VbaPropertyAccess.Readable,
                Metadata: new TypeLibCatalogCallableMetadata(
                    MemberId: 1,
                    FunctionFlags: 0)
                {
                    PropertyAccessorKind = VbaPropertyAccessorKind.Get,
                    IsReturnArray = false
                });
            var catalog = TypeLibReferenceCatalogBuilder.Build(
                referenceName,
                new TypeLibCatalogMetadata(
                    "Generated",
                    [
                        new TypeLibCatalogType(
                            "ISettings",
                            VbaSourceDefinitionKind.Class,
                            Documentation: null,
                            Members: [itemProperty],
                            IsCreatable: false,
                            IsBrowsable: true,
                            Metadata: new TypeLibCatalogTypeMetadata(
                                TypeLibCatalogRawTypeKind.Dispatch,
                                TypeFlags: 0,
                                ImplementedInterfaces: []))
                    ]));
            new VbaProjectReferenceCatalogPersistentStore(cacheRoot).Save(
                new VbaProjectReferenceCatalogPersistentEntry(
                    CreateGeneratedReferenceCatalogIdentity(referenceName),
                    catalog));

            await using var process = await LanguageServerProcessHarness.StartAsync(
                referenceCatalogCacheRoot: cacheRoot);
            await process.InitializeAsync();

            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Settings.cls");
            var uri = new Uri(sourcePath).AbsoluteUri;
            const string declaration = "Private Property Get ISettings_";
            var text = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "Attribute VB_Name = \"Settings\"",
                "Implements ISettings",
                declaration
            ]);
            File.WriteAllText(sourcePath, text);
            await process.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, text));
            await process.WaitForDiagnosticsAsync(uri);
            await process.WaitForLogTextAsync(
                "source=persisted outcome=skipped phase=persistent-load expensiveMetadata=false");
            await process.SendNotificationAsync(
                "textDocument/didChange",
                new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text } }
                });
            await process.WaitForDiagnosticsAsync(uri);

            var completion = await process.SendRequestAsync(
                2,
                "textDocument/completion",
                new
                {
                    textDocument = new { uri },
                    position = new { line = 3, character = declaration.Length }
                });
            var item = Assert.Single(
                completion.GetProperty("result").EnumerateArray());
            Assert.Equal("ISettings_Item", item.GetProperty("label").GetString());
            Assert.Equal("Interface Member", item.GetProperty("detail").GetString());
            Assert.Contains(
                "Property Get ISettings_Item() As Long",
                item.GetProperty("documentation").GetProperty("value").GetString(),
                StringComparison.Ordinal);

            await process.ShutdownAsync(3);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_Function_declaration_name_offers_only_the_implemented_interface_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ICalculator.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ICalculator"
            Public Function Calculate() As Long
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Calculator.cls";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Calculator\"",
            "Implements ICalculator",
            "Private Function "
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new
                {
                    line = 3,
                    character = "Private Function ".Length
                }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("ICalculator_", item.GetProperty("label").GetString());
        Assert.Equal("Interface", item.GetProperty("detail").GetString());
        Assert.Equal(
            "ICalculator_",
            item.GetProperty("textEdit").GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Dead_exact_prefix_keeps_a_longer_viable_interface_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string shortInterfaceUri = "file:///C:/work/I.cls";
        const string shortInterfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "I"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(shortInterfaceUri, shortInterfaceText));
        await process.WaitForDiagnosticsAsync(shortInterfaceUri);

        const string longInterfaceUri = "file:///C:/work/I_Foo.cls";
        const string longInterfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "I_Foo"
            Public Sub Start()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(longInterfaceUri, longInterfaceText));
        await process.WaitForDiagnosticsAsync(longInterfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub I_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements I",
            "Implements I_Foo",
            "Private Sub I_Run()",
            "End Sub",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 6, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("I_Foo_", item.GetProperty("label").GetString());
        Assert.Equal("I_Foo_", item.GetProperty("textEdit")
            .GetProperty("newText").GetString());
        Assert.Equal(
            "Private Sub ".Length,
            item.GetProperty("textEdit").GetProperty("range")
                .GetProperty("start").GetProperty("character").GetInt32());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Viable_exact_prefix_opens_members_before_a_longer_prefix()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string shortInterfaceUri = "file:///C:/work/I.cls";
        const string shortInterfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "I"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(shortInterfaceUri, shortInterfaceText));
        await process.WaitForDiagnosticsAsync(shortInterfaceUri);

        const string longInterfaceUri = "file:///C:/work/I_Foo.cls";
        const string longInterfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "I_Foo"
            Public Sub Start()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(longInterfaceUri, longInterfaceText));
        await process.WaitForDiagnosticsAsync(longInterfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string declaration = "Private Sub I_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Implements I",
            "Implements I_Foo",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 4, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("I_Run", item.GetProperty("label").GetString());
        Assert.Equal("Run", item.GetProperty("textEdit")
            .GetProperty("newText").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task All_guarded_colliding_implementation_family_keeps_the_member_candidate()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IRunner.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IRunner"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Runner.cls";
        const string declaration = "Private Sub IRunner_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "#If VBA7 Then",
            "Private Sub IRunner_Run()",
            "End Sub",
            "#Else",
            declaration,
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 7, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("IRunner_Run", item.GetProperty("label").GetString());
        Assert.Equal(
            "Interface Member",
            item.GetProperty("detail").GetString());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Unconditional_prospective_declaration_suppresses_a_guarded_collision()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IRunner.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IRunner"
            Public Sub Run()
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Runner.cls";
        const string declaration = "Private Sub IRunner_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "#If VBA7 Then",
            "Private Sub IRunner_Run()",
            "End Sub",
            "#End If",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 7, character = declaration.Length }
            });
        Assert.Empty(completion.GetProperty("result").EnumerateArray());

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Interface_member_completion_projects_its_contract_signature()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ICalculator.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ICalculator"
            Public Function Calculate(ByVal value As Long) As Long
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Calculator.cls";
        const string declaration = "Private Function ICalculator_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Calculator\"",
            "Implements ICalculator",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 3, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("ICalculator_Calculate", item.GetProperty("label").GetString());
        Assert.Equal(
            "markdown",
            item.GetProperty("documentation").GetProperty("kind").GetString());
        Assert.Contains(
            "Function ICalculator_Calculate(value As Long) As Long",
            item.GetProperty("documentation").GetProperty("value").GetString(),
            StringComparison.Ordinal);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Coalesced_interface_signature_lists_every_documentation_variant()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/ICalculator.cls";
        var longDocumentation =
            "Second contract " + new string('x', 6_000) + " tail.";
        var interfaceText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"ICalculator\"",
            "#If FIRST_CONFIGURATION Then",
            "'* @brief First contract documentation.",
            "Public Function Calculate(ByVal value As Long) As Long",
            "End Function",
            "#ElseIf DUPLICATE_CONFIGURATION Then",
            "'* @brief First contract documentation.",
            "Public Function Calculate(ByVal value As Long) As Long",
            "End Function",
            "#ElseIf EMPTY_CONFIGURATION Then",
            "Public Function Calculate(ByVal value As Long) As Long",
            "End Function",
            "#Else",
            $"'* @brief {longDocumentation}",
            "Public Function Calculate(ByVal value As Long) As Long",
            "End Function",
            "#End If"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Calculator.cls";
        const string declaration = "Private Function ICalculator_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Calculator\"",
            "Implements ICalculator",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 3, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        var documentation = item
            .GetProperty("documentation")
            .GetProperty("value")
            .GetString();
        const string signature =
            "Function ICalculator_Calculate(value As Long) As Long [#If]";
        Assert.Equal(
            1,
            documentation!.Split(signature, StringSplitOptions.None).Length - 1);
        Assert.Contains("**Documentation variants**", documentation);
        Assert.Equal(
            1,
            documentation.Split(
                "First contract documentation.",
                StringSplitOptions.None).Length - 1);
        Assert.Contains($"2. {longDocumentation}", documentation);
        Assert.DoesNotContain("\n3. ", documentation);
        Assert.DoesNotContain("origin", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            documentation.IndexOf(
                "1. First contract documentation.",
                StringComparison.Ordinal)
            < documentation.IndexOf(
                $"2. {longDocumentation}",
                StringComparison.Ordinal));
        Assert.DoesNotContain("FIRST_CONFIGURATION", documentation);
        Assert.DoesNotContain("DUPLICATE_CONFIGURATION", documentation);
        Assert.DoesNotContain("EMPTY_CONFIGURATION", documentation);

        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_and_unconditional_completion_signatures_match_Signature_Help_order()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IRunner.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IRunner"
            Public Sub Run(ByVal value As Long)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Runner.cls";
        const string declaration = "Private Sub IRunner_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "#If VBA7 Then",
            "Implements IRunner",
            "#End If",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 6, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        var documentation = item.GetProperty("documentation")
            .GetProperty("value")
            .GetString()!;

        const string completeDeclaration =
            "Private Sub IRunner_Run(ByVal argument As Long)";
        implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "#If VBA7 Then",
            "Implements IRunner",
            "#End If",
            completeDeclaration,
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = implementationUri, version = 2 },
                contentChanges = new[] { new { text = implementationText } }
            });
        await process.WaitForDiagnosticsAsync(implementationUri);
        var signatureHelp = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "argument");
        var labels = signatureHelp.GetProperty("result")
            .GetProperty("signatures")
            .EnumerateArray()
            .Select(signature => signature.GetProperty("label").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "Sub IRunner_Run(value As Long)",
                "Sub IRunner_Run(value As Long) [#If]"
            ],
            labels);
        var completionOrder = labels
            .Select(label => documentation.IndexOf(label, StringComparison.Ordinal))
            .ToArray();
        Assert.All(completionOrder, index => Assert.True(index >= 0));
        Assert.True(completionOrder[0] < completionOrder[1]);

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Repeated_identical_Implements_relationships_coalesce_completion_and_Signature_Help()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IRunner.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IRunner"
            Public Sub Run(ByVal value As Long)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Runner.cls";
        const string declaration = "Private Sub IRunner_";
        var implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "Implements IRunner",
            declaration
        ]);
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var completion = await process.SendRequestAsync(
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = implementationUri },
                position = new { line = 4, character = declaration.Length }
            });
        var item = Assert.Single(completion.GetProperty("result").EnumerateArray());
        Assert.Equal("IRunner_Run", item.GetProperty("label").GetString());
        const string expectedSignature = "Sub IRunner_Run(value As Long)";
        var documentation = item.GetProperty("documentation")
            .GetProperty("value")
            .GetString()!;
        Assert.Equal(
            1,
            documentation.Split(expectedSignature, StringSplitOptions.None).Length - 1);

        implementationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Runner\"",
            "Implements IRunner",
            "Implements IRunner",
            "Private Sub IRunner_Run(ByVal argument As Long)",
            "End Sub"
        ]);
        await process.SendNotificationAsync(
            "textDocument/didChange",
            new
            {
                textDocument = new { uri = implementationUri, version = 2 },
                contentChanges = new[] { new { text = implementationText } }
            });
        await process.WaitForDiagnosticsAsync(implementationUri);

        var signatureHelp = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "argument");
        var signature = Assert.Single(
            signatureHelp.GetProperty("result")
                .GetProperty("signatures")
                .EnumerateArray());
        Assert.Equal(expectedSignature, signature.GetProperty("label").GetString());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Indexed_Property_parameter_keeps_ordinary_passing_while_value_passing_is_normalized()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IIndexed.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IIndexed"
            Public Property Let Item(ByRef index As Long, ByVal assigned As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Indexed.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Indexed"
            Implements IIndexed
            Private Property Let IIndexed_Item(ByVal itemIndex As Long, ByRef rhs As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IIndexed_Item' signature does not match any required Property Let contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(interfaceUri, related
            .GetProperty("location")
            .GetProperty("uri")
            .GetString());
        Assert.Equal(
            "Required contract: Property Let IIndexed_Item(ByRef index As Long, assigned As String). "
                + "Mismatches: parameter 1 passing: expected ByRef, found ByVal.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Indexed_Property_signature_help_tracks_the_physical_parameter_position()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IIndexed.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IIndexed"
            Public Property Let Item(ByVal index As Long, ByVal assigned As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Indexed.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Indexed"
            Implements IIndexed
            Private Property Let IIndexed_Item(ByVal itemIndex As Long, ByVal rhs As String)
            End Property
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));
        await process.WaitForDiagnosticsAsync(implementationUri);

        var firstParameterResponse = await SendPositionRequestAsync(
            process,
            2,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "Long");
        Assert.Equal(
            0,
            firstParameterResponse
                .GetProperty("result")
                .GetProperty("activeParameter")
                .GetInt32());

        var response = await SendPositionRequestAsync(
            process,
            3,
            "textDocument/signatureHelp",
            implementationUri,
            implementationText,
            "rhs");
        var result = response.GetProperty("result");
        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());

        await process.ShutdownAsync(4);
    }

    [Fact]
    public async Task Optional_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = 1 + 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = 3)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IWorker_Run' signature does not match any required Sub contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([count As Long]). "
                + "Mismatches: parameter 1 default: expected 2, found 3.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Optional_string_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal text As String = "a")
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal value As String = "b")
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([text As String]). "
                + "Mismatches: parameter 1 default: expected \"a\", found \"b\".",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Optional_floating_defaults_compare_their_evaluated_constant_values()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal ratio As Double = 1.5)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Double = 2.5)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Sub IWorker_Run([ratio As Double]). "
                + "Mismatches: parameter 1 default: expected 1.5, found 2.5.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Equivalent_Optional_default_spellings_fulfill_the_same_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = 1 + 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = &H2)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Equivalent_integral_and_floating_Optional_defaults_fulfill_the_same_contract()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal ratio As Double = 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Double = 1#)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Unevaluable_Optional_default_evidence_suppresses_a_conclusive_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal count As Long = MissingDefault)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal amount As Long = 3)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Empty_Optional_default_evidence_suppresses_a_conclusive_mismatch()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();

        const string interfaceUri = "file:///C:/work/IWorker.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IWorker"
            Public Sub Run(Optional ByVal value As Variant = Empty)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/Worker.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "Worker"
            Implements IWorker
            Private Sub IWorker_Run(Optional ByVal actual As Variant = 1)
            End Sub
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        Assert.DoesNotContain(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            IsInterfaceFulfillmentDiagnostic);

        await process.ShutdownAsync(2);
    }

    [Fact]
    public async Task Function_result_array_shape_participates_in_contract_fulfillment()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync(new
        {
            textDocument = new
            {
                publishDiagnostics = new
                {
                    relatedInformation = true
                }
            }
        });

        const string interfaceUri = "file:///C:/work/IArray.cls";
        const string interfaceText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "IArray"
            Public Function Values() As Long()
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(interfaceUri, interfaceText));
        await process.WaitForDiagnosticsAsync(interfaceUri);

        const string implementationUri = "file:///C:/work/ArrayProvider.cls";
        const string implementationText = """
            VERSION 1.0 CLASS
            Attribute VB_Name = "ArrayProvider"
            Implements IArray
            Private Function IArray_Values() As Long
            End Function
            """;
        await process.SendNotificationAsync(
            "textDocument/didOpen",
            CreateOpenDocument(implementationUri, implementationText));

        var notification = await process.WaitForDiagnosticsAsync(implementationUri);
        var diagnostic = Assert.Single(
            notification
                .GetProperty("params")
                .GetProperty("diagnostics")
                .EnumerateArray(),
            candidate => candidate.GetProperty("code").GetString()
                == "validation.incompatibleInterfaceMemberSignature");
        Assert.Equal(
            "Interface member 'IArray_Values' signature does not match any required Function contract.",
            diagnostic.GetProperty("message").GetString());
        var related = Assert.Single(
            diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(
            "Required contract: Function IArray_Values() As Long(). "
                + "Mismatches: return array shape: expected array, found scalar.",
            related.GetProperty("message").GetString());

        await process.ShutdownAsync(2);
    }

    private static object CreateOpenDocument(string uri, string text)
        => new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version = 1,
                text
            }
        };

    private static void WriteReferenceCatalogProjectManifest(
        string projectRoot,
        string referenceName)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "InterfaceReferenceCatalogProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = "src/Book1",
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = new[]
                    {
                        new { name = referenceName, requested = true }
                    }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static VbaProjectReferenceCatalogIdentity
        CreateGeneratedReferenceCatalogIdentity(string referenceName)
        => new(
            referenceName,
            "{55555555-5555-5555-5555-555555555555}",
            1,
            0,
            0,
            @"C:\TypeLibs\GeneratedInterfaces.tlb");

    private static bool IsInterfaceFulfillmentDiagnostic(System.Text.Json.JsonElement diagnostic)
        => diagnostic.GetProperty("code").GetString() is
            "validation.interfaceMemberNotImplemented"
                or "validation.interfaceMemberKindMismatch"
                or "validation.incompatibleInterfaceMemberSignature"
                or "validation.interfaceMemberContractNotFullyImplemented";

    private static Task<System.Text.Json.JsonElement> SendPositionRequestAsync(
        LanguageServerProcessHarness process,
        int id,
        string method,
        string uri,
        string text,
        string needle)
    {
        var characterOffset = text.IndexOf(needle, StringComparison.Ordinal);
        var prefix = text[..characterOffset];
        var line = prefix.Count(character => character == '\n');
        var lineStart = prefix.LastIndexOf('\n');
        var character = lineStart < 0
            ? characterOffset
            : characterOffset - lineStart - 1;
        return process.SendRequestAsync(
            id,
            method,
            new
            {
                textDocument = new { uri },
                position = new { line, character }
            });
    }
}
