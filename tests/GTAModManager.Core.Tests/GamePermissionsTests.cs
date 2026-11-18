using System.Security.AccessControl;
using System.Security.Principal;
using GTAModManager.Core.Game;

namespace GTAModManager.Core.Tests;

/// <summary>Game folder write checks and the safety limits of the Fix button.</summary>
public sealed class GamePermissionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gmm-perm-" + Guid.NewGuid().ToString("N"),
        "Grand Theft Auto V Энхансед"
    );

    public GamePermissionsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, true);
        }
        catch { }
    }

    [Fact]
    public void WritableFolder_HasNoProblems_AndLeavesNoProbeFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        Assert.Empty(GamePermissions.UnwritableFolders(_root));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderWithDeniedWrite_IsReported()
    {
        var scripts = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(scripts);
        var sid = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(
            sid,
            FileSystemRights.WriteData | FileSystemRights.CreateFiles,
            AccessControlType.Deny
        );
        var info = new DirectoryInfo(scripts);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            Assert.Equal([scripts], GamePermissions.UnwritableFolders(_root));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public void FolderWithoutGameExe_IsRefused()
    {
        Assert.NotNull(GamePermissions.WhyRefused(_root));
        File.WriteAllText(Path.Combine(_root, "GTA5_Enhanced.exe"), "stub");
        Assert.Null(GamePermissions.WhyRefused(_root)); // a path with spaces and Cyrillic characters
    }

    [Fact]
    public void SystemFoldersAndDriveRoots_AreRefused_EvenWithGameExe()
    {
        foreach (
            var dangerous in new[]
            {
                Path.GetPathRoot(Environment.SystemDirectory)!,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            }.Where(p => p.Length > 0)
        )
            Assert.NotNull(GamePermissions.WhyRefused(dangerous));
    }

    [Fact]
    public void OnlyUserAccountSids_AreAccepted()
    {
        // A regular local or domain user (not the current one: CI runs as the built-in Administrator, refused on purpose).
        Assert.True(GamePermissions.IsUserAccountSid("S-1-5-21-1111111111-2222222222-3333333333-1001"));
        Assert.False(GamePermissions.IsUserAccountSid("S-1-5-21-1111111111-2222222222-3333333333-500")); // built-in Administrator
        Assert.False(GamePermissions.IsUserAccountSid("S-1-1-0")); // Everyone
        Assert.False(GamePermissions.IsUserAccountSid("S-1-5-32-545")); // Users
        Assert.False(GamePermissions.IsUserAccountSid("S-1-5-11")); // Authenticated Users
        Assert.False(GamePermissions.IsUserAccountSid("S-1-5-18")); // SYSTEM
        Assert.False(GamePermissions.IsUserAccountSid("not a sid & del *"));
    }

    [Fact]
    public void Grant_RefusesBadInputBeforeRunningAnything()
    {
        Assert.Equal(2, GamePermissions.Grant(_root, GamePermissions.CurrentUserSid()).ExitCode); // no game exe
        File.WriteAllText(Path.Combine(_root, "GTA5.exe"), "stub");
        Assert.Equal(2, GamePermissions.Grant(_root, "S-1-5-32-545").ExitCode); // the Users group
    }
}
