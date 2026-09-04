using VbaLanguageServer.Lsp;
using VbaLanguageServer.Processes;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevReferenceListStartupStateTests
{
    [Fact]
    public async Task Supplied_absolute_executable_is_validated_once_and_pinned_exactly()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var calls = new List<IReadOnlyList<string>>();

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (arguments, _) =>
            {
                calls.Add(arguments);
                return Task.FromResult(new VbaDevProcessInvocationResult(
                    0,
                    """
                    {
                      "commands": {
                        "reference list": {
                          "outputSchemaVersion": "1.0"
                        }
                      }
                    }
                    """,
                    ""));
            });

        Assert.True(state.IsAvailable);
        Assert.Equal(executablePath, state.ExecutablePath);
        Assert.Null(state.WarningMessage);
        var call = Assert.Single(calls);
        Assert.Equal(["capabilities", "--format", "json"], call);
    }

    [Fact]
    public async Task Stdio_transport_argument_can_precede_or_follow_the_companion_pair()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var argumentSets = new[]
        {
            new[] { "--stdio", "--vba-dev", executablePath },
            new[] { "--vba-dev", executablePath, "--stdio" }
        };

        foreach (var arguments in argumentSets)
        {
            var state = await VbaDevReferenceListStartupState.ResolveAsync(
                arguments,
                (_, _) => Task.FromResult(new VbaDevProcessInvocationResult(
                    0,
                    """
                    {"commands":{"reference list":{"outputSchemaVersion":"1.0"}}}
                    """,
                    "")));

            Assert.True(state.IsAvailable);
            Assert.Equal(executablePath, state.ExecutablePath);
        }
    }

    public static IEnumerable<object[]> InvalidStartupArguments()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        yield return [Array.Empty<string>()];
        yield return [new[] { "--vba-dev" }];
        yield return [new[] { "--vba-dev", "vba-dev.exe" }];
        yield return [new[] { "--other", executablePath }];
        yield return [new[] { "--stdio", "--stdio", "--vba-dev", executablePath }];
        yield return [new[] { "--vba-dev", executablePath, "--vba-dev", executablePath }];
    }

    [Theory]
    [MemberData(nameof(InvalidStartupArguments))]
    public async Task Missing_duplicate_or_invalid_arguments_never_start_a_process(
        string[] arguments)
    {
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            arguments,
            (_, _) =>
            {
                processCalls++;
                throw new InvalidOperationException("The process must not start.");
            });

        Assert.False(state.IsAvailable);
        Assert.Null(state.ExecutablePath);
        Assert.Contains("one absolute --vba-dev executable path", state.WarningMessage);
        Assert.Equal(0, processCalls);
    }

    public static IEnumerable<object[]> UnsupportedCapabilities()
    {
        yield return [1, "{}"];
        yield return [0, "not-json"];
        yield return [0, "{}"];
        yield return [0, """{"commands":{}}"""];
        yield return [0, """{"commands":{"reference list":{}}}"""];
        yield return [0, """{"commands":{"reference list":{"outputSchemaVersion":"0.9"}}}"""];
        yield return [0, """{"commands":{"reference list":{"outputSchemaVersion":"1.0"}},"commands":{"reference list":{"outputSchemaVersion":"1.0"}}}"""];
        yield return [0, """{"commands":{"reference list":{"outputSchemaVersion":"0.9"},"reference list":{"outputSchemaVersion":"1.0"}}}"""];
        yield return [0, """{"commands":{"reference list":{"outputSchemaVersion":"0.9","outputSchemaVersion":"1.0"}}}"""];
    }

    [Theory]
    [MemberData(nameof(UnsupportedCapabilities))]
    public async Task Failed_or_incompatible_capability_probe_disables_cli_backed_state(
        int exitCode,
        string standardOutput)
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(new VbaDevProcessInvocationResult(
                    exitCode,
                    standardOutput,
                    "probe error"));
            });

        Assert.False(state.IsAvailable);
        Assert.Null(state.ExecutablePath);
        Assert.Contains("registry-only discovery remains available", state.WarningMessage);
        Assert.Equal(1, processCalls);
    }

    [Fact]
    public async Task Unavailable_supplied_executable_disables_cli_backed_state_after_one_attempt()
    {
        var executablePath = Path.GetFullPath(Path.Combine("missing", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                throw new FileNotFoundException("The executable does not exist.");
            });

        Assert.False(state.IsAvailable);
        Assert.Null(state.ExecutablePath);
        Assert.Contains(executablePath, state.WarningMessage);
        Assert.Contains("registry-only discovery remains available", state.WarningMessage);
        Assert.Equal(1, processCalls);
    }

    [Fact]
    public void DefaultRuntimeUsesPinnedCliFactoryOnlyForValidatedStartupState()
    {
        var registryDiscovery = new StubRegistryDiscovery();
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));

        var available = VbaLanguageServerRuntime.CreateReferenceCatalogDiscovery(
            registryDiscovery,
            new VbaDevReferenceListStartupState(executablePath, null));
        var unavailable = VbaLanguageServerRuntime.CreateReferenceCatalogDiscovery(
            registryDiscovery,
            new VbaDevReferenceListStartupState(
                null,
                "CLI-backed reference catalog resolution is disabled."));

        Assert.True(available.IsCompanionPinned);
        Assert.False(unavailable.IsCompanionPinned);
    }

    private sealed class StubRegistryDiscovery : IVbaProjectReferenceCatalogDiscovery
    {
        public Task<VbaProjectReferenceCatalogDiscoveryResult> DiscoverAsync(
            string referenceName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VbaProjectReferenceCatalogDiscoveryResult.Failure(
                referenceName,
                "No registry result is needed for this factory-selection test."));
    }
}
