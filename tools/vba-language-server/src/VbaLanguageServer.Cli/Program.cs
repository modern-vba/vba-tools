using VbaLanguageServer.Lsp;

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine("vba-language-server 0.1.0");
    return;
}

var server = VbaLanguageServerRuntime.CreateDefault(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());
await server.RunAsync();
