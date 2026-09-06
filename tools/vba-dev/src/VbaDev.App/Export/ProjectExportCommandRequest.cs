namespace VbaDev.App.Export;

/// <summary>
/// Carries a resolved project document export destination.
/// </summary>
/// <param name="DestinationDirectory">The optional destination directory.</param>
/// <param name="WorkingDirectory">The directory used to resolve a relative destination.</param>
public sealed record ProjectExportCommandRequest(
    string? DestinationDirectory,
    string WorkingDirectory);
