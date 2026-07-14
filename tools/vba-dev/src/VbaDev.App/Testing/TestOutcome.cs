namespace VbaDev.App.Testing;

/// <summary>
/// Defines normalized test outcome values emitted by VbaDev test output.
/// </summary>
public static class TestOutcome
{
    /// <summary>
    /// The normalized value for a passing test.
    /// </summary>
    public const string Passed = "passed";

    /// <summary>
    /// The normalized value for a failing assertion.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// The normalized value for a test execution error or unknown workbook result.
    /// </summary>
    public const string Error = "error";

    /// <summary>
    /// Converts the unit-test worksheet result code to a normalized output outcome.
    /// </summary>
    /// <param name="value">The workbook result value, such as OK, NG, or ERR.</param>
    /// <returns>The normalized test outcome.</returns>
    public static string FromUnitTestSheetValue(string value)
        => value.Trim().ToUpperInvariant() switch
        {
            "OK" => Passed,
            "NG" => Failed,
            "ERR" => Error,
            _ => Error
        };
}
