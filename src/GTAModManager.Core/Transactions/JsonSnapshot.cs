using System.Text.Json;

namespace GTAModManager.Core.Transactions;

/// <summary>Readers of atomically replaced metadata must allow the old file to be renamed on Windows.</summary>
internal static class JsonSnapshot
{
    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            return JsonSerializer.Deserialize<T>(stream, options);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
    }
}
