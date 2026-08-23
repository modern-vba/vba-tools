namespace VbaDebugAdapter.Cli;

internal static class Program
{
    public static Task<int> Main(string[] args)
        => VbaDebugAdapterCommandLine.Create().InvokeAsync(
            args,
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            Console.OpenStandardError(),
            CancellationToken.None);
}
