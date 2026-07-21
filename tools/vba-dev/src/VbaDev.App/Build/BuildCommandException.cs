namespace VbaDev.App.Build;

/// <summary>
/// Reports a user-facing failure while planning or generating a workbook.
/// </summary>
public sealed class BuildCommandException : Exception
{
    /// <summary>
    /// Creates a build exception with a user-facing message.
    /// </summary>
    /// <param name="message">The message to return from the command.</param>
    public BuildCommandException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a build exception that preserves the underlying I/O or automation failure.
    /// </summary>
    /// <param name="message">The message to return from the command.</param>
    /// <param name="innerException">The underlying exception that caused the build failure.</param>
    public BuildCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
