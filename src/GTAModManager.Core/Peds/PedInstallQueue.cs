using System.Collections.ObjectModel;
using System.ComponentModel;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Peds;

public enum PedQueueStatus
{
    NeedsSetup,
    Ready,
    Installing,
    Failed,
}

public sealed class PedQueueItem(PedArchiveAnalysis analysis) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public PedArchiveAnalysis Analysis { get; } = analysis;
    public string ArchivePath { get; } = Path.GetFullPath(analysis.ArchivePath);
    public string Title => Path.GetFileName(ArchivePath);
    public IReadOnlyList<PedSelection> Selections { get; private set; } = [];
    private bool selected = true;
    public bool Selected
    {
        get => selected;
        set
        {
            selected = value;
            Notify(nameof(Selected));
        }
    }
    public PedQueueStatus State { get; private set; } = PedQueueStatus.NeedsSetup;
    public string Message { get; private set; } = "";
    public string Summary =>
        State switch
        {
            PedQueueStatus.Ready => L.T("core.peds.readyInstallModels", Selections.Count),
            PedQueueStatus.Installing => L.T("core.peds.installing"),
            PedQueueStatus.Failed => Message,
            _ => L.T("core.peds.variantsFoundConfigureBeforeInstallation", Analysis.Candidates.Count),
        };

    internal void Set(IReadOnlyList<PedSelection> selections, PedQueueStatus state, string message = "")
    {
        Selections = selections.ToArray();
        State = state;
        Message = message;
        Notify(nameof(State));
        Notify(nameof(Message));
        Notify(nameof(Summary));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Successful results alone clear the compact queue. Model choices never live in a global archive form.</summary>
public sealed class PedInstallQueue
{
    private readonly ObservableCollection<PedQueueItem> items = [];
    public ReadOnlyObservableCollection<PedQueueItem> Items { get; }

    public PedInstallQueue() => Items = new(items);

    public PedQueueItem Add(PedArchiveAnalysis analysis)
    {
        string path = Path.GetFullPath(analysis.ArchivePath);
        var existing = items.FirstOrDefault(i => i.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;
        if (analysis.Candidates.Count == 0)
            throw new ArgumentException(L.T("core.peds.noCharacterSetsFoundArchive"));
        if (items.Count >= 64)
            throw new ArgumentException(L.T("core.peds.canAddUp64Archives"));
        var item = new PedQueueItem(analysis);
        items.Add(item);
        return item;
    }

    public void Configure(PedQueueItem item, IReadOnlyList<PedSelection> selections)
    {
        if (!items.Contains(item))
            throw new ArgumentException(L.T("core.peds.archiveHasAlreadyBeenRemoved"));
        if (selections.Count == 0)
            throw new ArgumentException(L.T("core.peds.selectAtLeastOneModel"));
        var ids = new HashSet<string>();
        var hashes = new HashSet<uint>();
        foreach (var selection in selections)
        {
            var candidate = item.Analysis.Candidates.SingleOrDefault(c => c.Id == selection.CandidateId)
                ?? throw new ArgumentException(L.T("core.peds.modelVariantMissingFromArchive"));
            if (!ids.Add(candidate.Id) || !hashes.Add(PedMetadata.NameHash(candidate.SpawnName)))
                throw new ArgumentException(L.T("core.peds.selectedVariantsHaveSameGame"));
            if (candidate.LayoutAmbiguous || selection.Layout != candidate.Layout)
                throw new ArgumentException(L.T("core.peds.modelStructureAmbiguousDoesNot"));
        }
        item.Set(selections, PedQueueStatus.Ready);
    }

    public IReadOnlyList<PedInstallRequest> CreateRequests()
    {
        var selected = items.Where(i => i.Selected).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException(L.T("core.peds.selectArchivesInstall"));
        if (selected.Any(i => i.State != PedQueueStatus.Ready))
            throw new InvalidOperationException(L.T("core.peds.configureSelectedArchivesFirst"));
        var hashes = new HashSet<uint>();
        foreach (var item in selected)
        foreach (var selection in item.Selections)
        {
            var candidate = item.Analysis.Candidates.Single(c => c.Id == selection.CandidateId);
            if (!hashes.Add(PedMetadata.NameHash(candidate.SpawnName)))
                throw new InvalidOperationException(
                    L.T("core.peds.sameGameNameSelectedDifferent", candidate.SpawnName)
                );
        }
        return selected.Select(i => new PedInstallRequest(i.ArchivePath, i.Selections)).ToArray();
    }

    public void MarkInstalling(IEnumerable<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => set.Contains(i.ArchivePath)))
            item.Set(item.Selections, PedQueueStatus.Installing);
    }

    public void ApplyOutcomes(IReadOnlyList<PedInstallOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            var item = items.FirstOrDefault(i =>
                i.ArchivePath.Equals(Path.GetFullPath(outcome.ArchivePath), StringComparison.OrdinalIgnoreCase)
            );
            if (item is null)
                continue;
            if (outcome.Succeeded)
                items.Remove(item);
            else
                item.Set(item.Selections, PedQueueStatus.Failed, outcome.Message);
        }
        foreach (var item in items.Where(i => i.State == PedQueueStatus.Installing))
            item.Set(item.Selections, PedQueueStatus.Failed, L.T("core.peds.noResultReceivedCheckInstalled"));
    }

    public void Remove(PedQueueItem item) => items.Remove(item);

    public void Clear() => items.Clear();
}
