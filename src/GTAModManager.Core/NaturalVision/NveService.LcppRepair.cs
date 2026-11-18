using System.IO.Compression;
using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.NaturalVision;

/// <summary>
/// Repair for LCPP installations made before Mod Hub patched WorldTravelPatches.asi at install time. The fixed
/// file is applied as one more component of the same installation (like the preserved adjusters): its own
/// transaction and backup, undone first when LCPP is removed, so the original core parts still undo cleanly.
/// </summary>
public sealed partial class NveService
{
    /// <summary>Tests turn this off to reproduce installations made before the install-time patch.</summary>
    internal static bool PatchOnInstall { get; set; } = true;

    const string WorldTravelFixComponent = "lcpp-worldtravel-fix";
    const string WorldTravelFixCache = "worldtravel-fix.oiv";

    /// <summary>
    /// Whether this installation still has the unpatched WorldTravelPatches.asi in the game folder.
    /// Read-only: inspects the state and one file.
    /// </summary>
    public static bool NeedsWorldTravelRepair(GameInstall game, NveState state)
    {
        if (
            !state.IsLcpp
            || state.Phase != "Installed"
            || state.Components.Any(c => c.Component.Id == WorldTravelFixComponent && c.Active)
            || !Path.GetFullPath(state.GameRoot).Equals(Path.GetFullPath(game.Root), StringComparison.OrdinalIgnoreCase)
        )
            return false;
        return PatchedWorldTravel(Path.Combine(game.Root, WorldTravelPatches)) is not null;
    }

    /// <summary>Patched bytes of the file, or null when it is missing, too large or needs no patch.</summary>
    static byte[]? PatchedWorldTravel(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 2 or > 32L << 20)
                return null;
            var bytes = File.ReadAllBytes(path);
            return bytes[0] == 'M' && bytes[1] == 'Z' && ReplaceOnce(bytes, PopZoneSignature, PopZoneSignatureAnyJump)
                ? bytes
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public NveResult RepairWorldTravelPatches(NveRemoveRequest request, Action<string>? progress = null)
    {
        using var gate = GameOperationGate.Acquire(game);
        using var backupGate = GameOperationGate.Acquire(library.Root);
        Guard();
        var state = store.Load(request.InstallationId);
        if (!state.IsLcpp || state.Phase != "Installed")
            throw new InvalidOperationException(L.T("lcpp.repair.notInstalled"));
        if (!Path.GetFullPath(state.BackupRoot).Equals(library.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("core.lcpp.backupLibraryUnavailable"));
        RecoverPending(state);
        if (state.Components.Any(c => c.Component.Core && !c.Active))
            throw new InvalidOperationException(L.T("core.lcpp.finishRecoveryFirst"));
        if (state.Components.Any(c => c.Component.Id == WorldTravelFixComponent && c.Active))
            return new(true, state.Id, L.T("lcpp.repair.alreadyDone"));
        var patched = PatchedWorldTravel(Path.Combine(game.Root, WorldTravelPatches));
        if (patched is null)
            return new(true, state.Id, L.T("lcpp.repair.notNeeded"));
        // Only this one file is replaced, so only it is checked: it must be exactly what LCPP installed, otherwise
        // another program owns it now. Shared archives (update.rpf) may legitimately carry later edits of other
        // mods; they are not touched here.
        if (
            !state.FinalFiles.TryGetValue(WorldTravelPatches, out var expected)
            || FileTransactionEngine.HashOrNull(Path.Combine(game.Root, WorldTravelPatches)) != expected
        )
            throw new IOException(L.T("lcpp.conflict", WorldTravelPatches));

        string cache = NveStore.Cache(state, WorldTravelFixCache);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        if (File.Exists(cache))
            File.Delete(cache);
        using (var zip = ZipFile.Open(cache, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("content/" + WorldTravelPatches).Open())
                output.Write(patched);
            var xml = new XDocument(
                new XElement(
                    "package",
                    new XAttribute("version", "2.2"),
                    new XAttribute("target", "Five"),
                    new XElement("metadata", new XElement("name", "LCPP WorldTravelPatches fix")),
                    new XElement(
                        "content",
                        new XElement("add", new XAttribute("source", WorldTravelPatches), WorldTravelPatches)
                    )
                )
            );
            using var writer = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
            writer.Write(xml.ToString());
        }
        var item = state.Components.SingleOrDefault(c => c.Component.Id == WorldTravelFixComponent);
        if (item is not null)
            state.Components.Remove(item);
        item = new NveInstalledComponent
        {
            Component = new(
                WorldTravelFixComponent,
                WorldTravelFixCache,
                L.T("lcpp.repair.title"),
                L.T("lcpp.repair.description"),
                "",
                "",
                int.MaxValue,
                false,
                0
            ),
            CacheFile = WorldTravelFixCache,
            CacheHash = FileTransactionEngine.Hash(cache),
        };
        state.Components.Add(item);
        state.Phase = "Updating";
        store.Save(state);
        try
        {
            Apply(state, item, progress);
            state.Phase = "Installed";
            state.Message = "";
            store.Save(state);
            return new(true, state.Id, L.T("lcpp.repair.done"));
        }
        catch (Exception ex)
        {
            RecoverPending(state);
            try
            {
                if (item.Active)
                {
                    PreflightLcpp(state, [item]);
                    RemoveSteps(state, [item], progress, rollback: true);
                }
                state.Components.Remove(item);
                state.Phase = "Installed";
                state.Message = ex.Message;
            }
            catch (Exception rollback)
            {
                state.Phase = "NeedsAttention";
                state.Message = ex.Message + "\n" + rollback.Message;
            }
            store.Save(state);
            return new(false, state.Id, state.Message);
        }
    }
}
