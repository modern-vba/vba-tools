namespace VbaDevTools.App.Testing;

public static class TestOutcome
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Error = "error";

    public static string FromUnitTestSheetValue(string value)
        => value.Trim().ToUpperInvariant() switch
        {
            "OK" => Passed,
            "NG" => Failed,
            "ERR" => Error,
            _ => Error
        };
}
