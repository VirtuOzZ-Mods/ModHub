using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Installation;

/// <summary>Installs and undoes queue items; all of them share one elevated process (one UAC prompt).</summary>
internal interface IBatchRunner
{
    /// <summary>Fatal: the runner cannot go on at all (UAC declined, the worker ended): the queue stops.</summary>
    Task<(bool Ok, string Message, bool Fatal)> InstallAsync(BatchItem item);

    Task<(bool Ok, string Message)> UndoAsync(BatchItem item);
}

/// <summary>
/// Several dropped mods installed one after another, the prevailing kind first. By default each successful
/// installation asks before the next one; "Install all in a row" goes on without asking. Mods that need a choice
/// (a variant, a gender) are skipped and can be opened separately; nothing is guessed for them.
/// </summary>
internal sealed class BatchInstallWindow
{
    private enum State
    {
        Waiting,
        Installing,
        Done,
        Failed,
        Skipped,
        Cancelled,
    }

    private sealed class Row
    {
        public required BatchItem Item { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Number { get; init; }
        public required Button Remove { get; init; }
        public Border? Card { get; set; }
        public State State { get; set; }

        /// <summary>Not installed yet: it can still leave the queue.</summary>
        public bool Pending => State is State.Waiting or State.Skipped;
    }

    private readonly StackPanel _list = new();

