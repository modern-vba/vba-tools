using VbaDev.App.Cli;
using VbaDev.Cli;

try
{
    return await VbaDevCommandLine
        .CreateDefault()
        .InvokeAsync(args, Console.Out, Console.Error, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(CommandErrorMessages.UnexpectedFailure(ex));
    return 1;
}
