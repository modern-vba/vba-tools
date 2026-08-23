namespace VbaDev.App.Testing;

/// <summary>
/// Carries command-line inputs for a workbook-backed test run.
/// </summary>
/// <param name="Format">The output format, such as text or ndjson.</param>
/// <param name="BuildFirst">Whether the selected document should be built before tests run.</param>
/// <param name="Selector">The optional module or procedure selector.</param>
/// <param name="ExecutionTimeout">The macro execution deadline.</param>
/// <param name="SourceSnapshotPath">The optional caller-owned complete source snapshot.</param>
public sealed record TestCommandRequest(
    string Format,
    bool BuildFirst,
    WorkbookTestSelector Selector,
    TimeSpan ExecutionTimeout,
    string? SourceSnapshotPath = null)
{
    /// <summary>
    /// Creates a request through the original test-command extension contract.
    /// </summary>
    public TestCommandRequest(
        string Format,
        bool BuildFirst,
        WorkbookTestSelector Selector)
        : this(
            Format,
            BuildFirst,
            Selector,
            TimeSpan.FromSeconds(600),
            SourceSnapshotPath: null)
    {
    }

    /// <summary>
    /// Deconstructs the original three-part request contract.
    /// </summary>
    public void Deconstruct(
        out string format,
        out bool buildFirst,
        out WorkbookTestSelector selector)
    {
        format = Format;
        buildFirst = BuildFirst;
        selector = Selector;
    }
}
