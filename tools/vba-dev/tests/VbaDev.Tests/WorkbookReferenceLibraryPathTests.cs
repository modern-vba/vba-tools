using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookReferenceLibraryPathTests
{
    [Theory]
    [InlineData(@"\Device\HarddiskVolume1\Office\VBE7.DLL", @"C:\Office\VBE7.DLL")]
    [InlineData(@"\Device\HarddiskVolume11\Office\VBE7.DLL", null)]
    [InlineData(@"\Device\Mup\server\share\Library.dll", @"\\server\share\Library.dll")]
    public void MappedFileTranslationRequiresAnExactVolumeBoundary(string nativePath, string? expected)
        => Assert.Equal(expected, WindowsLoadedLibraryPaths.ToDosPath(nativePath,
            [new("C:", @"\Device\HarddiskVolume1")]));

    [Fact]
    public void ExistingObservedFileRemainsAuthoritative()
    {
        using var temp = TempDirectory.Create();
        var observed = Path.Combine(temp.Path, "VBE7.DLL");
        File.WriteAllBytes(observed, [1]);
        Assert.Equal(observed, WorkbookReferenceLibraryPath.Resolve(observed, ["C:/other/VBE7.DLL"]));
    }

    [Fact]
    public void MissingVirtualPathUsesTheUniqueLoadedPhysicalLibrary()
    {
        using var temp = TempDirectory.Create();
        var physical = Path.Combine(temp.Path, "VBE7.DLL");
        File.WriteAllBytes(physical, [1]);
        var observed = Path.Combine(temp.Path, "virtual", "VBE7.DLL");

        Assert.Equal(physical, WorkbookReferenceLibraryPath.Resolve(observed, [physical, "C:/other/UNRELATED.DLL"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousLoadedLibraryCannotSupplyAReplacement(bool ambiguous)
    {
        using var temp = TempDirectory.Create();
        var observed = Path.Combine(temp.Path, "virtual", "VBE7.DLL");
        var first = Path.Combine(temp.CreateDirectory("first"), "VBE7.DLL");
        var second = Path.Combine(temp.CreateDirectory("second"), "VBE7.DLL");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);

        var error = Assert.Throws<InvalidOperationException>(() => WorkbookReferenceLibraryPath.Resolve(observed,
            ambiguous ? [first, second] : []));

        Assert.Contains(observed, error.Message, StringComparison.Ordinal);
    }
}
