namespace VbaDev.Cli.Debugging;

internal static class DebugAdapterCommandLine
{
    public const string Usage = "Usage: vba-dev debug-adapter --stdio";

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Count > 0 &&
            args[0].Equals("debug-adapter", StringComparison.OrdinalIgnoreCase);

    public static string? Validate(IReadOnlyList<string> args)
        => args.Count == 2 &&
            args[0].Equals("debug-adapter", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("--stdio", StringComparison.OrdinalIgnoreCase)
                ? null
                : Usage;
}
