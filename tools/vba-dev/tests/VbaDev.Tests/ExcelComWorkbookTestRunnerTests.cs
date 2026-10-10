using System.Dynamic;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComWorkbookTestRunnerTests
{
    [Fact]
    public void ApostropheWorkbookNameUsesOneExactEscapedQualifiedMacroCall()
    {
        var boundary = new RecordingWorkbookTestBoundary { WorkbookName = "O'Brien.xlsm" };

        _ = ExcelComWorkbookTestRunner.RunTests(boundary,
            new WorkbookTestSelector("Test_Source", "Test_BoundWorkbook"));

        var invocation = Assert.Single(boundary.Invocations);
        Assert.Equal("'O''Brien.xlsm'!UnitTestMain", invocation.EntryPoint);
        Assert.Equal(["Test_Source", "Test_BoundWorkbook"], invocation.Arguments);
    }

    [Fact]
    public void BorrowedReaderPreservesOriginalReadFailureAndAttemptsEveryAcquiredReferenceRelease()
    {
        var readError = new InvalidDataException("Original result-cell read failure");
        var releaseError = new InvalidOperationException("Acquired result-cell release failed");
        var cell = new ReaderCell(readError);
        var cells = new InvokableReaderReference("cells", arguments =>
            Convert.ToInt32(arguments[0]) == 10 ? new ReaderLastCell() : cell);
        var sheet = new ReaderSheet(cells);
        var worksheets = new InvokableReaderReference("worksheets", _ => sheet);
        var excel = new ReaderExcel();
        var native = ExcelComWorkbookSession.Borrow(
            excel, new ReaderWorkbook(worksheets), Environment.ProcessId);
        var released = new List<string>();
        try
        {
            var error = Assert.Throws<WorkbookAutomationComReferenceReleaseException>(() =>
                ExcelComWorkbookTestRunner.RunTests(native, new WorkbookTestSelector(), reference =>
                {
                    if (reference is not ReaderReference acquired) return;
                    released.Add(acquired.ReferenceName);
                    if (ReferenceEquals(reference, cell)) throw releaseError;
                }));

            Assert.Same(readError, error.OperationError);
            Assert.Equal(["end cell", "last cell", "cells", "rows", "result cell", "cells", "sheet", "worksheets"],
                released);
            var unproved = Assert.Single(error.ReleaseFailures);
            Assert.Equal("result cell", unproved.ReferenceName);
            Assert.Same(releaseError, unproved.Error);
            Assert.Same(readError, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions[0]);
            Assert.Equal(1, excel.RunCalls);
            var facts = WorkbookAutomationTerminalFacts.Analyze(error);
            Assert.False(facts.ComReferenceReleaseProven);
            Assert.True(facts.ProcessReleaseProven);
            Assert.True(facts.DispatcherRetired);
        }
        finally
        {
            native.ReleaseBorrowed();
        }
    }

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
        public string WorkbookName { get; init; } = "Book.xlsm";

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

    public class ReaderReference(string referenceName) : DynamicObject
    {
        public string ReferenceName { get; } = referenceName;
    }

    public sealed class InvokableReaderReference(
        string referenceName,
        Func<object?[], object> invoke) : ReaderReference(referenceName)
    {
        public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
        {
            result = invoke(args ?? []);
            return true;
        }
    }

    public sealed class ReaderWorkbook(InvokableReaderReference worksheets)
    {
        public string Name => "Book.xlsm";
        public object Worksheets => worksheets;
    }

    public sealed class ReaderSheet(InvokableReaderReference cells) : ReaderReference("sheet")
    {
        public object Rows => new ReaderRows();
        public object Cells => cells;
    }

    public sealed class ReaderRows() : ReaderReference("rows")
    {
        public int Count => 10;
    }

    public sealed class ReaderLastCell() : ReaderReference("last cell")
    {
        public object End(int direction) => new ReaderEndCell();
    }

    public sealed class ReaderEndCell() : ReaderReference("end cell")
    {
        public int Row => 2;
    }

    public sealed class ReaderCell(Exception readError) : ReaderReference("result cell")
    {
        public object Value2 => throw readError;
    }

    public sealed class ReaderExcel
    {
        public int RunCalls { get; private set; }
        public void Run(string entryPoint) => RunCalls++;
    }
}
