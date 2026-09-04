using VbaLanguageServer.Lsp;

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine("vba-language-server 0.1.0");
    return;
}

var hasNormalCompanionlessArguments = args.Length == 0
    || args is ["--stdio"];
Func<CancellationToken, Task<VbaDevReferenceListStartupState>>?
    vbaDevStartupResolver = hasNormalCompanionlessArguments
        ? null
        : cancellationToken =>
            VbaDevReferenceListStartupState.ResolveAsync(
                args,
                cancellationToken);
var server = VbaLanguageServerRuntime.CreateDefault(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    vbaDevStartupResolver: vbaDevStartupResolver);
await server.RunAsync();
