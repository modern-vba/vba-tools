using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class BorrowedExcelWorkbookSessionTests
{
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
