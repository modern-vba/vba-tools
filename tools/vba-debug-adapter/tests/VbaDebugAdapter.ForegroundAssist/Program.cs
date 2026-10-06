namespace VbaDebugAdapter.ForegroundAssist;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The Extension Host foreground assist requires Windows.");
            return 2;
        }

        if (args.Length != 2 || !string.Equals(Path.GetFileName(args[0]), args[0],
                StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(args[0]), ".xlsm",
                StringComparison.OrdinalIgnoreCase) ||
            !DebugWorkbookIdentity.IsCanonicalLowerHexId(args[1]))
        {
            Console.Error.WriteLine("Expected a path-free debug workbook .xlsm file name and fixture owner token.");
            return 2;
        }

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "vba-debug-adapter", "workspaces");
        var probe = new WindowsForegroundAssistProbe(workspaceRoot, args[0], args[1]);
        var assist = new ForegroundAssistLoop(
            probe.CaptureExcelProcessIds().ToHashSet(), probe);
        try
        {
            await ForegroundAssistProtocol.RunAsync(Console.In, Console.Out, assist);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Foreground assist failed: {exception}");
            return 3;
        }
    }
}
