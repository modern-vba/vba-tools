using System.CommandLine;
using VbaDev.Cli;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbaDevCommandFamilyOwnershipTests
{
    [Fact]
    public void CompletedOwnershipRejectsAnUnownedLeaf()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        command.SetAction(_ => 0);
        root.Add(command);

        var exception = Assert.Throws<InvalidOperationException>(
            () => new VbaDevCommandFamilyOwnership().Complete(root));

        Assert.Equal(
            "Completed command leaves have no family owner: probe.",
            exception.Message);
    }

    [Fact]
    public void CompletedOwnershipRejectsMoreThanOneOwnerForTheSameLeaf()
    {
        var root = new RootCommand();
        var command = new Command("probe");
        command.SetAction(_ => 0);
        root.Add(command);
        var ownership = new VbaDevCommandFamilyOwnership();
        ownership.Register(new ProbeCommandFamily(), command);
        ownership.Register(new ProbeCommandFamily(), command);

        var exception = Assert.Throws<InvalidOperationException>(
            () => ownership.Complete(root));

        Assert.Equal("Command 'probe' has more than one family owner.", exception.Message);
    }

    private sealed class ProbeCommandFamily
    {
    }
}
