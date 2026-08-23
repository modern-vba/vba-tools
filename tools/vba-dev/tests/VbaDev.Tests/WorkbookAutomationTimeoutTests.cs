using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookAutomationTimeoutTests
{
    [Fact]
    public void DefaultsMatchTheBoundedAutomationContract()
    {
        var timeouts = WorkbookAutomationTimeouts.Default;

        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.ExcelStartup);
        Assert.Equal(TimeSpan.FromSeconds(300), timeouts.WorkbookOpen);
        Assert.Equal(TimeSpan.FromSeconds(60), timeouts.ReferenceAttempt);
        Assert.Equal(TimeSpan.FromSeconds(30), timeouts.ModuleImport);
        Assert.Equal(TimeSpan.FromSeconds(300), timeouts.WorkbookSave);
        Assert.Equal(TimeSpan.FromSeconds(5), timeouts.ProcessCleanup);
    }

    [Fact]
    public void ExistingPublicStageValuesRemainStableWhenTestExecutionIsAdded()
    {
        Assert.Equal(8, (int)WorkbookAutomationStageKind.ProcessCleanup);
        Assert.Equal(9, (int)WorkbookAutomationStageKind.OutputCommit);
        Assert.Equal(10, (int)WorkbookAutomationStageKind.TestExecution);
    }

    [Theory]
    [InlineData(WorkbookAutomationStageKind.ExcelStartup, null, "Excel startup")]
    [InlineData(WorkbookAutomationStageKind.WorkbookOpen, "Book1.xlsm", "workbook open 'Book1.xlsm'")]
    [InlineData(WorkbookAutomationStageKind.ReferenceAttempt, "Scripting", "reference attempt 'Scripting'")]
    [InlineData(WorkbookAutomationStageKind.ModuleImport, "Feature.bas", "module import 'Feature.bas'")]
    [InlineData(WorkbookAutomationStageKind.WorkbookSave, "Book1.xlsm", "workbook save 'Book1.xlsm'")]
    [InlineData(WorkbookAutomationStageKind.ProcessCleanup, null, "process cleanup")]
    public void StageDescriptionsIdentifyTheActiveOperation(
        WorkbookAutomationStageKind kind,
        string? item,
        string expected)
    {
        Assert.Equal(expected, new WorkbookAutomationStage(kind, item).Description);
    }
}
