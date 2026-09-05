using System.Text;
using Xunit;

namespace VbaTools.Integration.Tests;

public sealed class PrebuiltToolsTests
{
    [Fact]
    public async Task CancellationFrameUsesTheExactPublicStdinContractRegardlessOfHostNewline()
    {
        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n"
        };

        await PrebuiltTools.SendCancellationAsync(writer);

        Assert.Equal("cancel\n"u8.ToArray(), stream.ToArray());
    }
}
