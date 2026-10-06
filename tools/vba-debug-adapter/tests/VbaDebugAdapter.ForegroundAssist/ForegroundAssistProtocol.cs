namespace VbaDebugAdapter.ForegroundAssist;

public static class ForegroundAssistProtocol
{
    public static async Task RunAsync(
        TextReader input,
        TextWriter output,
        ForegroundAssistLoop assist)
    {
        output.WriteLine("READY");
        output.Flush();

        // Console.In.ReadLineAsync may block synchronously on Windows. Keep that read
        // off the foreground-assist loop, and signal readiness before starting it.
        var stop = Task.Run(input.ReadLine);
        while (!stop.IsCompleted)
        {
            assist.TryOneCycle();
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        await stop;
    }
}
