using VbaDev.App.FileSystem;

namespace VbaDev.Infrastructure.FileSystem;

/// <summary>
/// Opens exact Windows ownership sessions without sharing invocation state.
/// </summary>
public sealed class WindowsExactFileSystemObjectOwnershipFactory : IExactFileSystemObjectOwnershipFactory
{
    /// <inheritdoc />
    public ExactFileSystemObjectOwnership Open()
        => WindowsExactFileSystemObjectOwnership.Open();
}
