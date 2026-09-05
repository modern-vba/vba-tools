using Xunit;

namespace VbaTools.Integration.Tests;

public sealed class WindowsExcelIntegrationFactAttribute : FactAttribute
{
    public WindowsExcelIntegrationFactAttribute()
    {
        Timeout = 360_000;
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1 to run Windows Excel integration tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsExcelIntegrationCollection
{
    public const string Name = "Windows Excel Integration";
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path) => Path = path;

    public string Path { get; }

    public static TempDirectory Create()
        => new(Directory.CreateTempSubdirectory("vba-tools-integration-tests-").FullName);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