    private readonly Window _window;
    private readonly List<Row> _rows = [];
    private readonly IBatchRunner _runner;
    private bool _cancel;
    private readonly CheckBox _auto = new()
    {
        Content = L.T("ui.batch.installAll"),
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly Button _start;
    private readonly Button _close;
    private bool _running;

    /// <summary>A mod the user wants to handle on its own (it needs a choice): opened after this window closes.</summary>
    public BatchItem? OpenSeparately { get; private set; }

    public int Installed => _rows.Count(r => r.State == State.Done);

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public BatchInstallWindow(Window owner, BatchPlan plan, IBatchRunner runner)
    {
        _runner = runner;
        _window = InstallUi.Window(owner, L.T("ui.batch.title"), 760, 640);
        var root = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.batch.header", plan.Items.Count, DropBatch.Label(plan.Main), plan.MainCount),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        top.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.batch.hint"),
                Foreground = Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 10),
            }
        );

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        bottom.Children.Add(_auto);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _start = new Button
        {
            Content = L.T("ui.batch.start"),
            Style = (Style)Application.Current.FindResource("Primary"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 4, 8, 4),
        };
        _start.Click += async (_, _) => await RunAsync();
        // Closes the window between mods; while a mod installs it cancels that installation.
        _close = InstallUi.Button(L.T("ui.common.cancel"), CloseOrCancel);
        buttons.Children.Add(_start);
        buttons.Children.Add(_close);
        bottom.Children.Add(buttons);

        int n = 0;
        foreach (var item in plan.Items)
            _list.Children.Add(RowFor(++n, item, plan.Main));
        root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // Closing mid-installation would leave the queue half-done with no report: wait for the current mod.
        _window.Closing += (_, e) =>
        {
            if (_running)
                e.Cancel = true;
        };
        _window.Content = root;
    }

    public void ShowDialog() => _window.ShowDialog();

    private Border RowFor(int number, BatchItem item, BatchKind main)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var numberText = new TextBlock
        {
            Text = number + ".",
            Foreground = Res("Muted"),
            FontWeight = FontWeights.SemiBold,
        };
        grid.Children.Add(numberText);
        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        body.Children.Add(
            new TextBlock
            {
                Text = item.Title,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        body.Children.Add(
            new TextBlock
            {
                Text = L.Owned(item.Summary),
                Foreground = Res("Muted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        if (item.Moved)
            body.Children.Add(
                new TextBlock
                {
                    Text = L.T("ui.batch.movedAfter", DropBatch.Label(item.Kind), DropBatch.Label(main)),
                    Foreground = Res("Warn"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                }
            );
        var status = new TextBlock
        {
            Text = L.T("ui.batch.waiting"),
            Foreground = Res("Muted"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var remove = InstallUi.Button(L.T("ui.batch.remove"), () => { });
        var row = new Row
        {
            Item = item,
            Status = status,
            Number = numberText,
            Remove = remove,
        };
        remove.Click += (_, _) => RemoveRow(row);
        if (item.Problem is { } problem)
        {
            body.Children.Add(
                new TextBlock
                {
                    Text = L.T("ui.batch.needsAttention", L.Owned(problem)),
                    Foreground = Res("Warn"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                }
            );
            var open = InstallUi.Button(L.T("ui.batch.openSeparately"), () =>
            {
                if (_running)
                    return;
                OpenSeparately = item;
                _window.Close();
            });
            actions.Children.Add(open);
            row.State = State.Skipped;
            status.Text = L.T("ui.batch.willSkip");
        }
        actions.Children.Add(remove);
        body.Children.Add(actions);
        _rows.Add(row);
        return row.Card = new Border
        {
            Style = (Style)Application.Current.FindResource("CardBorder"),
            Margin = new Thickness(0, 0, 6, 8),
            Child = grid,
        };
    }

    /// <summary>Takes a mod that is not installed yet out of the queue (a pack dropped by mistake).</summary>
    private void RemoveRow(Row row)
    {
        if (_running || !row.Pending)
            return;
        _rows.Remove(row);
        _list.Children.Remove(row.Card);
        for (int i = 0; i < _rows.Count; i++)
            _rows[i].Number.Text = i + 1 + ".";
        _start.IsEnabled = _rows.Any(r => r.State == State.Waiting);
    }

    /// <summary>While a mod installs, the queue is fixed; afterwards only mods not installed yet can be removed.</summary>
    private void UpdateRemoveButtons()
    {
        foreach (var r in _rows)
            r.Remove.IsEnabled = !_running && r.Pending;
    }

    private void CloseOrCancel()
    {
        if (!_running)
        {
            _window.Close();
            return;
        }
        if (_cancel)
            return;
        // A half-written installation would leave the pack and dlclist.xml out of step, so the current mod
        // finishes as one transaction and is then undone right away; the queue stops after that.
        _cancel = true;
        _close.IsEnabled = false;
        if (_rows.FirstOrDefault(r => r.State == State.Installing) is { } current)
            current.Status.Text = L.T("ui.batch.cancelling");
    }

    private async Task RunAsync()
    {
        if (_running)
            return;
        _running = true;
        _cancel = false;
        _start.IsEnabled = false;
        _close.Content = L.T("ui.batch.cancelInstall");
        UpdateRemoveButtons();
        bool stopped = false;
        try
        {
            while (_rows.FirstOrDefault(r => r.State == State.Waiting) is { } row)
            {
                row.State = State.Installing;
                row.Status.Text = L.T("ui.batch.installing");
                row.Status.Foreground = Res("Text");
                var (ok, message, fatal) = await _runner.InstallAsync(row.Item);
                if (_cancel)
                {
                    if (ok)
                    {
                        row.Status.Text = L.T("ui.batch.undoing");
                        (ok, message) = await _runner.UndoAsync(row.Item);
                        row.State = ok ? State.Cancelled : State.Done;
                        row.Status.Text = ok ? L.T("ui.batch.cancelled") : L.T("ui.batch.undoFailed");
                        row.Status.Foreground = Res(ok ? "Muted" : "Bad");
                        row.Status.ToolTip = L.Owned(message);
                        if (!ok)
                            MessageBox.Show(
                                _window,
                                L.T("ui.batch.undoFailedDetails", row.Item.Title, L.Owned(message)),
                                L.T("ui.batch.title"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning
                            );
                    }
                    else
                    {
                        // Nothing was written: the failed installation already rolled itself back.
                        row.State = State.Cancelled;
                        row.Status.Text = L.T("ui.batch.cancelled");
                        row.Status.Foreground = Res("Muted");
                    }
                    stopped = true;
                    return;
                }
                row.State = ok ? State.Done : State.Failed;
                row.Status.Text = ok ? L.T("ui.batch.done") : L.T("ui.batch.failed");
                row.Status.Foreground = Res(ok ? "Ok" : "Bad");
                row.Status.ToolTip = L.Owned(message);
                if (fatal)
                {
                    MessageBox.Show(
                        _window,
                        L.Owned(message),
                        L.T("ui.batch.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    stopped = true;
                    return;
                }
                var next = _rows.FirstOrDefault(r => r.State == State.Waiting);
                if (next is null)
                    break;
                if (!ok)
                {
                    // An error always asks: the next mods may depend on the failed one.
                    if (
                        MessageBox.Show(
                            _window,
                            L.T("ui.batch.failedContinue", row.Item.Title, L.Owned(message), next.Item.Title),
                            L.T("ui.batch.title"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning
                        ) != MessageBoxResult.Yes
                    )
                        return;
                }
                else if (
                    _auto.IsChecked != true
                    && MessageBox.Show(
                        _window,
                        L.T("ui.batch.installedContinue", row.Item.Title, next.Item.Title),
                        L.T("ui.batch.title"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    ) != MessageBoxResult.Yes
                )
                    return;
            }
            int failed = _rows.Count(r => r.State == State.Failed);
            int skipped = _rows.Count(r => r.State == State.Skipped);
            MessageBox.Show(
                _window,
                L.T("ui.batch.finished", Installed, _rows.Count, skipped, failed),
                L.T("ui.batch.title"),
                MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information
            );
        }
        finally
        {
            _running = false;
            _cancel = false;
            UpdateRemoveButtons();
            _close.IsEnabled = true;
            _close.Content = L.T("ui.common.close");
            if (stopped)
                _start.Content = L.T("ui.batch.continue");
            bool more = _rows.Any(r => r.State == State.Waiting);
            _start.IsEnabled = more;
            _start.Content = more ? L.T("ui.batch.continue") : L.T("ui.batch.start");
        }
    }
}
