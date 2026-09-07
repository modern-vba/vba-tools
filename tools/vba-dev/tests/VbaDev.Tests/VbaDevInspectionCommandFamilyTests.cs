using System.CommandLine;
using System.CommandLine.Parsing;
using VbaDev.App.Diagnostics;
using VbaDev.Cli;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevInspectionCommandFamilyTests
{
    [Theory]
    [InlineData("VbaDev.Cli.VbaDevHostEventCommandFamily")]
    [InlineData("VbaDev.Cli.VbaDevInspectionCommandFamily")]
    public void InspectionFamiliesAreSealedInternalModules(string typeName)
    {
        var familyType = typeof(VbaDevCommandLine).Assembly.GetType(typeName);

        Assert.NotNull(familyType);
        Assert.True(familyType.IsSealed);
        Assert.False(familyType.IsPublic);
    }

    [Fact]
    public void InspectionFamilyOwnsTheActualCheckAndDoctorGrammar()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();

        var family = VbaDevInspectionCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());
        var snapshot = rules.CreateSnapshot([root, family.CheckCommand, family.DoctorCommand]);

        Assert.Equal(["check", "doctor"], root.Subcommands.Select(command => command.Name));
        Assert.Equal([family.CheckProjectOption], family.CheckCommand.Options);
        Assert.Equal(
            [family.DoctorProjectOption, family.DoctorScopeOption, family.DoctorFormatOption],
            family.DoctorCommand.Options);
        Assert.Empty(family.CheckProjectOption.Aliases);
        Assert.Equal(["-f"], family.DoctorFormatOption.Aliases);
        Assert.Equal(
            ["project", "environment"],
            Assert.IsType<VbaDevStringOption>(family.DoctorScopeOption).AcceptedValues);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.DoctorFormatOption).AcceptedValues);
        Assert.NotNull(family.CheckCommand.Action);
        Assert.NotNull(family.DoctorCommand.Action);

        var capability = Assert.Single(capabilities);
        Assert.Same(family.DoctorCommand, capability.Command);
        Assert.Equal("doctor", capability.CommandPath);
        Assert.Equal("1.0", capability.OutputSchemaVersion);

        var conflict = Assert.Single(snapshot.Relationships);
        Assert.Equal(VbaDevGrammarRelationshipKind.Conflicts, conflict.Kind);
        Assert.Same(family.DoctorCommand, conflict.Command);
        Assert.Same(family.DoctorScopeOption, conflict.First);
        Assert.Same(family.DoctorProjectOption, conflict.Second);
        Assert.Equal("environment", conflict.RequiredFirstValue);
    }

    [Fact]
    public void InspectionFamilyBindsClosedProjectAndEnvironmentIntents()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var family = VbaDevInspectionCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            new List<VbaDevCommandCapabilityRegistration>(),
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        var check = family.CheckIntentBinding.GetRequiredIntent(
            ParseSuccessfully(root, router, ["check", "--project", "project-root"]));
        Assert.Equal("project-root", check.ProjectRoot);

        var defaultDoctor = Assert.IsType<VbaDevDoctorCommandIntent.Project>(
            family.DoctorIntentBinding.GetRequiredIntent(
                ParseSuccessfully(root, router, ["doctor"])));
        Assert.Null(defaultDoctor.ProjectRoot);
        Assert.Equal(DoctorOutputFormat.Text, defaultDoctor.Format);

        var selectedDefaultDoctor = Assert.IsType<VbaDevDoctorCommandIntent.Project>(
            family.DoctorIntentBinding.GetRequiredIntent(
                ParseSuccessfully(
                    root,
                    router,
                    ["doctor", "--project", "project-root"])));
        Assert.Equal("project-root", selectedDefaultDoctor.ProjectRoot);
        Assert.Equal(DoctorOutputFormat.Text, selectedDefaultDoctor.Format);

        var explicitProject = Assert.IsType<VbaDevDoctorCommandIntent.Project>(
            family.DoctorIntentBinding.GetRequiredIntent(
                ParseSuccessfully(
                    root,
                    router,
                    [
                        "doctor",
                        "--scope", "PROJECT",
                        "--project", "project-root",
                        "-f", "JSON"
                    ])));
        Assert.Equal("project-root", explicitProject.ProjectRoot);
        Assert.Equal(DoctorOutputFormat.Json, explicitProject.Format);

        var environment = Assert.IsType<VbaDevDoctorCommandIntent.Environment>(
            family.DoctorIntentBinding.GetRequiredIntent(
                ParseSuccessfully(
                    root,
                    router,
                    ["doctor", "--scope", "ENVIRONMENT", "--format", "JSON"])));
        Assert.Equal(DoctorOutputFormat.Json, environment.Format);
    }

    [Fact]
    public void HostEventFamilyOwnsTheActualListGrammarAndClosedFormatIntent()
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var family = VbaDevHostEventCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            new VbaDevCommandFamilyOwnership());
        var router = new VbaDevGrammarFailureRouter(root, rules);

        Assert.Same(
            family.HostEventCommand,
            Assert.Single(root.Subcommands, command => command.Name == "host-event"));
        Assert.Same(
            family.ListCommand,
            Assert.Single(family.HostEventCommand.Subcommands));
        Assert.Equal("list", family.ListCommand.Name);
        Assert.Equal([family.ListFormatOption], family.ListCommand.Options);
        Assert.Equal(["-f"], family.ListFormatOption.Aliases);
        Assert.Equal(
            ["text", "json"],
            Assert.IsType<VbaDevStringOption>(family.ListFormatOption).AcceptedValues);
        Assert.NotNull(family.ListCommand.Action);
        var capability = Assert.Single(capabilities);
        Assert.Same(family.ListCommand, capability.Command);
        Assert.Equal("host-event list", capability.CommandPath);
        Assert.Equal("1.0", capability.OutputSchemaVersion);

        Assert.IsType<VbaDevHostEventListCommandIntent.Text>(
            family.ListIntentBinding.GetRequiredIntent(
                ParseSuccessfully(root, router, ["host-event", "list"])));
        Assert.IsType<VbaDevHostEventListCommandIntent.Json>(
            family.ListIntentBinding.GetRequiredIntent(
                ParseSuccessfully(
                    root,
                    router,
                    ["host-event", "list", "-f", "JSON"])));
    }

    [Theory]
    [InlineData(
        new[] { "doctor", "--scope", "environment", "--project", "project-root" },
        "Options '--scope' and '--project' cannot be used together.",
        "doctor")]
    [InlineData(
        new[] { "doctor", "--scope", "machine" },
        "Option '--scope' does not accept value 'machine'. Accepted values: project, environment.",
        "doctor")]
    [InlineData(
        new[] { "doctor", "-f", "yaml" },
        "Option '--format' does not accept value 'yaml'. Accepted values: text, json.",
        "doctor")]
    [InlineData(
        new[] { "host-event", "list", "-f", "yaml" },
        "Option '--format' does not accept value 'yaml'. Accepted values: text, json.",
        "host-event list")]
    [InlineData(
        new[] { "check", "--format", "json" },
        "Unrecognized token '--format' for command 'vba-dev check'.",
        "check")]
    [InlineData(
        new[] { "check", "-f", "json" },
        "Unrecognized token '-f' for command 'vba-dev check'.",
        "check")]
    public async Task InvalidInspectionGrammarStopsBeforeAnyCommandAction(
        string[] arguments,
        string expectedDiagnostic,
        string expectedCommandPath)
    {
        using var temp = TempDirectory.Create();
        var root = new RootCommand();
        var cancellationTransportOption = new Option<string>("--cancellation-transport")
        {
            Hidden = true,
            Recursive = true
        };
        root.Add(cancellationTransportOption);
        var rules = new VbaDevGrammarFailureRules();
        var capabilities = new List<VbaDevCommandCapabilityRegistration>();
        var commandFamilyOwnership = new VbaDevCommandFamilyOwnership();
        var inspection = VbaDevInspectionCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            commandFamilyOwnership);
        var hostEvent = VbaDevHostEventCommandFamily.Register(
            root,
            ToolingCompositionRoot.CreateApplicationComposition(temp.Path),
            rules,
            capabilities,
            commandFamilyOwnership);
        var actionCount = 0;
        foreach (var command in new[]
                 {
                     inspection.CheckCommand,
                     inspection.DoctorCommand,
                     hostEvent.ListCommand
                 })
        {
            command.SetAction(_ =>
            {
                actionCount++;
                return 0;
            });
        }

        var commandLine = new VbaDevCommandLine(new VbaDevCommandGraph(
            root,
            cancellationTransportOption,
            capabilities,
            new VbaDevGrammarFailureRouter(root, rules)));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await commandLine.InvokeAsync(
            arguments,
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(standardOutput.ToString());
        Assert.Equal(
            $"Error: {expectedDiagnostic}{Environment.NewLine}" +
            $"Hint: Run 'vba-dev {expectedCommandPath} --help' for usage.{Environment.NewLine}",
            standardError.ToString());
        Assert.Equal(0, actionCount);
    }

    private static ParseResult ParseSuccessfully(
        RootCommand root,
        VbaDevGrammarFailureRouter router,
        IReadOnlyList<string> arguments)
    {
        var result = root.Parse(arguments);
        using var standardError = new StringWriter();

        Assert.False(router.TryWriteFailure(result, standardError));
        Assert.Empty(standardError.ToString());
        return result;
    }
}
