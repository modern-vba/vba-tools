using VbaTools.SourceIdentities;
using Xunit;

namespace VbaTools.SourceIdentities.Tests;

public sealed class SourceIdentityTests
{
    [Theory]
    [InlineData("C:\\Sources\\Module.bas\\", "file:///C:/Sources/Module.bas")]
    [InlineData("\\\\server\\share", "file://server/share/")]
    [InlineData("\\\\server\\share\\", "file://server/share")]
    public void Native_Windows_paths_and_URI_paths_agree_on_root_and_trailing_separators(string path, string uri)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(SourceIdentity.TryFromPath(path, out var fromPath));
        Assert.True(SourceIdentity.TryFromUri(uri, out var fromUri));
        Assert.Equal(fromUri, fromPath);
        Assert.Equal(fromUri.Path, fromPath.Path);
    }

    [Fact]
    public void Invalid_UTF16_and_null_URI_inputs_return_a_default_identity()
    {
        foreach (var suffix in new[] { new string('\ud800', 1), new string('\udfff', 1) })
            Assert.False(SourceIdentity.TryFromUri("file:///C:/" + suffix, out _));
        Assert.False(SourceIdentity.TryFromUri((string?)null, out var nullString));
        Assert.False(SourceIdentity.TryFromUri((Uri?)null, out var nullUri));
        Assert.False(SourceIdentity.TryFromUri(new Uri("relative.bas", UriKind.Relative), out _));
        Assert.Equal(default, nullString);
        Assert.Equal(default, nullUri);
        Assert.Equal(string.Empty, nullUri.Path);
    }

    [Fact]
    public void A_native_path_keeps_its_own_admission_contract_and_matches_its_URI()
    {
        var path = OperatingSystem.IsWindows() ? "C:\\Sources\\sub\\..\\%41.bas" : "/sources/sub/../%41.bas";
        var uri = OperatingSystem.IsWindows() ? "file:///C:/Sources/%2541.bas" : "file:///sources/%2541.bas";
        Assert.True(SourceIdentity.TryFromPath(path, out var fromPath));
        Assert.True(SourceIdentity.TryFromUri(uri, out var fromUri));
        Assert.Equal(fromUri, fromPath);
        Assert.Equal(System.IO.Path.GetFullPath(path), fromPath.Path);
        Assert.False(SourceIdentity.TryFromPath("relative.bas", out _));
        Assert.False(SourceIdentity.TryFromPath(null, out _));
    }

    [Theory]
    [InlineData("file:///C%3A/Sources/%E6%97%A5%E6%9C%AC%F0%9F%98%80.bas?query=%GG#fragment", "C:\\Sources\\日本😀.bas")]
    [InlineData("file:///C:/Sources/%252F.bas", "C:\\Sources\\%2F.bas")]
    public void URI_path_is_strictly_decoded_once_without_consuming_its_suffix(string uri, string path)
    {
        Assert.True(SourceIdentity.TryFromUri(uri, out var identity));
        Assert.Equal(path, identity.Path);
    }

    [Theory]
    [InlineData("file:///C:/a%2Fb.bas")]
    [InlineData("file:///C:/a%5Cb.bas")]
    [InlineData("file:///C:/a%.bas")]
    [InlineData("file:///C:/a%GG.bas")]
    [InlineData("file:///C:/a%C0%AF.bas")]
    [InlineData("file:///C:/a%ED%A0%80.bas")]
    [InlineData("file:///C:/a%00.bas")]
    public void Malformed_or_structural_percent_escapes_cannot_produce_an_identity(string uri)
    {
        Assert.False(SourceIdentity.TryFromUri(uri, out var identity));
        Assert.Equal(default, identity);
    }

    [Fact]
    public void A_file_uri_identifies_a_windows_source_and_reuses_an_admitted_uri()
    {
        const string raw = "file:///C:/Sources/Module.bas";
        Assert.True(SourceIdentity.TryFromUri(raw, out var identity));
        Assert.Equal("C:\\Sources\\Module.bas", identity.Path);
        Assert.True(identity.IsWindowsPath);
        var admitted = new Uri(raw);
        Assert.True(SourceIdentity.TryFromUri(admitted, out var reused));
        Assert.Equal(identity, reused);
        Assert.Equal(raw, admitted.OriginalString);
    }
}
