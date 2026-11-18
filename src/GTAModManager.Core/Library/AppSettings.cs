using System.Text.Json;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Library;

/// <summary>User settings (Settings.json in the program folder), shared by the window and gmm.exe.</summary>
public sealed class AppSettings
{
    public string? Legacy { get; set; }
    public string? Enhanced { get; set; }
    public string? LastEdition { get; set; }
    public string? LanguagePackId { get; set; }
    public bool KeepDownloadedArchives { get; set; }
    public string? ArchiveLibrary { get; set; }

    /// <summary>Button names in Hotkeys → Gamepad: "xbox" (default) or "playstation".</summary>
    public string? GamepadLayout { get; set; }

    /// <summary>Backup folder chosen by the user.</summary>
    public string? BackupDir { get; set; }

    /// <summary>The user opted out of backups: mods are installed without the ability to undo.</summary>
    public bool BackupsDeclined { get; set; }

    /// <summary>The user has not been asked about backups yet.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool BackupChoicePending => BackupDir is null && !BackupsDeclined;

    public static string FilePath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException)
        {
            // A corrupted file is never silently overwritten: keep a copy so the backup path can be recovered.
            try
            {
                File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
            }
            catch (IOException) { }
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        MirrorToBackups();
    }

    /// <summary>Refreshes the settings copy in the backup folder (see <see cref="SettingsMirror"/>); never fails the caller.</summary>
    public void MirrorToBackups()
    {
        if (BackupDir is null || BackupsDeclined)
            return;
        try
        {
            SettingsMirror.Save(BackupDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public BackupLibrary? OpenLibrary() => BackupDir is null ? null : BackupLibrary.TryOpen(BackupDir);
}
