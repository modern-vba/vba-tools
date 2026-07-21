using VbaDev.App.Debugging;

namespace VbaDev.Cli.Debugging;

internal static class DebugAdapterCommandLine
{
    public const string Usage =
        "Usage: vba-dev " + VbaDebugCapabilityContract.AdapterCommand + " --stdio";

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Count > 0 &&
            args[0].Equals(
                VbaDebugCapabilityContract.AdapterCommand,
                StringComparison.OrdinalIgnoreCase);

    public static string? Validate(IReadOnlyList<string> args)
        => args.Count == 2 &&
            args[0].Equals(
                VbaDebugCapabilityContract.AdapterCommand,
                StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("--stdio", StringComparison.OrdinalIgnoreCase)
                ? null
                : Usage;
}
