using System.Text;
using VbaDev.App.Cli;
using VbaDev.Cli;

try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    VbaDevProcessStartupEvidence.TryWriteFromEnvironment();
    using var standardInput = Console.OpenStandardInput();
    return await VbaDevCommandLine
        .CreateDefault()
        .InvokeAsync(
            args,
            standardInput,
            Console.Out,
            Console.Error,
            CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(CommandErrorMessages.UnexpectedFailure(ex));
    return 1;
}
