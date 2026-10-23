using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugCompilationHostClassifierTests
{
    [Theory]
    [InlineData(DebugExcelProcessArchitecture.X86, "64", "16.0", "7.01", DebugCompilationHostFactsStatus.Verified, false, true)]
    [InlineData(DebugExcelProcessArchitecture.X86, "32", "12.0", "6.05", DebugCompilationHostFactsStatus.Verified, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X86, "64", "9.0", "6.0", DebugCompilationHostFactsStatus.Verified, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X64, "64", "14.0", "7.0", DebugCompilationHostFactsStatus.Verified, true, true)]
    [InlineData(DebugExcelProcessArchitecture.Arm64, "64", "16.0", "7.01", DebugCompilationHostFactsStatus.Verified, true, true)]
    [InlineData(DebugExcelProcessArchitecture.X64, "32", "16.0", "7.01", DebugCompilationHostFactsStatus.Mismatch, false, false)]
    [InlineData(DebugExcelProcessArchitecture.Arm64, "32", "unknown", "unknown", DebugCompilationHostFactsStatus.Mismatch, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X64, "32", "unknown", "7.01", DebugCompilationHostFactsStatus.Mismatch, false, false)]
    [InlineData(DebugExcelProcessArchitecture.Unknown, "unknown", "16.0", "6.05", DebugCompilationHostFactsStatus.Mismatch, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X86, "unknown", "12.0", "7.0", DebugCompilationHostFactsStatus.Mismatch, false, false)]
    [InlineData(DebugExcelProcessArchitecture.Unknown, "64", "16.0", "7.01", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X64, "unknown", "16.0", "7.01", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X64, "64", "13.0", "7.01", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X64, "64", "16.0", "8.0", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X86, "32", "8.0", "6.0", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.Unknown, "unknown", "unknown", "unknown", DebugCompilationHostFactsStatus.Unknown, false, false)]
    [InlineData(DebugExcelProcessArchitecture.X86, "64", "bad", "7.01", DebugCompilationHostFactsStatus.Unknown, false, false)]
    public async Task BothProductionCallersUseTheEvidenceJudgmentTable(
        DebugExcelProcessArchitecture architecture, string osBits, string excelVersion,
        string vbeVersion, DebugCompilationHostFactsStatus expectedStatus, bool win64, bool vba7)
    {
        var os = osBits == "unknown" ? "unsupported host" : $"Windows ({osBits}-bit) NT 10.00";
        var expected = DebugCompilationHostClassifier.Classify(excelVersion, vbeVersion, os, architecture);
        Assert.Equal(expectedStatus, expected.Status);
        if (expectedStatus == DebugCompilationHostFactsStatus.Verified)
        {
            Assert.Equal(new DebugCompilerBuiltInConstants(true, vba7, false, true, win64, false),
                expected.BuiltInConstants);
            Assert.Null(expected.UnavailableReason);
        }
        else
        {
            Assert.Null(expected.BuiltInConstants);
            Assert.NotEmpty(expected.UnavailableReason!);
        }

        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "not a saved VBA package");
        var sourceModel = FakeVbeModel.Create(path, [], excelVersion: excelVersion,
            vbeVersion: vbeVersion, operatingSystem: os);
        sourceModel.Workbook.Saved = false;
        var binding = new SourceVbeDesktopBinding(sourceModel.Excel, sourceModel.Workbook, path,
            12345, 638000000000000000, true, () => true, () => { }, architecture);
        var source = new SourceVbeDebugAutomation(new Connector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        await using (var session = await source.AttachOrOpenAsync(path, CancellationToken.None))
        {
            Assert.Equal(expected, await session.GetCompilationHostFactsAsync(CancellationToken.None));
            Assert.False(sourceModel.Workbook.Saved);
        }

        var process = new FakeDebugOwnedProcess(31414, DateTime.Now, architecture);
        var model = FakeVbeModel.Create("GeneratedBook.xlsm", [], excelVersion: excelVersion,
            vbeVersion: vbeVersion, operatingSystem: os);
        var legacy = new VbeDebugAutomation(new FakeExcelDebugApplicationFactory(model.Excel),
            new FakeDebugExcelProcessApi(31414, process), new FakeDebugWindowActivator(),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()));
        await using var legacySession = await legacy.StartVisibleAsync(CancellationToken.None);
        Assert.Equal(expected, await legacySession.GetCompilationHostFactsAsync(CancellationToken.None));
        Assert.Equal(0, process.KillCalls);
        process.Exit(0);
        await legacySession.Completion;
    }
    private sealed class Connector(SourceVbeDesktopBinding binding) : ISourceVbeDesktopConnector
    {
        public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath) => binding;
    }
}
