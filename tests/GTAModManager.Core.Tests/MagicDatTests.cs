using System.Net;
using GTAModManager.Core.Crypto;

namespace GTAModManager.Core.Tests;

public sealed class MagicDatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gmm-magic-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    private sealed class Fake(HttpStatusCode code, byte[] body) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public void Wrong_file_from_network_is_rejected_and_not_saved()
    {
        var path = Path.Combine(_dir, "magic.dat");
        var ex = Assert.Throws<KeysNotFoundException>(() =>
            MagicDat.Ensure(path, new Fake(HttpStatusCode.OK, [1, 2, 3]))
        );
        Assert.Contains("контрольная сумма", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Network_error_gives_clear_message()
    {
        var ex = Assert.Throws<KeysNotFoundException>(() =>
            MagicDat.Ensure(Path.Combine(_dir, "magic.dat"), new Fake(HttpStatusCode.NotFound, []))
        );
        Assert.Contains("не удалось скачать magic.dat", ex.Message);
    }

    [Fact]
    public void Existing_file_is_not_downloaded_again()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "magic.dat");
        File.WriteAllText(path, "users own");
        var fake = new Fake(HttpStatusCode.OK, []);
        MagicDat.Ensure(path, fake);
        Assert.Equal(0, fake.Calls);
        Assert.Equal("users own", File.ReadAllText(path));
    }

    /// <summary>
    /// Clean machine: neither a key cache nor magic.dat, only the game exe. The keys must come out the same
    /// as on the developer's machine. Only with GMM_LIVE_KEYS=game folder (network and a real exe).
    /// </summary>
    [Fact]
    public void Live_clean_machine_gets_same_keys()
    {
        var game = Environment.GetEnvironmentVariable("GMM_LIVE_KEYS");
        if (string.IsNullOrEmpty(game))
            return;
        var exe = Path.Combine(
            game,
            File.Exists(Path.Combine(game, "GTA5_Enhanced.exe")) ? "GTA5_Enhanced.exe" : "GTA5.exe"
        );
        var fresh = Gta5Keys.Load(exe, Path.Combine(_dir, "keys"), Path.Combine(_dir, "magic.dat"));
        Assert.True(MagicDat.IsValid(Path.Combine(_dir, "magic.dat")));
        var mine = Gta5Keys.Load(
            exe,
            AppPaths.KeyCache,
            AppPaths.MagicDat
        );
        Assert.Equal(mine.AesKey, fresh.AesKey);
        Assert.Equal(mine.HashLut, fresh.HashLut);
        Assert.Equal(mine.NgKeys.SelectMany(k => k), fresh.NgKeys.SelectMany(k => k));
    }
}
