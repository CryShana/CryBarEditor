using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryBar.Updates;
using Xunit;

namespace CryBar.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.4.0", 1, 3, 2, true)]    // newer minor
    [InlineData("v1.4.0", 1, 3, 2, true)]   // 'v' prefix tolerated by unanchored regex
    [InlineData("1.3.3", 1, 3, 2, true)]    // newer build
    [InlineData("2.0.0", 1, 3, 2, true)]    // newer major
    [InlineData("1.3.2", 1, 3, 2, false)]   // equal
    [InlineData("1.3.1", 1, 3, 2, false)]   // older
    [InlineData("0.9.9", 1, 3, 2, false)]   // older major
    [InlineData("1.2.99", 1, 3, 2, false)]  // older minor
    public void IsVersionNewer_ComparesCorrectly(string remote, int curMajor, int curMinor, int curBuild, bool expected)
    {
        var current = new System.Version(curMajor, curMinor, curBuild);
        Assert.Equal(expected, UpdateService.IsVersionNewer(remote, current));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    [InlineData("")]
    public void IsVersionNewer_ReturnsFalseOnUnparseableInput(string remote)
    {
        var current = new System.Version(1, 0, 0);
        Assert.False(UpdateService.IsVersionNewer(remote, current));
    }

    [Fact]
    public void BuildAssetUrl_FormatsCorrectly()
    {
        var url = UpdateService.BuildAssetUrl("1.4.0");
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/download/1.4.0/CryBarEditor-1.4.0.zip", url);
    }

    [Fact]
    public void BuildReleasePageUrl_FormatsCorrectly()
    {
        var url = UpdateService.BuildReleasePageUrl("1.4.0");
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/tag/1.4.0", url);
    }

    [Fact]
    public async Task TryGetLatestVersionAsync_ReturnsNullOnEmptyContent()
    {
        var handler = new StubHandler("");
        using var http = new HttpClient(handler);
        var info = await UpdateService.TryGetLatestVersionAsync(http);
        Assert.Null(info);
    }

    [Fact]
    public async Task TryGetLatestVersionAsync_ParsesLatestReleaseJson()
    {
        var json = """
            {
                "tag_name": "1.5.0",
                "html_url": "https://github.com/CryShana/CryBarEditor/releases/tag/1.5.0",
                "assets": [
                    { "name": "CryBar.Cli-1.5.0.zip", "browser_download_url": "https://github.com/CryShana/CryBarEditor/releases/download/1.5.0/CryBar.Cli-1.5.0.zip" },
                    { "name": "CryBarEditor-1.5.0.zip", "browser_download_url": "https://github.com/CryShana/CryBarEditor/releases/download/1.5.0/CryBarEditor-1.5.0.zip" }
                ]
            }
            """;
        var handler = new StubHandler(json);
        using var http = new HttpClient(handler);
        var info = await UpdateService.TryGetLatestVersionAsync(http);
        Assert.NotNull(info);
        Assert.Equal("1.5.0", info!.LatestVersion);
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/tag/1.5.0", info.ReleasePageUrl);
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/download/1.5.0/CryBarEditor-1.5.0.zip", info.AssetUrl);
    }

    [Fact]
    public async Task TryGetLatestVersionAsync_FallsBackToBuiltUrlsWhenAssetsMissing()
    {
        var json = """{ "tag_name": "2.0.0" }""";
        var handler = new StubHandler(json);
        using var http = new HttpClient(handler);
        var info = await UpdateService.TryGetLatestVersionAsync(http);
        Assert.NotNull(info);
        Assert.Equal("2.0.0", info!.LatestVersion);
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/tag/2.0.0", info.ReleasePageUrl);
        Assert.Equal("https://github.com/CryShana/CryBarEditor/releases/download/2.0.0/CryBarEditor-2.0.0.zip", info.AssetUrl);
    }

    [Fact]
    public async Task TryGetLatestVersionAsync_ReturnsNullOnUnparseableTag()
    {
        var json = """{ "tag_name": "nightly" }""";
        var handler = new StubHandler(json);
        using var http = new HttpClient(handler);
        var info = await UpdateService.TryGetLatestVersionAsync(http);
        Assert.Null(info);
    }

    [Fact]
    public async Task TryGetLatestVersionAsync_CallsApiEndpointWithUserAgent()
    {
        var handler = new StubHandler("""{ "tag_name": "1.5.0" }""");
        using var http = new HttpClient(handler);
        _ = await UpdateService.TryGetLatestVersionAsync(http);
        Assert.Equal("https://api.github.com/repos/CryShana/CryBarEditor/releases/latest", handler.LastRequest?.RequestUri?.ToString());
        Assert.NotEmpty(handler.LastRequest!.Headers.UserAgent);
    }

    [Fact]
    public void CleanupStaleUpdate_RemovesUpdateFolderAndBatch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var updateDir = Path.Combine(dir, ".update");
            var nestedDir = Path.Combine(updateDir, "extracted", "sub");
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(nestedDir, "a.bin"), "x");
            File.WriteAllText(Path.Combine(updateDir, "source.zip"), "y");
            var batPath = Path.Combine(dir, "update.bat");
            File.WriteAllText(batPath, "echo");

            UpdateService.CleanupStaleUpdate(dir);

            Assert.False(Directory.Exists(updateDir));
            Assert.False(File.Exists(batPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CleanupStaleUpdate_NoOpWhenNothingPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crybar-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Should not throw
            UpdateService.CleanupStaleUpdate(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateScriptContent_MatchesExpectedSnapshot()
    {
        const string expected =
            "@echo off\r\n" +
            ":wait\r\n" +
            "tasklist /FI \"PID eq %~1\" 2>nul | find \"%~1\" >nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "    timeout /t 1 /nobreak >nul\r\n" +
            "    goto wait\r\n" +
            ")\r\n" +
            "robocopy \".update\\extracted\" \".\" /E /MOVE /R:5 /W:1 >nul\r\n" +
            "start \"\" \"%~2\"\r\n";

        Assert.Equal(expected, UpdateService.UpdateScriptContent);
    }

    sealed class StubHandler : HttpMessageHandler
    {
        readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }
        public StubHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
