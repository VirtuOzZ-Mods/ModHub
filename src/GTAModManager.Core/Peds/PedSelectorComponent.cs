using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Peds;

public sealed record PedSelectorStatus(
    bool InstalledOwn,
    bool InstalledThirdParty,
    IReadOnlyList<string> ThirdPartyFiles,
    IReadOnlyList<string> MissingDependencies
)
{
    public bool HasSelector => InstalledOwn || InstalledThirdParty;

    /// <summary>Presence check only; actual game loading must be verified in Story Mode.</summary>
    public bool Ready => HasSelector && MissingDependencies.Count == 0;
}

/// <summary>Own optional native selector. Third-party selectors are detected read-only.</summary>
public sealed class PedSelectorComponent(GameInstall game)
{
    public const string AsiPath = "GMMPedSelector.asi";
    public const string IniPath = "GMMPedSelector.ini";
    public const string OwnerPath = @"GMMAddonPeds\selector-owner.txt";
    public const string OwnerMarker = "GMM native ped selector v1\n";
    public const string DefaultIni = "[Menu]\nMenuKey=L ; Default L\n";

    /// <summary>Folder for saved looks (Customize → Save look): the only place the game can write to without administrator rights.</summary>
    public const string LooksDir = "GMMPedSelector";

    public bool IsInstalled => Inspect().InstalledOwn;

