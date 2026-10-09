using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComClosedSourceWorkbookAutomationTests
{
    [Fact]
    public void NormallyReturningCanceledSourceSaveRemainsUnknown()
    {
        var workbook = new CancelingWorkbook();
        var session = ExcelComWorkbookSession.Borrow(new object(), workbook, Environment.ProcessId);
        try
        {
            var tracker = new ExcelComClosedSourceWorkbookAutomation.SourceWorkbookSaveTracker();
            var buildSession = ExcelComClosedSourceWorkbookAutomation.CreateTrackedBuildSession(session, tracker);

            var error = Assert.Throws<InvalidOperationException>(buildSession.Save);

            Assert.Contains("remains unsaved", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SourceWorkbookSaveState.Unknown, tracker.State);
            Assert.Equal(1, workbook.SaveCalls);
            Assert.False(workbook.Saved);
        }
        finally
        {
            session.ReleaseBorrowed();
        }
    }

    [Fact]
    public void ClosedSourceSaveAsPathChangeRemainsUnknown()
    {
        var workbook = new SuccessfulWorkbook();
        var session = ExcelComWorkbookSession.Borrow(new object(), workbook, Environment.ProcessId);
        try
        {
            var tracker = new ExcelComClosedSourceWorkbookAutomation.SourceWorkbookSaveTracker();
            var buildSession = ExcelComClosedSourceWorkbookAutomation.CreateTrackedBuildSession(
                session, tracker, isStillSelected: () => false);

            var error = Assert.Throws<InvalidOperationException>(buildSession.Save);

            Assert.Contains("selected source workbook path", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SourceWorkbookSaveState.Unknown, tracker.State);
            Assert.True(workbook.Saved);
            Assert.Equal(1, workbook.SaveCalls);
        }
        finally
        {
            session.ReleaseBorrowed();
        }
    }

    [Fact]
    public void PathVerificationRejectsDifferentWorkbookEvenWhenItsBasenameMatches()
    {
        using var temp = TempDirectory.Create();
        var selected = Path.Combine(temp.CreateDirectory("Selected"), "Book1.xlsm");
        var other = Path.Combine(temp.CreateDirectory("Other"), "Book1.xlsm");
        File.WriteAllBytes(selected, [1]);
        File.WriteAllBytes(other, [2]);
        var workbook = new PathWorkbook { FullName = selected };

        Assert.True(SourceWorkbookPathVerification.IsSelectedWorkbookPath(workbook, selected));
        workbook.FullName = other;
        Assert.False(SourceWorkbookPathVerification.IsSelectedWorkbookPath(workbook, selected));
    }

    public sealed class CancelingWorkbook
    {
        public int SaveCalls { get; private set; }

        public bool Saved => false;

        public void Save() => SaveCalls++;
    }

    public sealed class SuccessfulWorkbook
    {
        public int SaveCalls { get; private set; }

        public bool Saved { get; private set; }

        public void Save()
        {
            SaveCalls++;
            Saved = true;
        }
    }

    public sealed class PathWorkbook
    {
        public string FullName { get; set; } = string.Empty;
    }
}
