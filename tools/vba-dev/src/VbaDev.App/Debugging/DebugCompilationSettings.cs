using System.Collections.ObjectModel;

namespace VbaDev.App.Debugging;

/// <summary>
/// Identifies the VBA project platform recorded in the generated workbook.
/// </summary>
public enum VbaProjectSystemKind
{
    Win16 = 0,
    Win32 = 1,
    Macintosh = 2,
    Win64 = 3
}

/// <summary>
/// Contains compiler settings read from the exact generated workbook VBA project.
/// </summary>
public sealed class DebugCompilationSettings
{
    /// <summary>
    /// Creates immutable, case-insensitive project compilation settings.
    /// </summary>
    public DebugCompilationSettings(
        VbaProjectSystemKind systemKind,
        int codePage,
        IEnumerable<KeyValuePair<string, short>> projectConstants,
        string vbaProjectPartSha256)
    {
        ArgumentNullException.ThrowIfNull(projectConstants);
        if (codePage is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(codePage));
        }

        if (vbaProjectPartSha256.Length != 64
            || !vbaProjectPartSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The VBA project part fingerprint must be a SHA-256 hexadecimal digest.",
                nameof(vbaProjectPartSha256));
        }

        var constants = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
        foreach (var constant in projectConstants)
        {
            if (string.IsNullOrWhiteSpace(constant.Key)
                || !constants.TryAdd(constant.Key, constant.Value))
            {
                throw new ArgumentException(
                    $"VBA project compiler constant '{constant.Key}' is invalid or duplicated.",
                    nameof(projectConstants));
            }
        }

        SystemKind = systemKind;
        CodePage = codePage;
        ProjectConstants = new ReadOnlyDictionary<string, short>(constants);
        VbaProjectPartSha256 = vbaProjectPartSha256.ToUpperInvariant();
    }

    public VbaProjectSystemKind SystemKind { get; }

    public int CodePage { get; }

    public IReadOnlyDictionary<string, short> ProjectConstants { get; }

    public string VbaProjectPartSha256 { get; }
}

/// <summary>
/// Reads compiler settings from the exact generated workbook artifact.
/// </summary>
public interface IDebugCompilationSettingsReader
{
    DebugCompilationSettings Read(string workbookPath);
}
