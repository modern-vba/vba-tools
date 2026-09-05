using VbaDebugAdapter.Debugging;
using VbaTools.ProjectMetadata;
using PackageSystemKind = VbaTools.ProjectMetadata.VbaProjectSystemKind;
using VbaProjectSystemKind = VbaDebugAdapter.Debugging.VbaProjectSystemKind;

namespace VbaDebugAdapter.Infrastructure;

/// <summary>
/// Reads the persisted VBA compilation settings from an exact generated .xlsm artifact.
/// </summary>
public sealed class OpenXmlDebugCompilationSettingsReader
    : IDebugCompilationSettingsReader
{
    /// <inheritdoc />
    public DebugCompilationSettings Read(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        if (!Path.GetExtension(workbookPath).Equals(".xlsm", StringComparison.OrdinalIgnoreCase))
        {
            throw new DebugSetupException(
                $"Debug compilation settings require an .xlsm workbook: '{workbookPath}'.");
        }

        try
        {
            using var workbook = File.Open(
                workbookPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var packageLength = workbook.Length;
            if (packageLength is <= 0 or > VbaProjectPackageMetadataReader.MaximumPackageLength)
            {
                throw new DebugSetupException(
                    $"Could not read VBA compilation settings from generated workbook '{workbookPath}' " +
                    "(InvalidPackage): The workbook package has an invalid or excessive length.");
            }

            var capturedPackage = new byte[checked((int)packageLength)];
            workbook.ReadExactly(capturedPackage);
            var result = new VbaProjectPackageMetadataReader().Read(capturedPackage);
            if (result.Failure is { } failure)
            {
                throw new DebugSetupException(
                    $"Could not read VBA compilation settings from generated workbook '{workbookPath}' " +
                    $"({failure.Kind}): {failure.Message}");
            }

            var metadata = result.Metadata!;
            var systemKind = metadata.SystemKind switch
            {
                PackageSystemKind.Win16 => VbaProjectSystemKind.Win16,
                PackageSystemKind.Win32 => VbaProjectSystemKind.Win32,
                PackageSystemKind.Mac => VbaProjectSystemKind.Macintosh,
                PackageSystemKind.Win64 => VbaProjectSystemKind.Win64,
                _ => throw new DebugSetupException(
                    "The generated workbook declares an unsupported VBA project system kind.")
            };
            return new DebugCompilationSettings(
                systemKind,
                metadata.CodePage,
                metadata.ProjectConstants,
                metadata.VbaProjectPartContentIdentity.Sha256);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new DebugSetupException(
                $"Could not read VBA compilation settings from generated workbook '{workbookPath}'.",
                exception);
        }
    }
}
