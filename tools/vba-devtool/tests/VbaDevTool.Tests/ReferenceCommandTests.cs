using System.Text;
using System.Text.Json;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class ReferenceCommandTests
{
    [Fact]
    public void AddStoresMultipleTrimmedReferencesWithoutDuplicates()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        var first = application.Run(["reference", "add", " Microsoft Scripting Runtime ", "Microsoft VBScript Regular Expressions 5.5"]);
        var second = application.Run(["reference", "add", "microsoft scripting runtime"]);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(
            ["Microsoft Scripting Runtime", "Microsoft VBScript Regular Expressions 5.5"],
            manifest.Documents["Book1"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void RemoveDeletesCaseInsensitiveMatchesAndSucceedsForAbsentReferences()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("Microsoft Scripting Runtime"),
            new VbaProjectReference("Microsoft VBScript Regular Expressions 5.5"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        var result = application.Run(["reference", "remove", "microsoft scripting runtime", "Already Absent"]);

        Assert.Equal(0, result.ExitCode);
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Forms 2.0 Object Library", "--document", "SecondBook"]).ExitCode);

        var manifest = new JsonProjectManifestStore().Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        Assert.Equal(["Microsoft Scripting Runtime"], manifest.Documents["Book1"].References.Select(reference => reference.Name));
        Assert.Equal(["Microsoft Forms 2.0 Object Library"], manifest.Documents["SecondBook"].References.Select(reference => reference.Name));
    }

    [Fact]
    public void ListOutputsSelectedDocumentAsTextAndJson()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(
            temp,
            new VbaProjectReference("Microsoft Scripting Runtime"),
            new VbaProjectReference("Microsoft Forms 2.0 Object Library"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        var text = application.Run(["reference", "list"]);
        var json = application.Run(["reference", "list", "--format", "json"]);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Document: Book1", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Scripting Runtime", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Forms 2.0 Object Library", text.StandardOutput, StringComparison.Ordinal);

        Assert.Equal(0, json.ExitCode);
        using var parsed = JsonDocument.Parse(json.StandardOutput);
        Assert.Equal("Book1", parsed.RootElement.GetProperty("document").GetString());
        var references = parsed.RootElement.GetProperty("references");
        Assert.Equal("Microsoft Scripting Runtime", references[0].GetProperty("name").GetString());
        Assert.Equal("Microsoft Forms 2.0 Object Library", references[1].GetProperty("name").GetString());
    }

    [Fact]
    public void ReferenceCommandsDoNotMutateWorkbookFiles()
    {
        using var temp = TempDirectory.Create();
        var root = CreateProject(temp);
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(templatePath, "template workbook", new UTF8Encoding(false));
        File.WriteAllText(binPath, "bin workbook", new UTF8Encoding(false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        Assert.Equal(0, application.Run(["reference", "add", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "remove", "Microsoft Scripting Runtime"]).ExitCode);
        Assert.Equal(0, application.Run(["reference", "list"]).ExitCode);

        Assert.Equal("template workbook", File.ReadAllText(templatePath, Encoding.UTF8));
        Assert.Equal("bin workbook", File.ReadAllText(binPath, Encoding.UTF8));
    }

    private static string CreateProject(TempDirectory temp, params VbaProjectReference[] references)
    {
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "publish", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), "template", new UTF8Encoding(false));
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.AddRange(references);
        new JsonProjectManifestStore().Save(root, manifest);
        return root;
    }
}
