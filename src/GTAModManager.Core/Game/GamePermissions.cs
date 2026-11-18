using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Game;

/// <summary>
/// Write access to the game folder. After installing or reinstalling GTA V the folder may have inheritance disabled
/// and Users get only Read &amp; Execute: then Script Hook V, ScriptHookVDotNet and mods cannot
/// write logs, settings and saves. The check is a real write attempt; the fix runs in a separate process
/// with UAC and only adds Modify for the current user (nothing is removed, inheritance is left alone).
/// </summary>
public static class GamePermissions
{
    /// <summary>Folders that could not be written to: the game root, plus scripts and mods if present.</summary>
    public static IReadOnlyList<string> UnwritableFolders(string root)
    {
        var failed = new List<string>();
        foreach (var dir in new[] { root, Path.Combine(root, "scripts"), Path.Combine(root, "mods") })
        {
            if (!Directory.Exists(dir))
                continue;
            if (!CanWrite(dir))
                failed.Add(dir);
        }
        return failed;
    }

    /// <summary>Creates a uniquely named file, writes a couple of bytes and deletes it immediately.</summary>
    public static bool CanWrite(string dir)
    {
        var probe = Path.Combine(dir, ".modhub-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (
                var fs = new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose
                )
            )
                fs.Write([0x4D, 0x48]);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                    File.Delete(probe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    /// <summary>
    /// Why this folder cannot be used (null if it can): it must contain the game exe and must not be a drive root,
    /// Windows, the whole Program Files folder or the whole user profile.
    /// </summary>
    public static string? WhyRefused(string root)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return L.T("core.game.folderNotFound", root);
        }
        if (!Directory.Exists(full))
            return L.T("core.game.folderNotFound", root);
        var dangerous = new[]
        {
            Path.GetPathRoot(full) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        }
            .Where(p => p.Length > 0)
            .Select(Path.TrimEndingDirectorySeparator);
        if (dangerous.Any(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase)))
            return L.T("core.game.refusedSystemFolderDriveRoot", full);
        if (!File.Exists(Path.Combine(full, "GTA5.exe")) && !File.Exists(Path.Combine(full, "GTA5_Enhanced.exe")))
            return L.T("core.game.refusedContainsNeitherGta5Exe", full);
        return null;
    }

    /// <summary>SID of the currently signed-in user (not the administrator the UAC process will later run as).</summary>
    public static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException(L.T("core.game.couldNotDetermineCurrentWindows"));

    /// <summary>The right is granted only to a user account (S-1-5-21-…), never to groups such as Everyone or Users.</summary>
    public static bool IsUserAccountSid(string sid)
    {
        try
        {
            var s = new SecurityIdentifier(sid);
            return s.AccountDomainSid is not null
                && s.IsAccountSid()
                && !s.IsWellKnown(WellKnownSidType.AccountAdministratorSid)
                && !s.IsWellKnown(WellKnownSidType.AccountGuestSid);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Grants the user Modify on the folder and everything inside: icacls &lt;folder&gt; /grant *SID:(OI)(CI)M /T /C.
    /// Arguments are passed as a list (no command-line concatenation). Must be called from an elevated process.
    /// </summary>
    public static (int ExitCode, string Output) Grant(string root, string sid)
    {
        if (WhyRefused(root) is { } why)
            return (2, why);
        if (!IsUserAccountSid(sid))
            return (2, L.T("core.game.couldNotDetermineCurrentWindows"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // icacls writes in the console (OEM) code page, not UTF-8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "icacls.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        psi.ArgumentList.Add(full);
        psi.ArgumentList.Add("/grant");
        psi.ArgumentList.Add("*" + sid + ":(OI)(CI)M");
        psi.ArgumentList.Add("/T");
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add("/Q");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }
}
