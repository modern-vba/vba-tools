using VbaDev.App.Cli;
using Xunit;

namespace VbaDev.Tests;

public sealed class CommandErrorMessagesTests
{
    [Fact]
    public void UnexpectedFailureMentionsDotNetUnhandledExceptionCodeAndSandboxedAutomation()
    {
        var message = CommandErrorMessages.UnexpectedFailure(new InvalidOperationException("boom"));

        Assert.Contains("vba-dev failed unexpectedly: InvalidOperationException: boom", message, StringComparison.Ordinal);
        Assert.Contains("0xE0434352", message, StringComparison.Ordinal);
        Assert.Contains("generic .NET unhandled-exception code", message, StringComparison.Ordinal);
        Assert.Contains("coding agent", message, StringComparison.Ordinal);
        Assert.Contains("sandbox", message, StringComparison.OrdinalIgnoreCase);
    }
}
