namespace VbaDev.App.CommonModules;

/// <summary>
/// Reports a recoverable failure while applying CommonModules file or manifest changes.
/// </summary>
public sealed class CommonModulesTransactionException : Exception
{
    /// <summary>
    /// Creates a transaction exception with the recovery message for the user.
    /// </summary>
    /// <param name="message">The error or recovery message to return from the command.</param>
    public CommonModulesTransactionException(string message)
        : base(message)
    {
    }
}
