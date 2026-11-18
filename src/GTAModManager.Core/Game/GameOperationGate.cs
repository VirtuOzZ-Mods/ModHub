using System.Security.Cryptography;
using System.Text;

namespace GTAModManager.Core.Game;

/// <summary>One reentrant, cross-process mutation gate per canonical game root.</summary>
public static class GameOperationGate
{
    public static IDisposable Acquire(GameInstall game) => Acquire(game.Root);

    public static IDisposable Acquire(string root)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var mutex = new Mutex(false, "GMM-ModMode-" + id);
        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException) { }
            return new Lease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
