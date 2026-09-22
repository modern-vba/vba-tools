using System.Runtime.ExceptionServices;
using Xunit;

namespace VbaDev.Tests;

internal static class ExcelIntegrationScenario
{
    public static async Task RunAsync(
        Func<Task> body,
        Action requestQuit,
        Func<Task> waitForBaseline)
    {
        var bodyFailure = await Record.ExceptionAsync(body);
        var quitFailure = Record.Exception(requestQuit);
        var baselineFailure = await Record.ExceptionAsync(waitForBaseline);
        var failures = new[] { bodyFailure, quitFailure, baselineFailure }
            .OfType<Exception>()
            .ToArray();
        if (failures.Length == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Length > 1)
        {
            throw new AggregateException(
                "The integration scenario and its Excel cleanup failed.",
                failures);
        }
    }
}
