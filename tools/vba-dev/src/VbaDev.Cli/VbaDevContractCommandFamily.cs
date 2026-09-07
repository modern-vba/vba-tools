using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaDev.Cli;

/// <summary>
/// Owns side-effect-free executable contract leaves on the single vba-dev command graph.
/// </summary>
internal sealed class VbaDevContractCommandFamily
{
    private readonly string generatingExecutablePath;
    private readonly string releaseVersion;
    private readonly Func<IReadOnlyList<VbaDevCommandCapabilityRegistration>> getCapabilities;
    private readonly VbaDevCommandFamilyOwnership commandFamilyOwnership;

    private VbaDevContractCommandFamily(
        string generatingExecutablePath,
        string releaseVersion,
        Func<IReadOnlyList<VbaDevCommandCapabilityRegistration>> getCapabilities,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        this.generatingExecutablePath = generatingExecutablePath;
        this.releaseVersion = releaseVersion;
        this.getCapabilities = getCapabilities;
        this.commandFamilyOwnership = commandFamilyOwnership;
    }

    internal Command CompletionsCommand { get; private set; } = null!;

    internal Command CompletionScriptCommand { get; private set; } = null!;

    internal Command PowerShellCompletionCommand { get; private set; } = null!;

    internal Command CapabilitiesCommand { get; private set; } = null!;

    internal Option<string> CapabilitiesFormatOption { get; private set; } = null!;

    internal static VbaDevContractCommandFamily Create(
        string generatingExecutablePath,
        string releaseVersion,
        Func<IReadOnlyList<VbaDevCommandCapabilityRegistration>> getCapabilities,
        VbaDevCommandFamilyOwnership commandFamilyOwnership)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatingExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseVersion);
        ArgumentNullException.ThrowIfNull(getCapabilities);
        ArgumentNullException.ThrowIfNull(commandFamilyOwnership);
        return new VbaDevContractCommandFamily(
            generatingExecutablePath,
            releaseVersion,
            getCapabilities,
            commandFamilyOwnership);
    }

    internal void RegisterCompletions(RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        CompletionsCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "completions",
            "Generate shell completion setup.");
        CompletionScriptCommand = VbaDevCommandGrammar.AddCommand(
            CompletionsCommand,
            "script",
            "Write a shell completion registration script.");
        PowerShellCompletionCommand = VbaDevCommandGrammar.AddCommand(
            CompletionScriptCommand,
            "pwsh",
            "Write a PowerShell completion registration script.");
        PowerShellCompletionCommand.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.Write(
                PowerShellCompletionScriptRenderer.Render(generatingExecutablePath));
            return 0;
        });
        commandFamilyOwnership.Register(this, PowerShellCompletionCommand);
    }

    internal void RegisterCapabilities(RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        CapabilitiesCommand = VbaDevCommandGrammar.AddCommand(
            rootCommand,
            "capabilities",
            "Print the command contract supported by this executable.");
        CapabilitiesFormatOption = VbaDevCommandGrammar.CreateStringOption(
            "--format",
            "Capabilities output format.",
            "json",
            ["json"],
            "-f");
        CapabilitiesCommand.Add(CapabilitiesFormatOption);
        CapabilitiesCommand.SetAction(parseResult =>
        {
            var capabilities = new ToolCapabilities(
                releaseVersion,
                "1.0",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["build.sourceSnapshot"] = "2.0",
                    ["test.sourceSnapshot"] = "2.0",
                    ["invocation.stdinCancellation"] = "1.0",
                    ["sourceSnapshot.activeWindowsCodePage"] = "1.0",
                    ["projectCreation.pathValidation"] = "1.0",
                    ["hostEvent.list"] = "1.0"
                },
                GetActiveWindowsCodePage(),
                getCapabilities()
                    .OrderBy(
                        registration => registration.CommandPath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        registration => registration.CommandPath,
                        registration => new CommandCapability(registration.OutputSchemaVersion),
                        StringComparer.OrdinalIgnoreCase));
            parseResult.InvocationConfiguration.Output.Write(
                JsonSerializer.Serialize(capabilities, CapabilitiesJsonOptions) +
                Environment.NewLine);
            return 0;
        });
        commandFamilyOwnership.Register(this, CapabilitiesCommand);
    }

    private sealed record ToolCapabilities(
        string ToolVersion,
        string ContractVersion,
        IReadOnlyDictionary<string, string> FeatureVersions,
        int? ActiveWindowsCodePage,
        IReadOnlyDictionary<string, CommandCapability> Commands);

    private sealed record CommandCapability(string OutputSchemaVersion);

    private static readonly JsonSerializerOptions CapabilitiesJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int? GetActiveWindowsCodePage()
        => OperatingSystem.IsWindows()
            ? checked((int)GetACP())
            : null;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
