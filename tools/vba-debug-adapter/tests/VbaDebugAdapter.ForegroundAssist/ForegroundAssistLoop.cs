namespace VbaDebugAdapter.ForegroundAssist;

public interface IForegroundAssistProbe
{
    IReadOnlyList<int> CaptureExcelProcessIds();

    bool IsDebugWorkbookProcess(int processId);

    bool TryBringVbeToForeground(int processId);
}

public sealed class ForegroundAssistLoop(
    IReadOnlySet<int> baselineExcelProcessIds,
    IForegroundAssistProbe probe)
{
    public bool TryOneCycle()
    {
        var matches = probe.CaptureExcelProcessIds()
            .Except(baselineExcelProcessIds)
            .Where(probe.IsDebugWorkbookProcess)
            .Order()
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple new Excel processes hold matching debug workbooks: {string.Join(", ", matches)}.");
        }

        return matches.Length == 1 && probe.TryBringVbeToForeground(matches[0]);
    }
}
