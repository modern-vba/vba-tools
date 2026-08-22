using VbaLanguageServer.Lsp;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevReferenceListStartupStateTests
{
    [Fact]
    public async Task Supplied_absolute_executable_is_validated_once_and_pinned_exactly()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var calls = new List<(string File, IReadOnlyList<string> Arguments)>();

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (file, arguments, _) =>
            {
                calls.Add((file, arguments));
                return Task.FromResult(new VbaDevCapabilitiesProcessResult(
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
        Assert.Equal(executablePath, call.File);
        Assert.Equal(["capabilities", "--format", "json"], call.Arguments);
    }

    public static IEnumerable<object[]> InvalidStartupArguments()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        yield return [Array.Empty<string>()];
        yield return [new[] { "--vba-dev" }];
        yield return [new[] { "--vba-dev", "vba-dev.exe" }];
        yield return [new[] { "--other", executablePath }];
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
            (_, _, _) =>
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
            (_, _, _) =>
            {
                processCalls++;
                return Task.FromResult(new VbaDevCapabilitiesProcessResult(
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
            (_, _, _) =>
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
}
