namespace VbaDev.App.Workbooks;

internal sealed class WorkbookVerificationReportMissingException()
    : InvalidOperationException("Workbook generation verification returned no verification report.");
