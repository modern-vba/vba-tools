using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class InitialWorkbookCreatorContractTests
{
    [Fact]
    public async Task CancellationAwareCreationKeepsSynchronousImplementationsCompatible()
    {
        IInitialWorkbookCreator creator = new SynchronousInitialWorkbookCreator();

        var references = await creator.CreateInitialWorkbookAsync(
            "sample.xlsm",
            CancellationToken.None);

        Assert.Equal(
            ["Microsoft Excel 16.0 Object Library"],
            references.ReferenceNames);
    }

    [Fact]
    public async Task PreCancelledCreationDoesNotEnterASynchronousCompatibilityImplementation()
    {
        var implementation = new SynchronousInitialWorkbookCreator();
        IInitialWorkbookCreator creator = implementation;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            creator.CreateInitialWorkbookAsync("sample.xlsm", cancellation.Token));

        Assert.Equal(0, implementation.Calls);
    }

    private sealed class SynchronousInitialWorkbookCreator : IInitialWorkbookCreator
    {
        public int Calls { get; private set; }

        public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        {
            Calls++;
            return new InitialWorkbookCreationResult(
                ["Microsoft Excel 16.0 Object Library"],
                new InitialWorkbookArtifactEvidence(
                    Path.GetFullPath(workbookPath),
                    new VbaDev.Domain.FileSystemObjectIdentity(1, 2),
                    Length: 1,
                    Sha256: new string('0', 64)));
        }
    }
}
