using VbaDev.App.Testing;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComWorkbookTestRunnerTests
{
    [Fact]
    public void InvokesUnitTestMainWithExactCodePageSelectors()
    {
        var boundary = new RecordingWorkbookTestBoundary();

        _ = ExcelComWorkbookTestRunner.RunTests(
            boundary,
            new WorkbookTestSelector("\u00A0", "\u00A0"));

        var invocation = Assert.Single(boundary.Invocations);
        Assert.Equal("'Book.xlsm'!UnitTestMain", invocation.EntryPoint);
        Assert.Equal(["\u00A0", "\u00A0"], invocation.Arguments);
    }

    [Fact]
    public void ReturnsAResultRowWhoseIdentityIsAnExactCodePageIdentifier()
    {
        var boundary = new RecordingWorkbookTestBoundary
        {
            LastResultRow = 2
        };
        boundary.Cells[(2, 1)] = "\u00A0";
        boundary.Cells[(2, 2)] = "\u00A0";
        boundary.Cells[(2, 3)] = "OK";

        var rows = ExcelComWorkbookTestRunner.RunTests(
            boundary,
            new WorkbookTestSelector());

        Assert.Equal(
            [new WorkbookTestResultRow("\u00A0", "\u00A0", "OK", "")],
            rows);
    }

    [Theory]
    [InlineData("CDecl", "Test_Run")]
    [InlineData("Test_Module", "Run$")]
    public void RejectsAResultRowWhoseIdentityIsNotAnExactVbaIdentifier(
        string moduleName,
        string procedureName)
    {
        var boundary = new RecordingWorkbookTestBoundary
        {
            LastResultRow = 2
        };
        boundary.Cells[(2, 1)] = moduleName;
        boundary.Cells[(2, 2)] = procedureName;
        boundary.Cells[(2, 3)] = "OK";

        var error = Assert.Throws<InvalidDataException>(
            () => ExcelComWorkbookTestRunner.RunTests(
                boundary,
                new WorkbookTestSelector()));

        Assert.Contains("VBA IDENTIFIER", error.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingWorkbookTestBoundary : IExcelComWorkbookTestBoundary
    {
        public string WorkbookName => "Book.xlsm";

        public List<(string EntryPoint, IReadOnlyList<string?> Arguments)> Invocations { get; } = [];

        public int LastResultRow { get; init; } = 1;

        public Dictionary<(int Row, int Column), string> Cells { get; } = [];

        public void RunMacro(string entryPoint, IReadOnlyList<string?> arguments)
            => Invocations.Add((entryPoint, arguments.ToArray()));

        public int GetLastResultRow() => LastResultRow;

        public string GetCellText(int row, int column)
            => Cells.GetValueOrDefault((row, column), string.Empty);

        public void Dispose()
        {
        }
    }
}
