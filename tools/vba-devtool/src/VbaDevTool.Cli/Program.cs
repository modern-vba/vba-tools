using VbaDevTools.Composition;

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
