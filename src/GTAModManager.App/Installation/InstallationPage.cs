using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Transactions;
using Microsoft.Win32;

namespace GTAModManager.App.Installation;

public sealed class InstallationPage : UserControl
{
    readonly StackPanel adjusterOffer = new()
    {
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 4, 0, 12),
    };
    readonly StackPanel rows = new();
    readonly TextBlock status = InstallUi.Text("");
    readonly Dictionary<string, InstallQueueRecord> activeJobs = [];
    readonly System.Windows.Threading.DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Dictionary<string, InstallationRequest> requests = [];
    readonly Dictionary<string, InstallInspection> inspections = [];
    readonly SemaphoreSlim serial = new(1, 1);
    IReadOnlyList<GameInstall> games = [];
    GameInstall? current;
    public Base.BaseSetupCard BaseCard { get; } = new();
    public Func<string[], Task<(int code, string output)>>? RunGmm { get; set; }
    public event Action? Changed;
    public event Action<GameInstall>? GameSelected;
    public event Action<bool, string>? BusyChanged;
    public event Action<string>? CancelDownloadRequested;
    public event Action<Uri, string>? OpenRequested;

    /// <summary>A queued job was installed (for the Installed status in Recommendations).</summary>
    public event Action<GameInstall, InstallQueueRecord>? JobCompleted;

    public InstallationPage()
    {
        var p = new DockPanel { Margin = new Thickness(24) };
        Content = p;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        p.Children.Add(top);
        top.Children.Add(InstallUi.Text(L.T("ui.common.modInstallation"), 22));
        var buttons = new WrapPanel();
        top.Children.Add(buttons);
        buttons.Children.Add(InstallUi.Button(L.T("ui.installation.addFiles"), () =>
            {
                var d = new OpenFileDialog
                {
                    Multiselect = true,
                    Filter = L.Join(
                        "|",
                        L.T("filter.installation.modsSettings"),
                        "*.zip;*.rar;*.7z;*.oiv;*.asi;*.xml;*.ini;*.json;*.rpf"
                    ),
                };
                if (d.ShowDialog(Window.GetWindow(this)) == true)
                    _ = AddAsync(d.FileNames);
            }));
        buttons.Children.Add(InstallUi.Button("GameConfig", () => OpenRpf(@"common\data\gameconfig.xml")));
        buttons.Children.Add(InstallUi.Button("DLC List", () => OpenRpf(@"common\data\dlclist.xml")));
        buttons.Children.Add(InstallUi.Button(L.T("ui.common.openRpf"), PickRpf));
        top.Children.Add(BaseCard);
        BaseCard.BusyChanged += (busy, title) => BusyChanged?.Invoke(busy, title);
        BaseCard.OpenRequested += (uri, root) => OpenRequested?.Invoke(uri, root);
        status.Visibility = Visibility.Collapsed;
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            SetStatus("");
        };
        top.Children.Add(status);
        top.Children.Add(adjusterOffer);
        p.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void Refresh(IReadOnlyList<GameInstall> available, GameInstall? selected)
    {
        games = available;
        current = selected;
        BaseCard.Refresh(selected);
        Render();
    }

    Window Owner => Window.GetWindow(this);

    public void OpenGameConfig() => OpenRpf(@"common\data\gameconfig.xml");

    public void OpenDlcList() => OpenRpf(@"common\data\dlclist.xml");

    void OpenRpf(string? inner = null)
    {
        if (current is null)
            return;
        var w = new RpfBrowserWindow(Owner, current, new RpfWorkspace(current).WorkingUpdate, RunGmm!, inner);
        w.Changed += () => Changed?.Invoke();
        w.ShowDialog();
    }

    public void PickRpf()
    {
        if (current is null)
            return;
        var d = new OpenFileDialog
        {
            Filter = L.Join("|", L.T("filter.installation.rpfArchive"), "*.rpf"),
            InitialDirectory = current.Root,
        };
        if (d.ShowDialog(Owner) != true)
            return;
        try
        {
            string rel = Path.GetRelativePath(current.Root, d.FileName);
            new RpfWorkspace(current).FullPath(rel);
            var w = new RpfBrowserWindow(Owner, current, rel, RunGmm!);
            w.Changed += () => Changed?.Invoke();
            w.ShowDialog();
        }
        catch (Exception ex)
        {
            InstallUi.Error(Owner, ex);
        }
    }

    public async Task AddAsync(
        IEnumerable<string> files,
        string? fixedRoot = null,
        string? downloadId = null,
        string? sourceUrl = null,
        string? scriptLibraryId = null,
        string? recommendedId = null
    )
    {
        foreach (var file in files)
        {
            if (!File.Exists(file))
                continue;
            GameInstall? game = fixedRoot is null
                ? (games.Count == 1 ? games[0] : current)
                : games.FirstOrDefault(g => g.Root.Equals(fixedRoot, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase) && fixedRoot is null)
                {
                    var kind = InstallDocuments.Kind(InstallDocuments.ReadText(file));
                    if (kind is XmlDocumentKind.DlcList or XmlDocumentKind.GameConfig)
                        game = InstallUi.ChooseGame(
                            Owner,
                            games,
                            current,
                            kind == XmlDocumentKind.DlcList ? "DLC List" : "GameConfig"
                        );
                }
                if (game is null)
                {
                    SetStatus(games.Count == 0 ? L.T("ui.installation.specifyInstalledGtaVVersion") : "");
                    continue;
                }
                if (Path.GetExtension(file).Equals(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    var rel = Path.GetRelativePath(game.Root, file);
                    new RpfWorkspace(game).FullPath(rel);
                    new RpfBrowserWindow(Owner, game, rel, RunGmm!).ShowDialog();
                    continue;
                }
                var job = new InstallQueueRecord
                {
                    GameRoot = game.Root,
                    Source = Path.GetFullPath(file),
                    SourceUrl = sourceUrl ?? "",
                    RecommendedId = recommendedId,
                    ScriptLibraryId = scriptLibraryId,
                    DownloadId = downloadId,
                    Phase = InstallPhase.Analysis,
                };
                await Analyze(job);
            }
            catch (Exception ex)
            {
                InstallUi.Error(Owner, ex);
            }
        }
    }

    public void DownloadChanged(DownloadReceipt receipt, bool failed = false)
    {
        var store = new InstallQueueStore(receipt.GameRoot);
        var job = store.Load().FirstOrDefault(j => j.DownloadId == receipt.Id)
            ?? new()
            {
                GameRoot = receipt.GameRoot,
                Source = receipt.File,
                SourceUrl = receipt.SourceUrl,
                DownloadId = receipt.Id,
            };
        job.Phase = failed ? InstallPhase.Error : InstallPhase.Downloading;
        job.Message = failed ? L.T("ui.common.downloadInterruptedCanOpenAuthor") : L.T("ui.common.loadingBuiltBrowser");
        Save(job);
    }

    public void HandOffDownload(DownloadReceipt receipt)
    {
        var store = new InstallQueueStore(receipt.GameRoot);
        var job = store.Load().FirstOrDefault(j => j.DownloadId == receipt.Id);
        if (job is not null)
        {
            job.Phase = InstallPhase.AwaitingConfirmation;
            job.Message = L.T("ui.common.naturalvisionWizardOpened");
            Save(job, false);
        }
    }

    public async Task DownloadCompletedAsync(DownloadReceipt receipt)
    {
        var store = new InstallQueueStore(receipt.GameRoot);
        var job = store.Load().FirstOrDefault(j => j.DownloadId == receipt.Id)
            ?? new()
            {
                GameRoot = receipt.GameRoot,
                Source = receipt.File,
                SourceUrl = receipt.SourceUrl,
                DownloadId = receipt.Id,
            };
        await Analyze(job);
    }

    async Task Analyze(InstallQueueRecord job)
    {
        bool openPlan = false;
        await serial.WaitAsync();
        try
        {
            status.Text = L.T("ui.installation.checking", Path.GetFileName(job.Source));
            status.Visibility = Visibility.Visible;
            var game = GameInstall.Open(job.GameRoot);
            var request = requests.GetValueOrDefault(job.Id)
                ?? new InstallationRequest
                {
                    Source = job.Source,
                    RecommendedId = job.RecommendedId,
                    ScriptLibraryId = job.ScriptLibraryId,
                    DownloadId = job.DownloadId,
                    SourceUrl = job.SourceUrl,
                    JobId = job.Id,
                };
            request.ExpectedTargets.Clear();
            request.SourceHash = await Task.Run(() => FileTransactionEngine.Hash(job.Source));
            if (
                request.RecommendedId is null
                && request.ScriptLibraryId is null
                && new[] { ".zip", ".rar", ".7z" }.Contains(Path.GetExtension(job.Source).ToLowerInvariant())
                && request.Mappings.Count == 0
            )
            {
                var variants = await Task.Run(() =>
                {
                    using var archive = ModArchive.OpenExpanded(job.Source);
                    return GameConfigVariants.Find(archive);
                });
                if (variants.Count > 0)
                {
                    var choose = new GameConfigWindow(Owner, variants);
                    if (choose.ShowDialog() != true)
                    {
                        Save(job, false);
                        SetStatus("");
                        return;
                    }
                    var target = InstallUi.ChooseGame(Owner, games, game, "GameConfig");
                    if (target is null)
                    {
                        Save(job, false);
                        SetStatus("");
                        return;
                    }
                    if (
                        choose.Selected!.EditionHint is not null
                        && choose.Selected.EditionHint != target.Edition.ToString()
                    )
                        throw new InvalidOperationException(
                            L.T("ui.installation.selectedGameconfigVariantMarkedAnother")
                        );
                    if (
                        job.DownloadId is not null
                        && !target.Root.Equals(job.GameRoot, StringComparison.OrdinalIgnoreCase)
                    )
                        new ManagedDownloads().Retarget(job.DownloadId, job.GameRoot, target.Root);
                    game = target;
                    job.GameRoot = game.Root;
                    current = game;
                    GameSelected?.Invoke(game);
                    request.Mappings[choose.Selected.Source] = @"update\update.rpf|common\data\gameconfig.xml";
                    openPlan = true;
                }
            }
            job.Phase = InstallPhase.Analysis;
            Save(job);
            var inspection = await Task.Run(() =>
                InstallationService.Inspect(game, job.Source, job.ScriptLibraryId, job.RecommendedId)
            );
            requests[job.Id] = request;
            inspections[job.Id] = inspection;
            job.Phase = inspection.Dependencies.Any(d => d.Required && !d.Present)
                ? InstallPhase.Dependencies
                : InstallPhase.AwaitingConfirmation;
            job.Message = L.T(
                "ui.common.bindingsNotesOpenPlanBefore",
                inspection.Targets.Count,
                inspection.Warnings.Count
            );
            Save(job);
            SetStatus("");
        }
        catch (Exception ex)
        {
            job.Phase = InstallPhase.Error;
            job.Message = L.Owned(ex.Message);
            Save(job);
        }
        finally
        {
            serial.Release();
        }
        if (openPlan)
            await Review(job);
    }

    void SetStatus(string message)
    {
        noticeTimer.Stop();
        status.ToolTip = message;
        status.Text = L.Truncate(L.Owned(message), 360);
        status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    void Save(InstallQueueRecord job, bool show = true)
    {
        var displayMessage = job.Message;
        try
        {
            job.Message = L.Source(displayMessage);
            new InstallQueueStore(job.GameRoot).Save(job);
        }
        finally
        {
            job.Message = displayMessage;
        }
        if (!show || job.Phase == InstallPhase.Completed)
        {
            activeJobs.Remove(job.Id);
            requests.Remove(job.Id);
            inspections.Remove(job.Id);
        }
        else
        {
            foreach (
                var old in activeJobs
                    .Values.Where(j =>
                        j.Id != job.Id
                        && j.GameRoot.Equals(job.GameRoot, StringComparison.OrdinalIgnoreCase)
                        && j.Source.Equals(job.Source, StringComparison.OrdinalIgnoreCase)
                        && j.Phase is not (InstallPhase.Installing or InstallPhase.Verifying)
                    )
                    .ToArray()
            )
            {
                activeJobs.Remove(old.Id);
                requests.Remove(old.Id);
                inspections.Remove(old.Id);
            }
            activeJobs[job.Id] = job;
        }
        Render();
    }

    static string Phase(InstallPhase p) =>
        p switch
        {
            InstallPhase.Downloading => L.T("ui.installation.loading"),
            InstallPhase.Analysis => L.T("ui.installation.analysis"),
            InstallPhase.AwaitingConfirmation => L.T("ui.installation.awaitingConfirmation"),
            InstallPhase.Dependencies => L.T("ui.installation.checkingComponents"),
            InstallPhase.Installing => L.T("ui.installation.installation"),
            InstallPhase.Verifying => L.T("ui.installation.verifyingResults"),
            InstallPhase.Completed => L.T("ui.common.installed"),
            _ => L.T("ui.installation.needsAttention"),
        };

    void Render()
    {
        rows.Children.Clear();
        if (current is null)
            return;
        try
        {
            foreach (
                var job in activeJobs
                    .Values.Where(j => j.GameRoot.Equals(current.Root, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(j => j.Updated)
                    .Take(60)
            )
            {
                var p = new StackPanel { Margin = new Thickness(12) };
                var border = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("Card"),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = p,
                };
                rows.Children.Add(border);
                p.Children.Add(
                    InstallUi.Text(Path.GetFileName(job.Source) + " · " + GameInstall.Open(job.GameRoot).Edition, 15)
                );
                p.Children.Add(
                    new Expander
                    {
                        Header = L.T("ui.installation.sourceFileGame"),
                        Content = InstallUi.Text(job.Source + "\n" + job.GameRoot),
                    }
                );
                p.Children.Add(
                    InstallUi.Text(L.Join("", Phase(job.Phase), " · ", L.Truncate(L.Owned(job.Message), 320)))
                );
                if (job.Message.Length > 320)
                    p.Children.Add(
                        new Expander
                        {
                            Header = L.T("ui.installation.details"),
                            Content = new TextBox
                            {
                                Text = L.Owned(job.Message),
                                IsReadOnly = true,
                                TextWrapping = TextWrapping.Wrap,
                                MaxHeight = 160,
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            },
                        }
                    );
                var actions = new WrapPanel();
                p.Children.Add(actions);
                if (job.Phase == InstallPhase.Downloading)
                    actions.Children.Add(
                        InstallUi.Button(L.T("ui.installation.cancelDownload"), () =>
                            CancelDownloadRequested?.Invoke(job.DownloadId ?? "")
                        )
                    );
                if (
                    job.Phase
                    is not (
                        InstallPhase.Completed
                        or InstallPhase.Downloading
                        or InstallPhase.Analysis
                        or InstallPhase.Installing
                        or InstallPhase.Verifying
                    )
                )
                {
                    actions.Children.Add(
                        InstallUi.Button(
                            requests.ContainsKey(job.Id)
                                ? L.T("ui.installation.planInstallation")
                                : L.T("ui.installation.analyzeAgain"),
                            () => _ = Review(job)
                        )
                    );
                    if (requests.ContainsKey(job.Id))
                        actions.Children.Add(
                            InstallUi.Button(L.T("ui.installation.analyzeAgain"), () => _ = Analyze(job))
                        );
                    actions.Children.Add(
                        InstallUi.Button(L.T("ui.installation.components"), () => _ = Dependencies(job))
                    );
                    if (job.RecommendedId is null && job.ScriptLibraryId is null)
                        actions.Children.Add(
                            InstallUi.Button(L.T("ui.installation.assignFilesManually"), () => _ = Manual(job))
                        );
                    actions.Children.Add(InstallUi.Button(L.T("ui.installation.remove"), () =>
                        {
                            Save(job, false);
                            SetStatus("");
                        }));
                }
                if (!string.IsNullOrWhiteSpace(job.SourceUrl))
                    actions.Children.Add(InstallUi.Button(L.T("ui.installation.authorSPage"), () =>
                        {
                            if (Uri.TryCreate(job.SourceUrl, UriKind.Absolute, out var uri))
                                OpenRequested?.Invoke(uri, job.GameRoot);
                        }));
            }
        }
        catch (Exception ex)
        {
            SetStatus(L.Owned(ex.Message));
        }
    }

    async Task Manual(InstallQueueRecord job)
    {
        if (job.RecommendedId is not null || job.ScriptLibraryId is not null)
            return;
        try
        {
            if (!requests.ContainsKey(job.Id))
                await Analyze(job);
            if (!requests.TryGetValue(job.Id, out var r))
                return;
            var w = new ManualMappingWindow(Owner, job.Source);
            if (w.ShowDialog() == true)
            {
                r.Mappings = w.Mappings;
                job.Phase = InstallPhase.AwaitingConfirmation;
                job.Message = L.T("ui.common.destinationsWereSelectedManuallyCheck");
                Save(job);
            }
        }
        catch (Exception ex)
        {
            InstallUi.Error(Owner, ex);
        }
    }

    async Task Review(InstallQueueRecord job, bool confirmed = false)
    {
        if (!requests.ContainsKey(job.Id))
        {
            await Analyze(job);
            return;
        }
        if (
            !requests.TryGetValue(job.Id, out var request)
            || !inspections.TryGetValue(job.Id, out var inspection)
            || RunGmm is null
        )
            return;
        var game = GameInstall.Open(job.GameRoot);
        try
        {
            var settings = AppSettings.Load();
            if (settings.OpenLibrary() is null)
                throw new InvalidOperationException(
                    L.T("ui.installation.installationVerificationRollbackSelectBackup")
                );
            if (!confirmed)
            {
                var dialog = new InstallPlanWindow(Owner, game, request, inspection);
                if (dialog.ShowDialog() != true || !dialog.Accepted)
                    return;
            }
            bool installedConfig =
                request.Mappings.Values.Any(t => t.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
                || Path.GetExtension(request.Source).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                    && InstallDocuments.Kind(InstallDocuments.ReadText(request.Source)) == XmlDocumentKind.GameConfig;
            request.KeepArchive = settings.KeepDownloadedArchives;
            request.ArchiveLibrary = settings.ArchiveLibrary;
            await serial.WaitAsync();
            string path = Path.Combine(
                Path.GetTempPath(),
                "ModHub",
                "request-" + Guid.NewGuid().ToString("N") + ".json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                BusyChanged?.Invoke(true, L.T("ui.installation.installingCheckingFiles"));
                job.Phase = InstallPhase.Installing;
                job.Message = L.T("ui.common.applyingConfirmedPlanVerifyingFiles");
                Save(job);
                File.WriteAllText(path, JsonSerializer.Serialize(request));
                var (code, output) = await RunGmm(["pipeline-install", game.Root, path]);
                var line = output.Split('\n').LastOrDefault(l => l.StartsWith("GMM_PIPELINE_RESULT:"));
                var result = line is null
                    ? null
                    : JsonSerializer.Deserialize<TxResult>(line["GMM_PIPELINE_RESULT:".Length..]);
                job.Phase =
                    code == 0 && result?.State == TxState.Completed ? InstallPhase.Completed : InstallPhase.Error;
                job.Message = result?.Message ?? output.Trim();
                job.TransactionId = result?.Id;
                Save(job);
                Changed?.Invoke();
                if (job.Phase == InstallPhase.Completed)
                {
                    JobCompleted?.Invoke(game, job);
                    SetStatus(L.T("ui.installation.installedGtaV", Path.GetFileName(job.Source), game.Edition));
                    noticeTimer.Start();
                    if (Core.Discover.AuthorThanks.IsGta5Mods(job.SourceUrl))
                        new Core.Discover.AuthorThanks().Remember(
                            job.SourceUrl,
                            ModNames.Pretty(Path.GetFileNameWithoutExtension(job.Source)),
                            game.Root
                        );
                    if (installedConfig)
                        ShowAdjusterOffer(game);
                }
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                serial.Release();
                BusyChanged?.Invoke(false, "");
            }
        }
        catch (Exception ex)
        {
            job.Phase = InstallPhase.Error;
            job.Message = L.Owned(ex.Message);
            Save(job);
        }
    }

    void ShowAdjusterOffer(GameInstall game)
    {
        var missing = AdjusterComponents.Missing(game);
        adjusterOffer.Children.Clear();
        if (missing.Count == 0)
        {
            adjusterOffer.Visibility = Visibility.Collapsed;
            return;
        }
        adjusterOffer.Visibility = Visibility.Visible;
        adjusterOffer.IsEnabled = true;
        adjusterOffer.Children.Add(
            InstallUi.Text(
                L.T(
                    "ui.installation.gameconfigInstalledGtaVRecommended",
                    game.Edition,
                    L.Join(", ", missing.Select(c => c.Title))
                )
            )
        );
        var buttons = new WrapPanel();
        adjusterOffer.Children.Add(buttons);
        buttons.Children.Add(
            InstallUi.Button(L.T("ui.installation.installRequiredComponents"), () => _ = InstallAdjusters(game))
        );
        buttons.Children.Add(InstallUi.Button(L.T("ui.installation.notNow"), () =>
            {
                adjusterOffer.Children.Clear();
                adjusterOffer.Visibility = Visibility.Collapsed;
                SetStatus("");
            }));
    }

    async Task InstallAdjusters(GameInstall game)
    {
        adjusterOffer.IsEnabled = false;
        string directory = Path.Combine(Path.GetTempPath(), "ModHub", "adjusters-" + Guid.NewGuid().ToString("N"));
        var downloads = new Dictionary<string, string>();
        string? manifest = null;
        bool installed = false;
        try
        {
            if (game.IsRunning())
                throw new InvalidOperationException(L.T("ui.common.closeGtaVBeforeInstalling"));
            using var http = BaseDownloads.CreateClient();
            foreach (var component in AdjusterComponents.Missing(game))
            {
                SetStatus(L.T("ui.installation.downloadingGtaV", component.Title, game.Edition));
                downloads[component.Id] = await BaseDownloads.DownloadAdjusterAsync(
                    http,
                    component.Id,
                    directory,
                    CancellationToken.None
                );
            }
            if (downloads.Count == 0)
            {
                adjusterOffer.Visibility = Visibility.Collapsed;
                SetStatus(L.T("ui.installation.requiredComponentsAlreadyInstalled"));
                noticeTimer.Start();
                return;
            }
            manifest = Path.Combine(directory, "install.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(downloads));
            BusyChanged?.Invoke(true, L.T("ui.installation.installingGameconfigComponents"));
            var result = await RunGmm!(["adjusters-install", game.Root, manifest]);
            if (result.code != 0)
                throw new InvalidOperationException(result.output.Trim());
            installed = true;
            Changed?.Invoke();
            var settings = AppSettings.Load();
            foreach (var file in downloads.Values)
            {
                if (settings.KeepDownloadedArchives && settings.ArchiveLibrary is not null)
                {
                    string hash = FileTransactionEngine.Hash(file);
                    Directory.CreateDirectory(settings.ArchiveLibrary);
                    string target = Path.Combine(settings.ArchiveLibrary, hash + "-" + Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Copy(file, target);
                    if (FileTransactionEngine.Hash(target) != hash)
                        throw new IOException(L.T("ui.installation.couldNotVerifySavedArchive"));
                    File.Delete(file);
                }
                else if (!settings.KeepDownloadedArchives)
                    File.Delete(file);
            }
            adjusterOffer.Visibility = Visibility.Collapsed;
            adjusterOffer.Children.Clear();
            SetStatus(L.T("ui.installation.gameconfigComponentsInstalledGtaV", game.Edition));
            noticeTimer.Start();
        }
        catch (Exception ex)
        {
            SetStatus(
                installed
                    ? L.T("ui.installation.componentsInstalledDownloadedArchivesSaved", L.Owned(ex.Message))
                    : L.T("ui.installation.gameconfigRemainsInstalledComponentsNot", L.Owned(ex.Message))
            );
            adjusterOffer.IsEnabled = true;
            if (installed)
                adjusterOffer.Visibility = Visibility.Collapsed;
        }
        finally
        {
            if (manifest is not null && File.Exists(manifest))
                File.Delete(manifest);
            BusyChanged?.Invoke(false, "");
        }
    }

    async Task Dependencies(InstallQueueRecord job)
    {
        try
        {
            var game = GameInstall.Open(job.GameRoot);
            var inspection = await Task.Run(() =>
                InstallationService.Inspect(game, job.Source, job.ScriptLibraryId, job.RecommendedId)
            );
            var window = InstallUi.Window(Owner, L.T("ui.installation.components2", game.Edition));
            var panel = new StackPanel { Margin = new Thickness(24) };
            window.Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(InstallUi.Text(L.T("ui.installation.requirementsBasedPackageContentsReadme"), 20));
            foreach (var dep in inspection.Dependencies)
            {
                panel.Children.Add(
                    InstallUi.Text(
                        L.T(
                            "ui.installation.text",
                            (
                                dep.Present ? L.T("ui.installation.found2")
                                : dep.Required ? L.T("ui.installation.required2")
                                : L.T("ui.installation.recommended")
                            ) + dep.Title,
                            dep.Evidence
                        )
                    )
                );
                panel.Children.Add(InstallUi.Button(L.T("ui.installation.source", dep.Title), () =>
                    {
                        OpenRequested?.Invoke(new Uri(dep.Url), game.Root);
                        window.Close();
                    }));
            }
            panel.Children.Add(InstallUi.Text(L.T("ui.installation.onlyRequiredScriptHookV")));
            Button auto = null!;
            auto = InstallUi.Button(
                job.RecommendedId is null
                    ? L.T("ui.installation.installAllRequiredAvailableComponents")
                    : Core.Localization.L.T("recommended.auto"),
                () => _ = AutoBase()
            );
            panel.Children.Add(auto);
            window.ShowDialog();
            async Task AutoBase()
            {
                auto.IsEnabled = false;
                string folder = Path.Combine(
                    Path.GetTempPath(),
                    "ModHub",
                    "dependencies-" + Guid.NewGuid().ToString("N")
                );
                try
                {
                    var fresh = InstallationService.Inspect(game, job.Source, job.ScriptLibraryId, job.RecommendedId);
                    var missing = fresh.Dependencies.Where(d => d.Required && !d.Present).Select(d => d.Id).ToHashSet();
                    if (job.RecommendedId is { } recommendation)
                        RecommendedMods.CheckAutomaticDependencies(game, recommendation, fresh.Dependencies);
                    var libraries = ScriptLibraries.Catalog.Where(c => missing.Contains(c.Id)).ToArray();
                    var args = new List<string> { "base-install", game.Root };
                    var downloadedZips = new List<string>();
                    if (missing.Contains("shv") || missing.Contains("asi"))
                    {
                        string? version = ModdingBase.GameVersion(game);
                        if (string.IsNullOrEmpty(version))
                            throw new InvalidOperationException(L.T("ui.installation.unknownGameBuildOpenScript"));
                        using var http = BaseDownloads.CreateClient();
                        var zip = await BaseDownloads.DownloadScriptHookAsync(http, folder, CancellationToken.None);
                        if (!ModdingBase.ScriptHookSupports(zip, version))
                            throw new InvalidOperationException(L.T("ui.installation.currentScriptHookVDoes", version));
                        args.AddRange(["--shv", zip]);
                        downloadedZips.Add(zip);
                    }
                    if (missing.Contains("shvdn"))
                    {
                        if (ModdingBase.Inspect(game).Shvdn)
                            throw new InvalidOperationException(Core.Localization.L.T("libraries.repair"));
                        var variant =
                            game.Edition == GameEdition.Enhanced ? ShvdnVariant.Enhanced : ShvdnVariant.Nightly;
                        using var http = BaseDownloads.CreateClient();
                        var zip = await BaseDownloads.DownloadShvdnAsync(http, variant, folder, CancellationToken.None);
                        args.AddRange(["--shvdn", variant == ShvdnVariant.Enhanced ? "enhanced" : "nightly", zip]);
                        downloadedZips.Add(zip);
                    }
                    if (missing.Contains("mods"))
                    {
                        args.AddRange(["--rageopenv", await Base.BaseSetupCard.DownloadRageOpenVAsync(folder)]);
                    }
                    if (args.Count == 2 && libraries.Length == 0 && job.RecommendedId is null)
                    {
                        MessageBox.Show(window, L.T("ui.installation.remainingComponentsInstalledFromTheir"));
                        return;
                    }
                    InstallationRequest? approved = null;
                    if (job.RecommendedId is not null)
                    {
                        if (!requests.ContainsKey(job.Id))
                            await Analyze(job);
                        if (!requests.TryGetValue(job.Id, out approved))
                            return;
                        var review = new InstallPlanWindow(window, game, approved, fresh);
                        if (review.ShowDialog() != true || !review.Accepted)
                            return;
                    }
                    else if (
                        MessageBox.Show(
                            window,
                            L.T("ui.installation.installListedBaseComponentsGta", game.Edition),
                            L.T("ui.installation.components"),
                            MessageBoxButton.YesNo
                        ) != MessageBoxResult.Yes
                    )
                        return;
                    BusyChanged?.Invoke(true, Core.Localization.L.T("recommended.auto"));
                    if (args.Count > 2)
                    {
                        var result = await RunGmm!(args.ToArray());
                        if (result.code != 0)
                            throw new InvalidOperationException(result.output);
                        if (!AppSettings.Load().KeepDownloadedArchives)
                            foreach (var zip in downloadedZips)
                                File.Delete(zip);
                    }
                    if (job.RecommendedId is not null)
                    {
                        var requiredApi = InstallationService
                            .Inspect(game, job.Source, recommendedId: job.RecommendedId)
                            .Dependencies.FirstOrDefault(d => d.Id == "shvdn" && !d.Present);
                        if (requiredApi is not null)
                            throw new InvalidOperationException(
                                Core.Localization.L.T("recommended.manualDependency", requiredApi.Title)
                            );
                    }
                    foreach (var library in libraries)
                    {
                        if (ScriptLibraries.Present(game, library.Id))
                            continue;
                        if (!library.Supports(game.Edition) || File.Exists(Path.Combine(game.Root, library.Target)))
                            throw new InvalidOperationException(
                                Core.Localization.L.T("recommended.manualDependency", library.Title)
                            );
                        using var http = BaseDownloads.CreateClient();
                        var downloads = new ManagedDownloads();
                        var receipt = downloads.Begin(
                            library.Asset,
                            $"https://github.com/{library.Repository}/releases/tag/{library.Tag}",
                            game.Root
                        );
                        await BaseDownloads.DownloadScriptLibraryAsync(
                            http,
                            library.Id,
                            Path.GetDirectoryName(receipt.File)!,
                            CancellationToken.None
                        );
                        receipt = downloads.Complete(receipt.Id);
                        var settings = AppSettings.Load();
                        var request = new InstallationRequest
                        {
                            Source = receipt.File,
                            DownloadId = receipt.Id,
                            ScriptLibraryId = library.Id,
                            KeepArchive = settings.KeepDownloadedArchives,
                            ArchiveLibrary = settings.ArchiveLibrary,
                        };
                        Directory.CreateDirectory(folder);
                        string manifest = Path.Combine(folder, library.Id + ".json");
                        try
                        {
                            File.WriteAllText(manifest, JsonSerializer.Serialize(request));
                            var result = await RunGmm!(["pipeline-install", game.Root, manifest]);
                            if (result.code != 0)
                                throw new InvalidOperationException(result.output);
                        }
                        finally
                        {
                            if (File.Exists(manifest))
                                File.Delete(manifest);
                        }
                    }
                    Changed?.Invoke();
                    window.Close();
                    if (approved is not null)
                    {
                        var check = InstallationService.Inspect(
                            game,
                            job.Source,
                            job.ScriptLibraryId,
                            job.RecommendedId
                        );
                        var remaining = check
                            .Dependencies.Where(d =>
                                d.Required
                                && !d.Present
                                && !RecommendedMods.ProvidesBundledDependency(job.RecommendedId!, d.Id)
                            )
                            .ToArray();
                        if (remaining.Length > 0)
                            throw new InvalidOperationException(
                                Core.Localization.L.T(
                                    "core.installation.requiredComponentsNotInstalledInstall",
                                    string.Join(", ", remaining.Select(d => d.Title))
                                )
                            );
                        requests[job.Id] = approved;
                        inspections[job.Id] = check;
                        await Review(job, true);
                    }
                    else
                    {
                        MessageBox.Show(
                            Owner,
                            Core.Localization.L.T("ui.installation.baseComponentsInstalledAnalyzePackage")
                        );
                        await Analyze(job);
                    }
                }
                catch (Exception ex)
                {
                    InstallUi.Error(Owner, ex);
                }
                finally
                {
                    auto.IsEnabled = true;
                    BusyChanged?.Invoke(false, "");
                }
            }
        }
        catch (Exception ex)
        {
            InstallUi.Error(Owner, ex);
        }
    }
}
