using System.Text.Json;
using VbaLanguageServer.Lsp;
using VbaTools.Processes;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDevReferenceListStartupStateTests
{
    [Fact]
    public async Task UniqueAdditiveOffersKeepOnlyTheReferenceListRequirementAndPinnedExecutable()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;
        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(new ProcessInvocationResult(0,
                    """
                    {"future":[{"value":1},{"value":2}],"contractVersion":"99.0","commands":{"future command":{"outputSchemaVersion":"99.0"},"reference list":{"future":[1,1],"outputSchemaVersion":"1.0"}},"featureVersions":{"future":"99.0"}}
                    """, ""));
            });

        Assert.True(state.IsAvailable);
        Assert.Equal(executablePath, state.ExecutablePath);
        Assert.Null(state.WarningMessage);
        Assert.Equal(1, processCalls);
        Assert.True(VbaLanguageServerRuntime.CreateReferenceCatalogDiscovery(new StubRegistryDiscovery(), state).IsCompanionPinned);
    }

    [Fact]
    public async Task WholeResponseDuplicateRejectionKeepsRegistryDiscoveryAvailable()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;
        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(new ProcessInvocationResult(0,
                    """
                    {"commands":{"reference list":{"outputSchemaVersion":"1.0"}},"future":{"nested":[{"value":1,"value":1}]}}
                    """, ""));
            });

        Assert.False(state.IsAvailable);
        Assert.Null(state.ExecutablePath);
        Assert.Contains("registry-only discovery remains available", state.WarningMessage);
        Assert.Equal(1, processCalls);
        Assert.False(VbaLanguageServerRuntime.CreateReferenceCatalogDiscovery(new StubRegistryDiscovery(), state).IsCompanionPinned);
    }

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
                return Task.FromResult(new ProcessInvocationResult(
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
                (_, _) => Task.FromResult(new ProcessInvocationResult(
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
                return Task.FromResult(new ProcessInvocationResult(
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
    public async Task Fatal_capability_probe_exit_retries_once_with_a_new_invocation()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(processCalls == 1
                    ? new ProcessInvocationResult(unchecked((int)0xC0000005), "private output", "private error")
                    : new ProcessInvocationResult(0,
                        """{"commands":{"reference list":{"outputSchemaVersion":"1.0"}}}""", ""));
            });

        Assert.True(state.IsAvailable);
        Assert.Equal(executablePath, state.ExecutablePath);
        Assert.Contains("0xC0000005", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("second attempt", state.WarningMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", state.WarningMessage, StringComparison.Ordinal);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public async Task Repeated_fatal_capability_probe_exit_reports_both_attempts_without_output()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(new ProcessInvocationResult(
                    unchecked((int)0xC0000409), "private output", "private error"));
            });

        Assert.False(state.IsAvailable);
        Assert.Contains("0xC0000409", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("2 attempts", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("registry-only discovery remains available", state.WarningMessage);
        Assert.DoesNotContain("private", state.WarningMessage, StringComparison.Ordinal);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public async Task Ordinary_failure_after_a_fatal_exit_stops_after_the_second_attempt()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                return Task.FromResult(processCalls == 1
                    ? new ProcessInvocationResult(unchecked((int)0xC0000005), "", "")
                    : new ProcessInvocationResult(1, "private output", "private error"));
            });

        Assert.False(state.IsAvailable);
        Assert.Contains(executablePath, state.WarningMessage);
        Assert.Contains("0xC0000005", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("second attempt exited with code 1", state.WarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private", state.WarningMessage, StringComparison.Ordinal);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public async Task Retry_exception_preserves_first_crash_without_a_third_attempt()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                if (processCalls == 2)
                {
                    throw new InvalidOperationException("private launch detail");
                }

                return Task.FromResult(new ProcessInvocationResult(
                    unchecked((int)0xC0000005), "", ""));
            });

        Assert.False(state.IsAvailable);
        Assert.Contains("0xC0000005", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("2 attempts", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", state.WarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private", state.WarningMessage, StringComparison.Ordinal);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public async Task Unproved_retry_cleanup_preserves_first_crash_without_a_third_attempt()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                if (processCalls == 2)
                {
                    throw new ProcessLifecycleException(
                        executablePath,
                        new InvalidOperationException("private original"),
                        null,
                        new TimeoutException("private cleanup"));
                }

                return Task.FromResult(new ProcessInvocationResult(
                    unchecked((int)0xC0000005), "", ""));
            });

        Assert.False(state.IsAvailable);
        Assert.Contains("0xC0000005", state.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("could not prove process cleanup", state.WarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private", state.WarningMessage, StringComparison.Ordinal);
        Assert.Equal(2, processCalls);
    }

    [Fact]
    public async Task Cancellation_after_fatal_probe_exit_prevents_retry()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        using var cancellation = new CancellationTokenSource();
        var processCalls = 0;
        var diagnostics = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VbaDevReferenceListStartupState.ResolveAsync(
                ["--vba-dev", executablePath],
                (_, _) =>
                {
                    processCalls++;
                    cancellation.Cancel();
                    return Task.FromResult(new ProcessInvocationResult(
                        unchecked((int)0xC0000005), "private stdout", "private stderr"));
                },
                cancellation.Token,
                diagnostics.Add));

        Assert.Equal(1, processCalls);
        var diagnostic = Assert.Single(diagnostics);
        using var json = JsonDocument.Parse(diagnostic);
        var observed = json.RootElement;
        Assert.Equal("companion-process-abnormal-termination", observed.GetProperty("eventType").GetString());
        Assert.Equal("vba-dev", observed.GetProperty("role").GetString());
        Assert.Equal("capabilities", observed.GetProperty("stage").GetString());
        Assert.Equal(executablePath, observed.GetProperty("executablePath").GetString());
        Assert.Equal("1/2", observed.GetProperty("attempt").GetString());
        Assert.Equal(unchecked((int)0xC0000005), observed.GetProperty("exitCode").GetInt32());
        Assert.Equal("0xC0000005", observed.GetProperty("exitCodeHex").GetString());
        Assert.Equal("retry-eligible", observed.GetProperty("outcome").GetString());
        Assert.False(observed.GetProperty("retryPerformed").GetBoolean());
        Assert.DoesNotContain("private", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unproved_process_cleanup_is_never_retried()
    {
        var executablePath = Path.GetFullPath(Path.Combine("tools", "vba-dev.exe"));
        var processCalls = 0;

        var state = await VbaDevReferenceListStartupState.ResolveAsync(
            ["--vba-dev", executablePath],
            (_, _) =>
            {
                processCalls++;
                throw new ProcessLifecycleException(
                    executablePath,
                    new InvalidOperationException("original failure"),
                    null,
                    new TimeoutException("cleanup not proved"));
            });

        Assert.False(state.IsAvailable);
        Assert.Contains("could not be validated", state.WarningMessage);
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
