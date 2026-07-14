namespace VbaDev.App.Testing;

/// <summary>
/// Represents one workbook-backed test run and its event stream.
/// </summary>
/// <param name="Project">The project that owns the test run.</param>
/// <param name="Document">The document whose workbook produced the test run.</param>
/// <param name="Results">The normalized completed test procedure results.</param>
public sealed record TestRun(
    string Project,
    string Document,
    IReadOnlyList<TestResultRecord> Results)
{
    /// <summary>
    /// Creates a test run from raw workbook test result rows.
    /// </summary>
    /// <param name="project">The project that owns the test run.</param>
    /// <param name="document">The document whose workbook produced the rows.</param>
    /// <param name="rows">The workbook result rows.</param>
    /// <returns>The normalized test run.</returns>
    public static TestRun FromWorkbookRows(
        string project,
        string document,
        IEnumerable<WorkbookTestResultRow> rows)
        => new(
            project,
            document,
            rows.Select(row => TestResultRecord.FromWorkbookRow(document, row)).ToArray());

    /// <summary>
    /// Creates a test run from normalized test result records.
    /// </summary>
    /// <param name="project">The project that owns the test run.</param>
    /// <param name="document">The document whose workbook produced the run.</param>
    /// <param name="results">The completed test procedure results.</param>
    /// <returns>The normalized test run.</returns>
    public static TestRun FromResults(
        string project,
        string document,
        IReadOnlyList<TestResultRecord> results)
        => new(project, document, results);

    /// <summary>
    /// Gets whether the test run contains any failed or errored test procedure.
    /// </summary>
    public bool HasFailures
        => Results.Any(result => !result.Outcome.Equals(TestOutcome.Passed, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Creates the stable event sequence for this test run.
    /// </summary>
    /// <returns>The test run events in emission order.</returns>
    public IReadOnlyList<TestRunEvent> CreateEvents()
    {
        var events = new List<TestRunEvent>
        {
            new RunStartedTestRunEvent(Project, Document)
        };
        foreach (var result in Results)
        {
            events.Add(TestStartedTestRunEvent.FromResult(Project, result));
            events.Add(TestFinishedTestRunEvent.FromResult(Project, result));
        }

        events.Add(RunFinishedTestRunEvent.FromResults(Project, Document, Results));
        return events;
    }
}

/// <summary>
/// Base record for test run events.
/// </summary>
/// <param name="Type">The machine-readable event type.</param>
/// <param name="Project">The project that owns the event.</param>
/// <param name="Document">The document that owns the event.</param>
public abstract record TestRunEvent(string Type, string Project, string Document);

/// <summary>
/// Event emitted at the start of a workbook test run.
/// </summary>
/// <param name="Project">The project that owns the event.</param>
/// <param name="Document">The document that owns the event.</param>
public sealed record RunStartedTestRunEvent(string Project, string Document)
    : TestRunEvent("runStarted", Project, Document);

/// <summary>
/// Event emitted before reporting one test procedure result.
/// </summary>
/// <param name="Project">The project that owns the event.</param>
/// <param name="Document">The document that owns the event.</param>
/// <param name="Module">The test module name.</param>
/// <param name="Procedure">The test procedure name.</param>
public sealed record TestStartedTestRunEvent(
    string Project,
    string Document,
    string Module,
    string Procedure)
    : TestRunEvent("testStarted", Project, Document)
{
    /// <summary>
    /// Creates a started event from one normalized test result.
    /// </summary>
    /// <param name="project">The project that owns the event.</param>
    /// <param name="result">The normalized test result.</param>
    /// <returns>The started event.</returns>
    public static TestStartedTestRunEvent FromResult(string project, TestResultRecord result)
        => new(project, result.Document, result.Category, result.TestName);
}

/// <summary>
/// Event emitted after one test procedure completes.
/// </summary>
/// <param name="Project">The project that owns the event.</param>
/// <param name="Document">The document that owns the event.</param>
/// <param name="Module">The test module name.</param>
/// <param name="Procedure">The test procedure name.</param>
/// <param name="Outcome">The normalized test outcome.</param>
/// <param name="Message">The test result message.</param>
/// <param name="DurationMilliseconds">The optional test duration in milliseconds.</param>
public sealed record TestFinishedTestRunEvent(
    string Project,
    string Document,
    string Module,
    string Procedure,
    string Outcome,
    string Message,
    double? DurationMilliseconds)
    : TestRunEvent("testFinished", Project, Document)
{
    /// <summary>
    /// Creates a finished event from one normalized test result.
    /// </summary>
    /// <param name="project">The project that owns the event.</param>
    /// <param name="result">The normalized test result.</param>
    /// <returns>The finished event.</returns>
    public static TestFinishedTestRunEvent FromResult(string project, TestResultRecord result)
        => new(
            project,
            result.Document,
            result.Category,
            result.TestName,
            result.Outcome,
            result.Message,
            result.Duration?.TotalMilliseconds);
}

/// <summary>
/// Event emitted at the end of a workbook test run.
/// </summary>
/// <param name="Project">The project that owns the event.</param>
/// <param name="Document">The document that owns the event.</param>
/// <param name="Outcome">The normalized run outcome.</param>
/// <param name="Total">The total number of completed test procedures.</param>
/// <param name="Passed">The number of passed test procedures.</param>
/// <param name="Failed">The number of failed test procedures.</param>
/// <param name="Errors">The number of errored test procedures.</param>
public sealed record RunFinishedTestRunEvent(
    string Project,
    string Document,
    string Outcome,
    int Total,
    int Passed,
    int Failed,
    int Errors)
    : TestRunEvent("runFinished", Project, Document)
{
    /// <summary>
    /// Creates a run-finished event from normalized test results.
    /// </summary>
    /// <param name="project">The project that owns the event.</param>
    /// <param name="document">The document that owns the event.</param>
    /// <param name="results">The normalized test results.</param>
    /// <returns>The run-finished event.</returns>
    public static RunFinishedTestRunEvent FromResults(
        string project,
        string document,
        IReadOnlyList<TestResultRecord> results)
    {
        var summary = TestResultSummary.FromResults(results);
        var outcome = summary.Failed == 0 && summary.Errors == 0
            ? TestOutcome.Passed
            : TestOutcome.Failed;
        return new(project, document, outcome, summary.Total, summary.Passed, summary.Failed, summary.Errors);
    }
}
