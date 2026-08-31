using System.Text;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Cli;

internal static class VbeImportWarningRenderer
{
    internal static string Render(VbeImportVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var output = new StringBuilder();
        foreach (var warning in report.Warnings)
        {
            output.Append("[WARN] ");
            output.Append(warning.Code);
            output.Append(": Imported component '");
            output.Append(warning.ComponentName);
            output.Append("' identifier casing (source -> VBE): ");
            output.Append(string.Join(
                "; ",
                warning.DistinctPairs.Select(pair =>
                    $"'{pair.SourceIdentifier}' -> '{pair.VbeIdentifier}'")));
            output.AppendLine(".");
        }

        return output.ToString();
    }
}
