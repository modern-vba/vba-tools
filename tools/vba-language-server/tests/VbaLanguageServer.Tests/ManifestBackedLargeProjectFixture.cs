using System.Text;
using System.Text.Json;

namespace VbaLanguageServer.Tests;

internal sealed class ManifestBackedLargeProjectFixture : IDisposable
{
    private const int CallerDocumentCount = 95;
    private const int CallsPerCaller = 450;
    private readonly string projectRoot;

    private ManifestBackedLargeProjectFixture(
        string projectRoot,
        string activeUri,
        string activeText)
    {
        this.projectRoot = projectRoot;
        ActiveUri = activeUri;
        ActiveText = activeText;
    }

    public string ActiveUri { get; }

    public string ActiveText { get; }

    public static ManifestBackedLargeProjectFixture Create()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-large-project-").FullName;
        try
        {
            var sourceRoot = Directory.CreateDirectory(Path.Combine(
                projectRoot,
                "src",
                "Book1")).FullName;
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    projectName = "LargeProjectFixture",
                    primaryDocument = "Book1",
                    documents = new Dictionary<string, object>
                    {
                        ["Book1"] = new
                        {
                            kind = "excel",
                            sourcePath = "src/Book1",
                            templatePath = "Book1.xlsm",
                            binPath = "bin/Book1.xlsm",
                            publishPath = "publish/Book1.xlsm",
                            commonModules = Array.Empty<object>(),
                            references = Array.Empty<object>()
                        }
                    }
                }));
            File.WriteAllText(
                Path.Combine(sourceRoot, "TargetModule.bas"),
                "Attribute VB_Name = \"TargetModule\"\n"
                    + "Option Explicit\n"
                    + "Public Function ResolveValue(ByVal Value As Long) As Long\n"
                    + "    ResolveValue = Value\n"
                    + "End Function\n");

            string? activePath = null;
            string? activeText = null;
            for (var callerIndex = 1;
                callerIndex <= CallerDocumentCount;
                callerIndex++)
            {
                var text = CreateCallerText(callerIndex);
                var path = Path.Combine(
                    sourceRoot,
                    $"Caller{callerIndex:D3}.bas");
                File.WriteAllText(path, text);
                if (callerIndex == 1)
                {
                    activePath = path;
                    activeText = text;
                }
            }

            return new ManifestBackedLargeProjectFixture(
                projectRoot,
                new Uri(activePath!).AbsoluteUri,
                activeText!);
        }
        catch
        {
            Directory.Delete(projectRoot, recursive: true);
            throw;
        }
    }

    public void Dispose()
        => Directory.Delete(projectRoot, recursive: true);

    private static string CreateCallerText(int callerIndex)
    {
        var source = new StringBuilder();
        source.Append("Attribute VB_Name = \"Caller")
            .Append(callerIndex.ToString("D3"))
            .Append("\"\n")
            .Append("Option Explicit\n")
            .Append("Public Sub Run")
            .Append(callerIndex.ToString("D3"))
            .Append("()\n")
            .Append("    Dim result As Long\n");
        for (var callIndex = 0;
            callIndex < CallsPerCaller;
            callIndex++)
        {
            source.Append("    result = ResolveValue(")
                .Append(callIndex)
                .Append(")\n");
        }

        return source.Append("End Sub\n").ToString();
    }
}
