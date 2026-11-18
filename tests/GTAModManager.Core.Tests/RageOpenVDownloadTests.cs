using System.IO.Compression;
using System.Net;
using System.Text.Json;
using GTAModManager.Core.Base;
using GTAModManager.Core.Discover;
using Xunit;

namespace GTAModManager.Core.Tests;

/// <summary>RageOpenV is never shipped: only the authors' pinned GitHub release, verified by SHA-256, is installed.</summary>
public sealed class RageOpenVDownloadTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("gmm-rageopenv-").FullName;

    public void Dispose() => Directory.Delete(directory, true);

    sealed class Fake(string json, byte[] archive) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content =
                        request.RequestUri.Host == "api.github.com"
                            ? new StringContent(json)
                            : new ByteArrayContent(archive),
                }
            );
        }
    }

    private static byte[] Zip(string asiText)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(RageOpenVRelease.AsiEntry).Open()))
            writer.Write(asiText);
        return memory.ToArray();
    }

    private static string Release(string url) =>
        JsonSerializer.Serialize(
            new { tag_name = RageOpenVRelease.Tag, assets = new[] { new { name = RageOpenVRelease.Asset, browser_download_url = url } } }
        );

    [Fact]
    public async Task An_archive_that_is_not_the_reviewed_release_is_rejected_before_extraction()
    {
        var fake = new Fake(Release(RageOpenVRelease.DownloadUrl), Zip("MZ tampered"));
        using var http = BaseDownloads.CreateClient(fake);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BaseDownloads.DownloadRageOpenVAsync(http, directory, CancellationToken.None)
        );

        Assert.EndsWith("/releases/tags/" + RageOpenVRelease.Tag, fake.Requests[0]);
        Assert.Equal(RageOpenVRelease.DownloadUrl, fake.Requests[1]);
        Assert.False(File.Exists(Path.Combine(directory, "RageOpenV", RageOpenVRelease.AsiEntry)));
    }

    [Fact]
    public async Task A_release_pointing_somewhere_else_is_not_downloaded()
    {
        var fake = new Fake(Release("https://example.org/RageOpenV.zip"), Zip("MZ"));
        using var http = BaseDownloads.CreateClient(fake);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BaseDownloads.DownloadRageOpenVAsync(http, directory, CancellationToken.None)
        );
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void The_pin_points_at_the_authors_repository_and_the_catalog_card_at_their_mod_page()
    {
        Assert.Equal("https://github.com/Chiheb-Bacha/RageOpenV/releases/download/v1.0/RageOpenV.zip", RageOpenVRelease.DownloadUrl);
        Assert.Equal(64, RageOpenVRelease.ArchiveSha256.Length);
        var card = DiscoverCatalog.LoadBuiltIn().Entries.Single(e => e.Id == "rageopenv");
        Assert.Equal(RageOpenVRelease.ModPage.AbsoluteUri, card.Url);
        Assert.Equal("base", card.Install);
        Assert.Equal("essentials", card.Category);
    }
}
