namespace VbaDev.App.CommonModules;

public sealed class CommonModulesTransactionException : Exception
{
    public CommonModulesTransactionException(string message)
        : base(message)
    {
    }
}
