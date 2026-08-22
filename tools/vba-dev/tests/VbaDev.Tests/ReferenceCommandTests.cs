using System.Text;
using System.Text.Json;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaTools.TypeLibRegistry;
using Xunit;

namespace VbaDev.Tests;

public sealed class ReferenceCommandTests
{
    [Fact]
    public async Task AddStoresMultipleTrimmedReferencesWithoutDuplicates()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                    new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0),
                    new ResolvedVbaProjectReference("Microsoft VBScript Regular Expressions 5.5", "{3F4DACA7-160D-11D2-A8E9-00104B365C9F}", 5, 5))));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var firstExitCode = await application.InvokeAsync(
            ["reference", "add", " Microsoft Scripting Runtime ", "Microsoft VBScript Regular Expressions 5.5"],
            standardOutput,
            standardError,
            CancellationToken.None);
        var secondExitCode = await application.InvokeAsync(
            ["reference", "add", "microsoft scripting runtime"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, firstExitCode);
        Assert.Equal(0, secondExitCode);
        Assert.Empty(standardError.ToString());
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Microsoft Scripting Runtime", "Microsoft VBScript Regular Expressions 5.5"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void AddPlansEveryMissingNameBeforeMutationAndStoresTheRepresentativeRegistrySpelling()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "WIDGET LIBRARY",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                10,
                16));
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var failed = application.Run([
            "reference",
            "add",
            " widget library ",
            "Missing Library"]);

        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(["widget library", "Missing Library"], resolver.RequestedNames);
        var unchanged = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(unchanged.Documents["Book1"].References);

        var succeeded = application.Run(["reference", "add", " widget library "]);

        Assert.Equal(0, succeeded.ExitCode);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["WIDGET LIBRARY"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void AddRejectsAnIncompleteResolverBatchBeforeManifestMutation()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "First Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0))
        {
            OmittedRequestedNames = ["Second Library"]
        };
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run([
            "reference",
            "add",
            "First Library",
            "Second Library"]);

        Assert.Equal(1, result.ExitCode);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    [Fact]
    public void AddAlreadyPresentIsAnEnvironmentIndependentNoOpThatPreservesStoredSpelling()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("MiXeD Library"));
        var resolver = new FakeVbaProjectReferenceResolver
        {
            ThrowOnResolve = true
        };
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["reference", "add", " mixed library "]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(resolver.RequestedNames);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["MiXeD Library"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public async Task RemoveDeletesCaseInsensitiveMatchesAndSucceedsForAbsentReferences()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("Microsoft Scripting Runtime"),
            new VbaProjectReference("Microsoft VBScript Regular Expressions 5.5"));
        var resolver = new FakeVbaProjectReferenceResolver { ThrowOnResolve = true };
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                vbaProjectReferenceResolver: resolver));

        var result = await application.RunAsync(
            ["reference", "remove", "microsoft scripting runtime", "Already Absent"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(resolver.RequestedNames);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(["Microsoft VBScript Regular Expressions 5.5"], manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void ReferenceCommandsUsePrimaryDocumentByDefaultAndHonorExplicitDocument()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "src", "SecondBook"));
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0),
                new ResolvedVbaProjectReference("Microsoft Forms 2.0 Object Library", "{0D452EE1-E08F-101A-852E-02608C4D0BB4}", 2, 0)));

        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Forms 2.0 Object Library", "--document", "SecondBook"]).ExitCode);

        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(["Microsoft Scripting Runtime"], manifest.Documents["Book1"].References.Select(reference => reference.Name));
        Assert.Equal(["Microsoft Forms 2.0 Object Library"], manifest.Documents["SecondBook"].References.Select(reference => reference.Name));
    }

    [Fact]
    public async Task ListOutputsSelectedDocumentAsTextAndJson()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("Microsoft Scripting Runtime"),
            new VbaProjectReference("Microsoft Forms 2.0 Object Library"));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                    new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0),
                    new ResolvedVbaProjectReference("Microsoft Forms 2.0 Object Library", "{0D452EE1-E08F-101A-852E-02608C4D0BB4}", 2, 0))));

        var text = await application.RunAsync(["reference", "list"]);
        var json = await application.RunAsync(["reference", "list", "--format", "json"]);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains($"Project: {root}", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Document: Book1", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Scripting Runtime", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Forms 2.0 Object Library", text.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(0, json.ExitCode);
        using var parsed = JsonDocument.Parse(json.StandardOutput);
        Assert.Equal("1.0", parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("project", parsed.RootElement.GetProperty("scope").GetString());
        Assert.Equal(root, parsed.RootElement.GetProperty("project").GetString());
        Assert.Equal("Book1", parsed.RootElement.GetProperty("document").GetString());
        Assert.Equal("configured", parsed.RootElement.GetProperty("mode").GetString());
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
        Assert.Empty(parsed.RootElement.GetProperty("warnings").EnumerateArray());
        var references = parsed.RootElement.GetProperty("references");
        Assert.Equal("Microsoft Scripting Runtime", references[0].GetProperty("name").GetString());
        Assert.Equal("Microsoft Forms 2.0 Object Library", references[1].GetProperty("name").GetString());
        var identity = references[0].GetProperty("identity");
        Assert.Equal("420b2830-e718-11cf-893d-00a0c9054228", identity.GetProperty("guid").GetString());
        Assert.Equal(1, identity.GetProperty("major").GetInt32());
        Assert.Equal(0, identity.GetProperty("minor").GetInt32());
    }

    [Fact]
    public void ListRejectsAnIncompleteResolverBatchWithAControlledCommandFailure()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("First Library"),
            new VbaProjectReference("Second Library"));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "First Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0))
        {
            OmittedRequestedNames = ["Second Library"]
        };
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["reference", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Reference resolver returned an incomplete configured-reference batch.",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ListRejectsAnOutOfOrderResolverBatchInsteadOfMispairingIdentities()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("First Library"),
            new VbaProjectReference("Second Library"));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "First Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Second Library",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                2,
                0))
        {
            ReverseResolutionOrder = true
        };
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["reference", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Reference resolver returned an incomplete configured-reference batch.",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredListReportsNoUsableIdentityAndAggregatesMalformedWarnings()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Broken Library"));
        var resolver = new FakeVbaProjectReferenceResolver
        {
            RegisteredNamesWithoutUsableIdentity = ["Broken Library"],
            Warnings =
            [
                new TypeLibRegistryCatalogWarning(
                    "malformedRegistrationsSkipped",
                    "Skipped 2 malformed TypeLib registrations.",
                    2)
            ]
        };
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["reference", "list", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        Assert.True(parsed.RootElement.GetProperty("complete").GetBoolean());
        var warning = Assert.Single(parsed.RootElement.GetProperty("warnings").EnumerateArray());
        Assert.Equal("malformedRegistrationsSkipped", warning.GetProperty("code").GetString());
        var reference = Assert.Single(parsed.RootElement.GetProperty("references").EnumerateArray());
        Assert.Equal("Broken Library", reference.GetProperty("name").GetString());
        Assert.Equal("unavailable", reference.GetProperty("status").GetString());
        Assert.Equal("noUsableIdentity", reference.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void ConfiguredListSerializesUnsignedTypeLibVersionBoundaries()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp, new VbaProjectReference("Boundary Library"));
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                new ResolvedVbaProjectReference(
                    "Boundary Library",
                    "{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}",
                    0,
                    ushort.MaxValue)));

        var result = application.Run(["reference", "list", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var parsed = JsonDocument.Parse(result.StandardOutput);
        var identity = Assert.Single(
                parsed.RootElement.GetProperty("references").EnumerateArray())
            .GetProperty("identity");
        Assert.Equal("ffffffff-ffff-ffff-ffff-ffffffffffff", identity.GetProperty("guid").GetString());
        Assert.Equal(0, identity.GetProperty("major").GetInt32());
        Assert.Equal(ushort.MaxValue, identity.GetProperty("minor").GetInt32());
    }

    [Fact]
    public void IncompleteRegistryCatalogFailsClosedBeforeAddMutation()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
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
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run([
            "reference",
            "add",
            "Known Partial Library"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("registryCatalogIncomplete", result.StandardError, StringComparison.Ordinal);
        var manifest = new JsonProjectManifestStore().Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    [Fact]
    public void ReferenceCommandsDoNotMutateWorkbookFiles()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(templatePath, "template workbook", new UTF8Encoding(false));
        File.WriteAllText(binPath, "bin workbook", new UTF8Encoding(false));
        var application = CommandLineTestFactory.Create(root);

        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "remove", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "list"]).ExitCode);

        Assert.Equal("template workbook", File.ReadAllText(templatePath, Encoding.UTF8));
        Assert.Equal("bin workbook", File.ReadAllText(binPath, Encoding.UTF8));
    }

    [Fact]
    public void AddFailsForMissingResolvedReferenceWithoutMutatingManifest()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver());

        var result = application.Run(["reference", "add", "Missing Library"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not found", result.StandardError, StringComparison.OrdinalIgnoreCase);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    [Fact]
    public void AddFailsForAmbiguousResolvedReferenceWithoutMutatingManifest()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = CommandLineTestFactory.Create(
            root,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                new ResolvedVbaProjectReference("Ambiguous Library", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", 1, 0),
                new ResolvedVbaProjectReference("Ambiguous Library", "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}", 1, 0)));

        var result = application.Run(["reference", "add", "Ambiguous Library"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ambiguous", result.StandardError, StringComparison.OrdinalIgnoreCase);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Empty(manifest.Documents["Book1"].References);
    }

    private static string CreateProject(TempDirectory temp, params VbaProjectReference[] references)
    {
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "publish"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), "template", new UTF8Encoding(false));
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.AddRange(references);
        new JsonProjectManifestStore().Save(root, manifest);
        return root;
    }
}
