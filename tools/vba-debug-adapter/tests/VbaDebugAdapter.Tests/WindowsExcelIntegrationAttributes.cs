using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class WindowsExcelIntegrationFactAttribute : FactAttribute
{
    private const string OptInEnvironmentVariable =
        "VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS";

    public WindowsExcelIntegrationFactAttribute()
    {
        Timeout = 360_000;
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInEnvironmentVariable}=1 to run Windows Excel integration tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsExcelIntegrationCollection
{
    public const string Name = "Windows Excel Integration";
}
