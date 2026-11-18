using System.Text;

namespace GTAModManager.Core.Processes;

/// <summary>Relays the existing elevated-process log while it is being written, including a final partial line.</summary>
public static class LiveLogRelay
{
    public static async Task RelayAsync(string path, Task processExit, TextWriter output)
    {
        FileStream? input = null;
        using var pending = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                bool exited = processExit.IsCompleted;
                if (input is null && File.Exists(path))
                    input = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        buffer.Length,
                        FileOptions.Asynchronous
                    );
                if (input is not null)
                {
                    int count;
                    while ((count = await input.ReadAsync(buffer)) > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            pending.WriteByte(buffer[i]);
                            if (buffer[i] != (byte)'\n')
                                continue;
                            await output.WriteAsync(
                                Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length)
                            );
                            pending.SetLength(0);
                            pending.Position = 0;
                        }
                    }
                    await output.FlushAsync();
                }
                if (exited)
                    break;
                await Task.WhenAny(processExit, Task.Delay(100));
            }
            await processExit;
            if (pending.Length > 0)
                await output.WriteAsync(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
            await output.FlushAsync();
        }
        finally
        {
            if (input is not null)
                await input.DisposeAsync();
        }
    }
}
