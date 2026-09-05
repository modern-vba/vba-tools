using VbaTools.Syntax;

namespace VbaDev.App.Workbooks;

internal static class VbaPublishExclusionMarker
{
    private const int ScanLineLimit = 32;
    private const string Marker = "'#ExcludePublish";

    internal static bool IsPresent(string text)
        => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Take(ScanLineLimit)
            .Any(line => VbaIdentifier.TrimStartWhitespace(line)
                .StartsWith(Marker, StringComparison.OrdinalIgnoreCase));
}
