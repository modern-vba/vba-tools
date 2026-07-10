using VbaDev.Composition;
using VbaDev.App.Cli;

try
{
    var application = ToolingCompositionRoot.CreateCommandLineApplication();
    var result = application.Run(args);

    if (!string.IsNullOrEmpty(result.StandardOutput))
    {
        Console.Out.Write(result.StandardOutput);
    }

    if (!string.IsNullOrEmpty(result.StandardError))
    {
        Console.Error.Write(result.StandardError);
    }

    return result.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine(CommandErrorMessages.UnexpectedFailure(ex));
    return 1;
}
