using System.Net;
using GTAModManager.Core.Base;

namespace GTAModManager.Core.Tests;

public sealed class BaseDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gmm-basedl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private const string Page = """
        <a href="/files/ScriptHookV_SDK_1.0.617.1a.zip">SDK</a>
        <a href="/files/ScriptHookV_3889.0_1158.13.zip">Download</a>
        """;
    private static readonly byte[] Zip = [(byte)'P', (byte)'K', 3, 4, 0, 0, 0, 0];

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r);
            var resp = respond(r);
            resp.RequestMessage = r;
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public void Link_is_taken_from_page_and_sdk_is_ignored()
    {
        var link = BaseDownloads.FindScriptHookLink(Page, BaseDownloads.ScriptHookPage)!;
        Assert.Equal("https://www.dev-c.com/files/ScriptHookV_3889.0_1158.13.zip", link.AbsoluteUri);
    }

    [Fact]
    public async Task Script_hook_is_requested_from_its_page_with_referer()
    {
        var fake = new Fake(r =>
            r.RequestUri!.AbsolutePath.EndsWith(".zip")
                ? (
                    r.Headers.Referrer == BaseDownloads.ScriptHookPage
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) }
                        : new HttpResponseMessage(HttpStatusCode.Found)
                        {
                            Headers = { Location = new Uri("https://dev-c.com") },
                        }
                )
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page) }
        );
        using var http = BaseDownloads.CreateClient(fake);
        var path = await BaseDownloads.DownloadScriptHookAsync(http, _dir, CancellationToken.None);
        Assert.Equal("ScriptHookV_3889.0_1158.13.zip", Path.GetFileName(path));
        Assert.Equal(2, fake.Requests.Count); // the page, then the archive
    }

    [Fact]
    public async Task Redirect_to_home_page_means_file_was_not_given()
    {
        var fake = new Fake(r =>
            r.RequestUri!.AbsolutePath.EndsWith(".zip")
                ? new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://dev-c.com") },
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page) }
        );
        using var http = BaseDownloads.CreateClient(fake);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BaseDownloads.DownloadScriptHookAsync(http, _dir, CancellationToken.None)
        );
    }

    [Fact]
    public async Task Html_instead_of_archive_is_rejected_and_not_left_on_disk()
    {
        var fake = new Fake(r =>
            r.RequestUri!.AbsolutePath.EndsWith(".zip")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page) }
        );
        using var http = BaseDownloads.CreateClient(fake);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BaseDownloads.DownloadScriptHookAsync(http, _dir, CancellationToken.None)
        );
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
    }

    [Fact]
    public async Task Shvdn_downloads_single_release_zip_following_cdn_redirect()
    {
        const string release =
            """{"assets":[{"name":"ScriptHookVDotNet-v3.7.0-nightly.204.zip","browser_download_url":"https://github.com/o/r/releases/download/v1/ScriptHookVDotNet-v3.7.0-nightly.204.zip"}]}""";
        var fake = new Fake(r =>
            r.RequestUri!.Host switch
            {
                "api.github.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(release) },
                "github.com" => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://objects.githubusercontent.com/x/asset.zip") },
                },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) },
            }
        );
        using var http = BaseDownloads.CreateClient(fake);
        var path = await BaseDownloads.DownloadShvdnAsync(http, ShvdnVariant.Nightly, _dir, CancellationToken.None);
        Assert.Equal("ScriptHookVDotNet-v3.7.0-nightly.204.zip", Path.GetFileName(path));
        Assert.Contains(fake.Requests, r =>
            r.RequestUri!.AbsoluteUri.Contains("scripthookvdotnet/scripthookvdotnet-nightly/releases/latest")
        );
    }

    /// <summary>Live check against the official sources: only with GMM_LIVE_DOWNLOADS=1.</summary>
    [Fact]
    public async Task Live_official_sources()
    {
        if (Environment.GetEnvironmentVariable("GMM_LIVE_DOWNLOADS") != "1")
            return;
        using var http = BaseDownloads.CreateClient();
        var shv = await BaseDownloads.DownloadScriptHookAsync(http, _dir, CancellationToken.None);
        Assert.True(new FileInfo(shv).Length > 100_000);
        using (var z = System.IO.Compression.ZipFile.OpenRead(shv))
            Assert.Contains(z.Entries, e => e.Name.Equals("ScriptHookV.dll", StringComparison.OrdinalIgnoreCase));
        var shvdn = await BaseDownloads.DownloadShvdnAsync(http, ShvdnVariant.Enhanced, _dir, CancellationToken.None);
        Assert.True(new FileInfo(shvdn).Length > 100_000);
    }
}
