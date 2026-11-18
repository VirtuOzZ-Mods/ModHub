using System.IO.Compression;
using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.NaturalVision;

public sealed partial class NveService
{
    const string LcppAdjusterComponent = "lcpp-adjusters";
    const string LcppAdjusterCache = "preserved-adjusters.oiv";
    static IEnumerable<AdjusterComponent> LcppRuntimeGroups =>
        AdjusterComponents.Catalog.Where(c => c.Id is "heap" or "packfile");

    static IEnumerable<string> RuntimeNames(AdjusterComponent group) =>
        group
            .InstalledNames.Concat(group.InstalledNames.Select(n => Path.ChangeExtension(n, ".ini")))
            .Append(group.Ini)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    void CachePreservedAdjusters(NveState state, OivPackage package)
    {
        var skipped = package
            .Ops.OfType<OivAdd>()
            .Where(add =>
            {
                var target = GameInstall.Normalize(add.Target);
                return !target.Contains('\\')
                    && LcppRuntimeGroups.Any(g =>
                        RuntimeNames(g).Contains(target, StringComparer.OrdinalIgnoreCase)
                        && (AdjusterComponents.Present(game, g.Id) || File.Exists(Path.Combine(game.Root, target)))
                    );
            })
            .ToArray();
        if (skipped.Length == 0)
            return;
        var content = new XElement("content");
        foreach (
            var group in LcppRuntimeGroups.Where(g =>
                skipped.Any(a =>
                    g.InstalledNames.Contains(GameInstall.Normalize(a.Target), StringComparer.OrdinalIgnoreCase)
                )
            )
        )
        foreach (var name in RuntimeNames(group))
            content.Add(new XElement("delete", name));
        string path = NveStore.Cache(state, LcppAdjusterCache);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var add in skipped)
            {
                string target = GameInstall.Normalize(add.Target);
                if (package.SourceSize(add.Source) > 32L << 20)
                    throw new InvalidDataException(L.T("core.lcpp.adjusterTooLarge"));
                using (var input = package.OpenSource(add.Source))
                using (var output = zip.CreateEntry("content/" + target).Open())
                    input.CopyTo(output);
                content.Add(new XElement("add", new XAttribute("source", target), target));
                state.PreservedAdjusters.Add(target);
            }
            var xml = new XDocument(
                new XElement(
                    "package",
                    new XAttribute("version", "2.2"),
                    new XAttribute("target", "Five"),
                    new XElement("metadata", new XElement("name", "LCPP adjusters")),
                    content
                )
            );
            using var writer = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
            writer.Write(xml.ToString());
        }
        state.PreservedAdjusterHash = FileTransactionEngine.Hash(path);
    }

    public NveResult InstallPreservedAdjusters(NveRemoveRequest request, Action<string>? progress = null)
    {
        using var gate = GameOperationGate.Acquire(game);
        using var backupGate = GameOperationGate.Acquire(library.Root);
        Guard();
        var state = store.Load(request.InstallationId);
        if (
            !state.IsLcpp
            || state.Phase == "Removed"
            || state.PreservedAdjusters.Count == 0
            || state.PreservedAdjusterHash is null
        )
            throw new InvalidOperationException(L.T("core.lcpp.noSavedAdjusters"));
        if (!Path.GetFullPath(state.BackupRoot).Equals(library.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("core.lcpp.backupLibraryUnavailable"));
        RecoverPending(state);
        var item = state.Components.SingleOrDefault(c => c.Component.Id == LcppAdjusterComponent);
        if (item?.Active == true)
        {
            state.Phase = "Installed";
            store.Save(state);
            return new(true, state.Id, L.T("core.lcpp.adjustersAlreadyInstalled"));
        }
        if (state.Components.Any(c => c.Component.Core && !c.Active))
            throw new InvalidOperationException(L.T("core.lcpp.finishRecoveryFirst"));
        // Validate the whole installation before changing any shared runtime file.
        PreflightLcpp(state, state.Components.Where(c => c.Active).ToArray());
        item ??= new NveInstalledComponent
        {
            Component = new(
                LcppAdjusterComponent,
                LcppAdjusterCache,
                L.T("core.lcpp.adjusterTitle"),
                L.T("core.lcpp.adjusterDescription"),
                "",
                "",
                int.MaxValue,
                false,
                0
            ),
            CacheFile = LcppAdjusterCache,
            CacheHash = state.PreservedAdjusterHash,
        };
        CheckCache(state, item);
        if (!state.Components.Contains(item))
            state.Components.Add(item);
        state.Phase = "Updating";
        store.Save(state);
        try
        {
            Apply(state, item, progress);
            state.Phase = "Installed";
            state.Message = "";
            store.Save(state);
            return new(true, state.Id, L.T("core.lcpp.adjustersInstalled"));
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
