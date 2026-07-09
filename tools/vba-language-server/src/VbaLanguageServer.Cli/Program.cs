using VbaLanguageServer.Lsp;

var server = VbaLanguageServerRuntime.CreateDefault(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());
await server.RunAsync();
