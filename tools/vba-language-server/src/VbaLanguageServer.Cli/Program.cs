using VbaLanguageServer.Lsp;

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine("vba-language-server 0.1.0");
    return;
}

var vbaDevStartupState = await VbaDevReferenceListStartupState.ResolveAsync(args);
var server = VbaLanguageServerRuntime.CreateDefault(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    vbaDevStartupState);
await server.RunAsync();
