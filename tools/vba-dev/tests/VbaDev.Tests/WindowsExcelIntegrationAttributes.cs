using Xunit;

namespace VbaDev.Tests;

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

public sealed class PrivateDesktopExcelFeasibilityFactAttribute : FactAttribute
{
    public const string Category = "PrivateDesktopExcelFeasibility";
    public const string OptInEnvironmentVariable =
        "VBA_TOOLS_RUN_PRIVATE_DESKTOP_EXCEL_FEASIBILITY_TESTS";

    public PrivateDesktopExcelFeasibilityFactAttribute()
    {
        Timeout = 360_000;
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInEnvironmentVariable}=1 to run private-desktop Excel feasibility tests.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsExcelIntegrationCollection
{
    public const string Name = "Windows Excel Integration";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PrivateDesktopExcelFeasibilityCollection
{
    public const string Name = "Private Desktop Excel Feasibility";
}
