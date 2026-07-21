using VbaDev.Composition;
using VbaDev.App.Cli;
using VbaDev.Cli.Debugging;

try
{
    if (DebugAdapterCommandLine.IsRequested(args))
    {
        var usageError = DebugAdapterCommandLine.Validate(args);
        if (usageError is not null)
        {
            Console.Error.WriteLine(usageError);
            return 1;
        }

        var composition = ToolingCompositionRoot.CreateDebugAdapterComposition();
        var adapter = new VbaDebugAdapter(
            composition.ProjectContextResolver,
            composition.LaunchCoordinator,
            () => composition.WorkingDirectory);
        await adapter.RunAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            CancellationToken.None);
        return 0;
    }

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
