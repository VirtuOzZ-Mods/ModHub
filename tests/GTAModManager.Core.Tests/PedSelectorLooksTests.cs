using System.Security.AccessControl;
using System.Security.Principal;
using GTAModManager.Core.Game;
using GTAModManager.Core.Peds;
using Xunit;

namespace GTAModManager.Core.Tests;

public class PedSelectorLooksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gmm-looks-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch { }
    }

    [Fact]
    public void Looks_folder_is_created_and_writable_for_users()
    {
        File.WriteAllText(Path.Combine(_dir, "GTA5.exe"), "x");
        var selector = new PedSelectorComponent(GameInstall.Open(_dir));
        selector.EnsureLooksFolder();
        selector.EnsureLooksFolder(); // second run: no duplicate rules
        var dir = new DirectoryInfo(Path.Combine(_dir, PedSelectorComponent.LooksDir));
        Assert.True(dir.Exists);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rules = dir.GetAccessControl()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(r =>
                r.IdentityReference == users
                && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify
            )
            .ToList();
        Assert.Single(rules);
    }
}
