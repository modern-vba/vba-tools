using VbaDebugAdapter.Debugging;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugCompilationSettingsTests
{
    [Fact]
    public void ConstructorPreservesACodePageConstantNameThatDotNetTreatsAsWhitespace()
    {
        const string constantName = "\u00A0";

        var settings = new DebugCompilationSettings(
            VbaProjectSystemKind.Win64,
            1252,
            [new KeyValuePair<string, short>(constantName, 1)],
            new string('A', 64));

        Assert.Equal(1, settings.ProjectConstants[constantName]);
    }

    [Fact]
    public void ConstructorRejectsConstantNamesOutsideTheSharedIdentifierAuthority()
    {
        var invalidNames = new[]
        {
            "Bad Name",
            "CDecl",
            "Name$",
            "亜ㄱ",
            new string('A', 256)
        };

        foreach (var invalidName in invalidNames)
        {
            Assert.Throws<ArgumentException>(() => new DebugCompilationSettings(
                VbaProjectSystemKind.Win64,
                1252,
                [new KeyValuePair<string, short>(invalidName, 1)],
                new string('A', 64)));
        }
    }
}