    /// <summary>
    /// The game root is read-only for a regular user, yet the in-game menu saves looks. We create a single
    /// GMMPedSelector folder and make only it writable (Modify for Users). Runs when the menu is installed or updated
    /// with administrator rights; without them it silently does nothing. The rest of the game stays protected.
    /// </summary>
    public void EnsureLooksFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var dir = Path.Combine(game.Root, LooksDir);
            if (File.Exists(dir))
                return;
            var info = Directory.CreateDirectory(dir);
            if (IsReparse(dir))
                return;
            var users = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid,
                null
            );
            var acl = info.GetAccessControl();
            bool has = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .OfType<System.Security.AccessControl.FileSystemAccessRule>()
                .Any(r =>
                    r.IdentityReference == users
                    && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                    && (r.FileSystemRights & System.Security.AccessControl.FileSystemRights.Modify)
                        == System.Security.AccessControl.FileSystemRights.Modify
                );
            if (has)
                return;
            acl.AddAccessRule(
                new System.Security.AccessControl.FileSystemAccessRule(
                    users,
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit
                        | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow
                )
            );
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    public PedSelectorStatus Inspect()
    {
        var thirdParty = new List<string>();
        foreach (var rootName in new[] { "AddonPeds.asi", "PedSelector.asi" })
            if (PeFile(Path.Combine(game.Root, rootName)))
                thirdParty.Add(rootName);
        var scripts = Path.Combine(game.Root, "scripts");
        if (Directory.Exists(scripts) && !IsReparse(scripts))
        {
            // The current Ped Selector and the older AddonPeds selector both use
            // scripts/PedSelector.dll. A single child level covers SHVDN subfolders.
            var dirs = new List<string> { scripts };
            dirs.AddRange(Directory.EnumerateDirectories(scripts).Take(256).Where(d => !IsReparse(d)));
            foreach (var dir in dirs)
            foreach (var name in new[] { "PedSelector.dll", "AddonPeds.dll" })
                if (PeFile(Path.Combine(dir, name)))
                    thirdParty.Add(Path.GetRelativePath(game.Root, Path.Combine(dir, name)));
        }
        var missing = new List<string>();
        if (game.Edition != GameEdition.Legacy)
            missing.Add(L.T("core.peds.onlyGtaVLegacySupported"));
        if (!PeFile(Path.Combine(game.Root, "ScriptHookV.dll")))
            missing.Add("ScriptHookV.dll (Script Hook V)");
        if (
            !new[] { "dinput8.dll", "dsound.dll" }.Any(n => PeFile(Path.Combine(game.Root, n)))
            && !new[] { "version.dll", "winmm.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll" }.Any(n =>
                RecognizedAlternativeLoader(Path.Combine(game.Root, n))
            )
        )
            missing.Add(L.T("core.peds.asiLoaderSuchAsDinput8"));
        if (!game.CheckModLoader().Installed)
            missing.Add(game.CheckModLoader().Message);
        return new(PeFile(Path.Combine(game.Root, AsiPath)) && HasOwner(), thirdParty.Count > 0, thirdParty, missing);
    }

    public InstallPlan PrepareInstall(string packagedAsi, IEnumerable<PedEntry>? externalEntries = null) =>
        Prepare(L.T("core.peds.installingGmmPedSelector"), () =>
        {
            CheckOwnership();
            EnsureLooksFolder();
            if (!PeFile(packagedAsi))
                throw new ArgumentException(L.T("core.peds.gmmpedselectorAsiMissingNotPe"));
            var source = new SelectorSource();
            var ops = new List<OivOp>
            {
                new OivAdd(Path.GetFullPath(packagedAsi), AsiPath),
                new OivAdd(source.Text(OwnerMarker), OwnerPath),
                new OivAdd(
                    source.Text(new PedInstaller(game).BuildSelectorModels(externalEntries)),
                    PedInstaller.ModelsPath
                ),
            };
            if (!File.Exists(Path.Combine(game.Root, IniPath)))
                ops.Add(new OivAdd(source.Text(DefaultIni), IniPath));
            var missingDependencies = Inspect().MissingDependencies;
            var plan = new OivInstaller(game).Prepare(L.T("core.peds.installingGmmPedSelector"), ops, source);
            foreach (var missing in missingDependencies)
                plan.Log.Add(L.T("core.peds.requiredLoadGame", missing));
            return plan;
        });

    /// <summary>Looks saved in game by Customize → Save look.</summary>
    public const string LooksFile = LooksDir + @"\looks.txt";

    /// <param name="withLooks">Also delete the looks saved in game (full removal together with the characters).</param>
    public InstallPlan PrepareRemove(bool withLooks = false) => Prepare(L.T("core.peds.removingGmmPedSelector"), () =>
        {
            if (!HasOwner())
                throw new ArgumentException(L.T("core.peds.componentDoesNotBelongModhub"));
            var ops = new List<OivOp> { new OivDelete(AsiPath), new OivDelete(IniPath), new OivDelete(OwnerPath) };
            if (withLooks && File.Exists(Path.Combine(game.Root, LooksFile)))
                ops.Add(new OivDelete(LooksFile));
            return new OivInstaller(game).Prepare(L.T("core.peds.removingGmmPedSelector"), ops);
        });

    public InstallPlan PrepareSync(IEnumerable<PedEntry>? externalEntries = null) =>
        Prepare(L.T("core.peds.updatingGmmPedSelectorList"), () =>
        {
            // The old gmm_peds pack or a dropped-in original pedselector: tidy up the pack first
            // (migration / attachment); the menu list is updated in the same operation.
            var installer = new PedInstaller(game);
            if (installer.NeedsPackUpdate())
                return installer.PrepareUpdatePack(externalEntries);
            CheckOwnership();
            EnsureLooksFolder();
            var source = new SelectorSource();
            return new OivInstaller(game).Prepare(
                L.T("core.peds.updatingGmmPedSelectorList"),
                [
                    new OivAdd(
                        source.Text(new PedInstaller(game).BuildSelectorModels(externalEntries)),
                        PedInstaller.ModelsPath
                    ),
                ],
                source
            );
        });

    private InstallPlan Prepare(string title, Func<InstallPlan> action)
    {
        using var gate = GameOperationGate.Acquire(game);
        try
        {
            if (game.Edition != GameEdition.Legacy)
                throw new ArgumentException(L.T("core.peds.gmmPedSelectorSupportsGta"));
            if (game.IsRunning())
                throw new ArgumentException(L.T("core.peds.gameRunningPedSelectorChanges"));
            if (new ModModeService(game).Inspect().WritesBlocked)
                throw new ArgumentException(L.T("core.peds.enableModsFinishRecoveryFirst"));
            return action();
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or IOException
                        or UnauthorizedAccessException
                        or System.Xml.XmlException
                        or System.Text.Json.JsonException
                        or InvalidOperationException
                        or Rpf.RpfException
                        or OivFormatException
            )
        {
            var plan = new InstallPlan
            {
                Title = title,
                PrepDir = Path.Combine(game.WorkDir, "prep", "selector-rejected-" + Guid.NewGuid().ToString("N")),
            };
            plan.Errors.Add(ex.Message);
            return plan;
        }
    }

    private void CheckOwnership()
    {
        if (File.Exists(Path.Combine(game.Root, OwnerPath)) && !HasOwner())
            throw new ArgumentException(L.T("core.peds.unknownPedSelectorMarkerAutomatic"));
        if (
            !HasOwner()
            && (File.Exists(Path.Combine(game.Root, AsiPath)) || File.Exists(Path.Combine(game.Root, IniPath)))
        )
            throw new ArgumentException(L.T("core.peds.gmmpedselectorAsiIniAlreadyExists"));
    }

    private bool HasOwner()
    {
        var path = Path.Combine(game.Root, OwnerPath);
        try
        {
            return File.Exists(path)
                && !IsReparse(path)
                && new FileInfo(path).Length < 256
                && File.ReadAllText(path) == OwnerMarker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PeFile(string path)
    {
        try
        {
            if (!File.Exists(path) || IsReparse(path))
                return false;
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool RecognizedAlternativeLoader(string path)
    {
        if (!PeFile(path))
            return false;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            string identity = string.Join(" ", info.FileDescription, info.ProductName, info.CompanyName);
            return identity.Contains("ASI", StringComparison.OrdinalIgnoreCase)
                && identity.Contains("loader", StringComparison.OrdinalIgnoreCase)
                && (
                    identity.Contains("Alexander Blade", StringComparison.OrdinalIgnoreCase)
                    || identity.Contains("Ultimate ASI", StringComparison.OrdinalIgnoreCase)
                );
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed class SelectorSource : IModSource
    {
        private readonly Dictionary<string, byte[]> _texts = [];

        public string Text(string text)
        {
            string key = "gmm-selector:" + Guid.NewGuid().ToString("N");
            _texts[key] = new UTF8Encoding(false).GetBytes(text);
            return key;
        }

        public Stream OpenSource(string source) =>
            _texts.TryGetValue(source, out var data)
                ? new MemoryStream(data, false)
                : LocalFileSource.Instance.OpenSource(source);

        public long SourceSize(string source) =>
            _texts.TryGetValue(source, out var data) ? data.Length : LocalFileSource.Instance.SourceSize(source);
    }
}
