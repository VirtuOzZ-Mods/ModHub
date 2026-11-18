namespace GTAModManager.Core;

/// <summary>
/// Every place ModHub writes on its own behalf. ModHub is portable: all of it lives in the program folder,
/// so deleting that folder removes ModHub. Layout of the program folder:
/// <code>
///   Mod Hub.exe        launcher (release build)
///   Settings.json      user settings
///   Languages\         translation packs
///   Program\           the app, gmm.exe and the .NET runtime
///   Data\              keys, browser profile, downloads, caches, mod records, temp files
/// </code>
/// Game-related state lives next to the game instead (<see cref="Game.GameInstall.WorkDir"/> and the backup folder).
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Marker next to ModHub.exe in the release layout. A "root=.." line points to the program folder
    /// (the app sits in Program\, the user-facing files one level up). Old builds wrote "data=..\ModHub-Data".
    /// </summary>
    public const string Marker = "ModHub.portable";

    /// <summary>Fallback when the program folder is read-only (for example, unpacked into Program Files).</summary>
    public const string DocumentsFolderName = "ModHub";

    private static string? _root;

    /// <summary>The program folder: everything ModHub keeps for itself is under it.</summary>
    public static string Root => _root ??= ChooseRoot(AppContext.BaseDirectory);

    /// <summary>True when the program folder was not writable and data went to Documents\ModHub.</summary>
    public static bool UsesDocumentsFallback { get; private set; }

    public static string SettingsFile => Path.Combine(Root, "Settings.json");
    public static string Languages => Path.Combine(Root, "Languages");
    public static string Data => Path.Combine(Root, "Data");

    /// <summary>Decryption keys read from the user's own game exe, and CodeWalker's magic.dat.</summary>
    public static string GameKeys => Path.Combine(Data, "Game keys");
    public static string KeyCache => Path.Combine(GameKeys, "Cache");
    public static string MagicDat => Path.Combine(GameKeys, "magic.dat");

    /// <summary>Profile of the built-in browser (WebView2): cookies and site data.</summary>
    public static string Browser => Path.Combine(Data, "Browser");

    /// <summary>Archives downloaded through the built-in browser.</summary>
    public static string Downloads => Path.Combine(Data, "Downloads");

    /// <summary>Data that can be rebuilt at any time (mod page metadata, previews).</summary>
    public static string Cache => Path.Combine(Data, "Cache");

    /// <summary>What ModHub remembers about installed mods and the user's choices.</summary>
    public static string Library => Path.Combine(Data, "Library");

    /// <summary>Reports of unexpected errors (crash-*.txt), for the user to send with a bug report.</summary>
    public static string Logs => Path.Combine(Data, "Logs");

    /// <summary>Temp files of ModHub and its child processes; emptied on exit.</summary>
    public static string Temp => Path.Combine(Data, "Temp");

    public static string LibraryFile(string name) => Path.Combine(Library, name);

    /// <summary>Points all paths at another folder (tests).</summary>
    public static void UseRoot(string root)
    {
        _root = Path.GetFullPath(root);
        UsesDocumentsFallback = false;
    }

    /// <summary>The program folder for an exe directory: the marker's "root=" line, otherwise the exe directory itself.</summary>
    public static string ResolveRoot(string baseDirectory)
    {
        baseDirectory = Path.GetFullPath(baseDirectory);
        var marker = Path.Combine(baseDirectory, Marker);
        if (!File.Exists(marker))
            return baseDirectory.TrimEnd('\\');
        try
        {
            foreach (var raw in File.ReadLines(marker))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length == 0 || Path.IsPathRooted(value))
                    continue;
                if (key.Equals("root", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(Path.Combine(baseDirectory, value)).TrimEnd('\\');
                // Old marker: data=..\ModHub-Data means the program folder is the data folder's parent.
                if (key.Equals("data", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(Path.GetFullPath(Path.Combine(baseDirectory, value)))!.TrimEnd('\\');
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return baseDirectory.TrimEnd('\\');
    }

    private static string ChooseRoot(string baseDirectory)
    {
        var root = ResolveRoot(baseDirectory);
        if (CanWrite(root))
            return root;
        UsesDocumentsFallback = true;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DocumentsFolderName);
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".modhub-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Sends temp files of this process (and of the processes it starts) to Data\Temp instead of %TEMP%.</summary>
    public static void UseOwnTemp()
    {
        try
        {
            Directory.CreateDirectory(Temp);
            Environment.SetEnvironmentVariable("TMP", Temp);
            Environment.SetEnvironmentVariable("TEMP", Temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Empties Data\Temp on exit (locked entries are skipped).</summary>
    public static void CleanTemp()
    {
        if (!Directory.Exists(Temp))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(Temp))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
