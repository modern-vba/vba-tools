using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class BorrowedExcelWorkbookSessionTests
{
    [Fact]
    public void BorrowedOuterAcquisitionsReleaseOnceAndRetainEveryNamedReleaseFailure()
    {
        var excel = new object();
        var workbook = new object();
        var workbookFailure = new InvalidOperationException("Workbook acquisition release failed");
        var excelFailure = new InvalidOperationException("Excel acquisition release failed");
        var attempts = new List<object>();
        var session = ExcelComWorkbookSession.Borrow(excel, workbook, Environment.ProcessId, reference =>
        {
            attempts.Add(reference!);
            throw ReferenceEquals(reference, workbook) ? workbookFailure : excelFailure;
        });

        var error = Assert.Throws<WorkbookAutomationComReferenceReleaseException>(session.ReleaseBorrowed);
        session.ReleaseBorrowed();

        Assert.Null(error.OperationError);
        Assert.Equal([workbook, excel], attempts);
        Assert.Equal(["borrowed workbook", "borrowed Excel application"],
            error.ReleaseFailures.Select(failure => failure.ReferenceName));
        Assert.Same(workbookFailure, error.ReleaseFailures[0].Error);
        Assert.Same(excelFailure, error.ReleaseFailures[1].Error);
        var facts = WorkbookAutomationTerminalFacts.Analyze(error);
        Assert.False(facts.ComReferenceReleaseProven);
        Assert.True(facts.ProcessReleaseProven);
        Assert.True(facts.DispatcherRetired);
    }

    [Fact]
    public void BorrowedSessionReadsExactProcessLibrariesWithoutOwningThatProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var session = ExcelComWorkbookSession.Borrow(
            new object(), new object(), Environment.ProcessId);
        try
        {
            Assert.NotEmpty(session.CaptureLoadedModulePaths());
        }
        finally
        {
            session.ReleaseBorrowed();
        }
    }
}
