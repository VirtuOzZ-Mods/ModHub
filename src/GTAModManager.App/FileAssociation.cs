using System.IO;
using System.Runtime.InteropServices;
using GTAModManager.Core.Localization;
using Microsoft.Win32;

namespace GTAModManager.App;

/// <summary>
/// Associates .oiv with the app for the current user (HKCU, no administrator rights required):
/// the app appears in Open With; if the user has not chosen a program for .oiv, double-click opens it here.
/// Windows does not allow overriding the user's own default choice (UserChoice is protected).
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "GTAModManager.oiv";
    private const string Ext = ".oiv";

    public enum State
    {
        NotRegistered,
        Registered,
        Default,
    }

    private static string ExePath => Environment.ProcessPath!;

    public static State Check()
    {
        using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        if (cmd?.GetValue(null) is not string value || !value.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
            return State.NotRegistered;
        var choice = UserChoiceProgId();
        if (choice is not null)
            return choice == ProgId ? State.Default : State.Registered;
        using var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Ext}");
        return ext?.GetValue(null) as string == ProgId ? State.Default : State.Registered;
    }

    /// <summary>The program the user chose for .oiv with "Always" (if any).</summary>
    public static string? UserChoiceProgId()
    {
        using var k = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{Ext}\UserChoice"
        );
        return k?.GetValue("ProgId") as string;
    }

    public static void Register()
    {
        var exe = ExePath;
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, L.T("ui.fileAssociation.typeName"));
            prog.SetValue("FriendlyTypeName", L.T("ui.fileAssociation.typeName"));
            using (var icon = prog.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            using (var open = prog.CreateSubKey(@"shell\open"))
                open.SetValue("FriendlyAppName", "Mod Hub by VirtuOzZ");
        }
        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Ext}"))
        {
            // Set the default value only if .oiv is not yet taken by anything for this user.
            if (ext.GetValue(null) is not string current || string.IsNullOrEmpty(current) || current == ProgId)
                ext.SetValue(null, ProgId);
            using var withProgIds = ext.CreateSubKey("OpenWithProgids");
            withProgIds.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(exe)}"))
        {
            app.SetValue("FriendlyAppName", "Mod Hub by VirtuOzZ");
            using (var types = app.CreateSubKey("SupportedTypes"))
                types.SetValue(Ext, "");
            using (var cmd = app.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Classes\Applications\{Path.GetFileName(ExePath)}",
            throwOnMissingSubKey: false
        );
        using (var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Ext}", writable: true))
        {
            if (ext is not null)
            {
                if (ext.GetValue(null) as string == ProgId)
                    ext.DeleteValue("", throwOnMissingValue: false);
                using var withProgIds = ext.OpenSubKey("OpenWithProgids", writable: true);
                withProgIds?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
        NotifyShell();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell() =>
        SHChangeNotify(
            0x08000000 /* SHCNE_ASSOCCHANGED */
            ,
            0,
            IntPtr.Zero,
            IntPtr.Zero
        );
}
