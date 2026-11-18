using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;
using Microsoft.Win32;

namespace GTAModManager.App.Installation;

internal sealed class RpfBrowserWindow : Window
{
    readonly GameInstall game;
    readonly Func<string[], Task<(int code, string output)>> run;
    readonly RpfWorkspace workspace;
    readonly TreeView tree = new()
    {
        Background = System.Windows.Media.Brushes.Black,
        Foreground = System.Windows.Media.Brushes.White,
    };
    readonly TextBox xml = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    readonly DataGrid visual = new() { AutoGenerateColumns = true, CanUserAddRows = false };
    readonly TextBlock status = InstallUi.Text("");
    readonly TextBox search = new() { Margin = new Thickness(0, 4, 0, 8) };
    string archive,
        nested = "",
        hash = "",
        selected = "";
    IReadOnlyList<RpfEntryInfo> entries = [];
    List<XmlField> fields = [];
    XDocument? document;
    bool busy;
    public event Action? Changed;

    public sealed class XmlField
    {
        public string Path { get; set; } = "";
        public string Value { get; set; } = "";
        internal Action<string> Apply { get; set; } = _ => { };
    }

    public RpfBrowserWindow(
        Window owner,
        GameInstall game,
        string archive,
        Func<string[], Task<(int code, string output)>> run,
        string? initial = null
    )
    {
        this.game = game;
        this.archive = archive;
        this.run = run;
        workspace = new(game);
        Owner = owner;
        Title = L.T("ui.installation.rpf", game.Edition);
        Width = 1120;
        Height = 780;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
        Foreground = System.Windows.Media.Brushes.White;
        Resources.MergedDictionaries.Add(
            (ResourceDictionary)
                System.Windows.Markup.XamlReader.Parse(
                    """
                    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                     <Style TargetType="DataGrid"><Setter Property="Background" Value="#111111"/><Setter Property="Foreground" Value="White"/><Setter Property="RowBackground" Value="#181818"/><Setter Property="AlternatingRowBackground" Value="#222222"/><Setter Property="GridLinesVisibility" Value="Horizontal"/><Setter Property="HorizontalGridLinesBrush" Value="#333333"/></Style>
                     <Style TargetType="DataGridColumnHeader"><Setter Property="Background" Value="#303030"/><Setter Property="Foreground" Value="White"/><Setter Property="Padding" Value="8"/></Style>
                     <Style TargetType="DataGridRow"><Setter Property="Foreground" Value="White"/></Style>
                     <Style TargetType="DataGridCell"><Setter Property="Foreground" Value="White"/></Style>
                     <Style TargetType="TabControl"><Setter Property="Background" Value="#111111"/><Setter Property="BorderBrush" Value="#333333"/></Style>
                     <Style TargetType="TabItem"><Setter Property="Foreground" Value="White"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border x:Name="B" Background="#181818" Padding="12,8" BorderBrush="#333333" BorderThickness="1"><ContentPresenter ContentSource="Header"/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="B" Property="Background" Value="#444444"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
                    </ResourceDictionary>
                    """
                )
        );
        var root = new DockPanel { Margin = new Thickness(20) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(status);
        var actions = new WrapPanel();
        top.Children.Add(actions);
        actions.Children.Add(InstallUi.Button(L.T("ui.common.refresh"), () => _ = Load()));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.copyMods2"), () => _ = Copy()));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.archiveRoot"), () =>
            {
                nested = "";
                _ = Load();
            }));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.openNestedRpf"), () =>
            {
                if (selected.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    nested = selected;
                    _ = Load();
                }
            }));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.export"), () => _ = Export()));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.importReplace"), () => _ = Import()));
        actions.Children.Add(InstallUi.Button(L.T("ui.common.remove"), () => _ = Delete()));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.saveXml"), () => _ = SaveXml(false)));
        actions.Children.Add(InstallUi.Button(L.T("ui.installation.saveTable"), () => _ = SaveXml(true)));
        top.Children.Add(InstallUi.Text(L.T("ui.installation.originalArchivesAvailableReadingChanges")));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(320) });
        grid.ColumnDefinitions.Add(new());
        root.Children.Add(grid);
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(left);
        search.ToolTip = L.T("ui.installation.searchByPath");
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(tree);
        search.TextChanged += (_, _) => ShowTree();
        var tabs = new TabControl();
        Grid.SetColumn(tabs, 1);
        grid.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = L.T("ui.installation.xmlText"), Content = xml });
        tabs.Items.Add(new TabItem { Header = L.T("ui.installation.xmlParameters"), Content = visual });
        visual.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName == "Path")
            {
                e.Column.IsReadOnly = true;
                e.Column.Header = L.T("rpf.column.path");
            }
            else if (e.PropertyName == "Value")
                e.Column.Header = L.T("rpf.column.value");
        };
        tree.SelectedItemChanged += async (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem { Tag: RpfEntryInfo entry } && entry.Kind != "Папка")
            {
                selected = string.IsNullOrEmpty(nested) ? entry.Path : nested + "|" + entry.Path;
                await Preview(entry);
            }
        };
        Loaded += async (_, _) =>
        {
            await Load();
            if (initial is not null)
            {
                selected = initial;
                await Preview(new(initial, "Файл", 0));
            }
        };
    }

    async Task Load()
    {
        if (busy)
            return;
        busy = true;
        try
        {
            string path = workspace.FullPath(archive);
            var result = await Task.Run(() =>
            {
                var h = FileTransactionEngine.Hash(path);
                var list = workspace.List(archive, nested);
                if (FileTransactionEngine.Hash(path) != h)
                    throw new IOException(L.T("ui.installation.archiveChangedWhileBeingRead"));
                return (h, list);
            });
            hash = result.h;
            entries = result.list;
            xml.Clear();
            visual.ItemsSource = null;
            selected = "";
            ShowTree();
            status.Text = L.T(
                "ui.common.text",
                archive + (nested.Length > 0 ? " | " + nested : ""),
                (
                    workspace.Editable(archive)
                        ? L.T("ui.installation.copyMods")
                        : L.T("ui.installation.originalReadOnly")
                )
            );
        }
        catch (Exception ex)
        {
            status.Text = L.Owned(ex.Message);
            tree.Items.Clear();
        }
        finally
        {
            busy = false;
        }
    }

    void ShowTree()
    {
        tree.Items.Clear();
        var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        bool filtered = !string.IsNullOrWhiteSpace(search.Text);
        foreach (
            var entry in entries.Where(e =>
                !filtered || e.Path.Contains(search.Text, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            var node = new TreeViewItem
            {
                Header = filtered ? entry.Path : Path.GetFileName(entry.Path),
                Tag = entry,
                ToolTip = L.T("ui.installation.fileSizeBytes", L.Owned(entry.Kind), entry.Size),
            };
            nodes[entry.Path] = node;
            string? parent = Path.GetDirectoryName(entry.Path);
            if (!filtered && parent is not null && nodes.TryGetValue(parent, out var folder))
                folder.Items.Add(node);
            else
                tree.Items.Add(node);
        }
    }

    async Task Preview(RpfEntryInfo entry)
    {
        try
        {
            fields = [];
            document = null;
            visual.ItemsSource = null;
            string requestedPath = selected,
                requestedArchive = archive;
            if (entry.Kind.StartsWith("RSC"))
            {
                xml.Text = L.T("ui.installation.rscDecodingNotSupported");
                visual.ItemsSource = null;
                return;
            }
            byte[] bytes = await Task.Run(() => workspace.Read(requestedArchive, requestedPath));
            if (selected != requestedPath || archive != requestedArchive)
                return;
            xml.Text =
                entry.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || entry.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                || entry.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')
                    : L.T("ui.installation.binaryFileBUseExport", bytes.Length);
            fields = [];
            document = null;
            visual.ItemsSource = null;
            if (xml.Text.TrimStart().StartsWith('<'))
            {
                document = InstallDocuments.Parse(xml.Text);
                void Visit(XElement e, string prefix)
                {
                    string p =
                        prefix
                        + "/"
                        + e.Name.LocalName
                        + "["
                        + ((e.Parent?.Elements(e.Name).TakeWhile(c => c != e).Count() ?? 0) + 1)
                        + "]";
                    foreach (var a in e.Attributes())
                    {
                        var attr = a;
                        fields.Add(
                            new()
                            {
                                Path = p + "/@" + a.Name.LocalName,
                                Value = a.Value,
                                Apply = v => attr.Value = v,
                            }
                        );
                    }
                    if (!e.HasElements)
                    {
                        var el = e;
                        fields.Add(
                            new()
                            {
                                Path = p,
                                Value = e.Value,
                                Apply = v => el.Value = v,
                            }
                        );
                    }
                    foreach (var child in e.Elements())
                        Visit(child, p);
                }
                if (document.Root is not null)
                    Visit(document.Root, "");
                visual.ItemsSource = fields;
            }
        }
        catch (Exception ex)
        {
            xml.Text = L.Owned(ex.Message);
            visual.ItemsSource = null;
            document = null;
        }
    }

    bool CanWrite()
    {
        if (!workspace.Editable(archive))
        {
            MessageBox.Show(this, L.T("ui.installation.createArchiveCopyModsFirst"));
            return false;
        }
        if (game.IsRunning())
        {
            MessageBox.Show(this, L.T("ui.installation.closeGameBeforeChangingFiles"));
            return false;
        }
        return true;
    }

    async Task Copy()
    {
        if (workspace.Editable(archive))
        {
            MessageBox.Show(this, L.T("ui.installation.archiveAlreadyMods"));
            return;
        }
        if (
            MessageBox.Show(
                this,
                L.T("ui.installation.createCopyJustRpfMods"),
                L.T("ui.installation.preparingMods"),
                MessageBoxButton.YesNo
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            workspace.FullPath(archive);
            if (File.Exists(workspace.FullPath(game.MapWriteTarget(archive))))
                throw new InvalidOperationException(L.T("ui.installation.copyAlreadyExistsModsOpen"));
            var result = await run(["rpf-copy", game.Root, archive]);
            if (result.code != 0)
                throw new InvalidOperationException(result.output);
            archive = game.MapWriteTarget(archive);
            Changed?.Invoke();
            await Load();
        }
        catch (Exception ex)
        {
            InstallUi.Error(this, ex);
        }
    }

    async Task Export()
    {
        if (selected.Length == 0)
            return;
        var d = new SaveFileDialog { FileName = Path.GetFileName(selected.Split('|')[^1]) };
        if (d.ShowDialog(this) != true)
            return;
        try
        {
            var bytes = await Task.Run(() => workspace.Read(archive, selected, 256L << 20));
            await File.WriteAllBytesAsync(d.FileName, bytes);
        }
        catch (Exception ex)
        {
            InstallUi.Error(this, ex);
        }
    }

    async Task Import()
    {
        if (!CanWrite())
            return;
        var d = new OpenFileDialog();
        if (d.ShowDialog(this) != true)
            return;
        string target = selected;
        if (target.Length == 0)
        {
            var prompt = InstallUi.Window(this, L.T("ui.installation.pathInsideRpf"), 650, 250);
            var p = new StackPanel { Margin = new Thickness(20) };
            prompt.Content = p;
            var input = new TextBox();
            p.Children.Add(InstallUi.Text(L.T("ui.installation.exampleCommonDataFileXml")));
            p.Children.Add(input);
            p.Children.Add(InstallUi.Button(L.T("ui.installation.select"), () => prompt.DialogResult = true));
            if (prompt.ShowDialog() != true)
                return;
            target = (nested.Length > 0 ? nested + "|" : "") + input.Text.Trim();
        }
        await Write(target, d.FileName, false);
    }

    async Task Delete()
    {
        if (!CanWrite() || selected.Length == 0)
            return;
        await Write(selected, null, true);
    }

    async Task SaveXml(bool fromTable)
    {
        if (!CanWrite() || selected.Length == 0)
            return;
        try
        {
            string text = xml.Text;
            if (fromTable)
            {
                visual.CommitEdit();
                visual.CommitEdit(DataGridEditingUnit.Row, true);
                if (document is null)
                    throw new InvalidOperationException(L.T("ui.installation.openXmlFileFirst"));
                foreach (var f in fields)
                    f.Apply(f.Value);
                text = document.ToString();
            }
            InstallDocuments.Parse(text);
            string temp = Path.Combine(Path.GetTempPath(), "ModHub", "xml-" + Guid.NewGuid().ToString("N") + ".xml");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                await Write(selected, temp, false);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        catch (Exception ex)
        {
            InstallUi.Error(this, ex);
        }
    }

    async Task Write(string target, string? source, bool delete)
    {
        if (busy)
            return;
        string? temporary = null;
        try
        {
            if (!delete && source is not null && target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var incoming = InstallDocuments.ReadText(source);
                InstallDocuments.Parse(incoming);
                if (target.EndsWith("dlclist.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var current = Encoding.UTF8.GetString(workspace.Read(archive, target));
                    var diff = InstallDocuments.CompareDlc(current, incoming);
                    temporary = Path.Combine(
                        Path.GetTempPath(),
                        "ModHub",
                        "dlc-" + Guid.NewGuid().ToString("N") + ".xml"
                    );
                    Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                    File.WriteAllText(temporary, InstallDocuments.MergeDlc(current, incoming));
                    source = temporary;
                    if (
                        MessageBox.Show(
                            this,
                            L.T("ui.installation.addDlcAllExistingEntries", L.Join(", ", diff.Added)),
                            "DLC List · " + game.Edition,
                            MessageBoxButton.YesNo
                        ) != MessageBoxResult.Yes
                    )
                        return;
                }
                else if (
                    target.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase)
                    && InstallDocuments.Kind(incoming) != XmlDocumentKind.GameConfig
                )
                    throw new InvalidDataException(L.T("ui.installation.fileNotGameconfig"));
                else if (
                    target.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase)
                    && MessageBox.Show(
                        this,
                        L.T("ui.installation.gameconfigCompatibilityGameBuildUnconfirmed"),
                        "GameConfig · " + game.Edition,
                        MessageBoxButton.YesNo
                    ) != MessageBoxResult.Yes
                )
                    return;
            }
            if (
                MessageBox.Show(
                    this,
                    L.T(
                        "ui.installation.gtaVPreviousStateWill",
                        (delete ? L.T("ui.installation.delete") : L.T("ui.installation.change")) + target,
                        game.Edition
                    ),
                    L.T("ui.common.confirmation"),
                    MessageBoxButton.YesNo
                ) != MessageBoxResult.Yes
            )
                return;
            busy = true;
            string path = Path.Combine(
                Path.GetTempPath(),
                "ModHub",
                "rpf-edit-" + Guid.NewGuid().ToString("N") + ".json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(new RpfEditRequest(archive, target, source, delete, hash))
                );
                var result = await run(["rpf-edit", game.Root, path]);
                if (result.code != 0)
                    throw new InvalidOperationException(result.output);
                Changed?.Invoke();
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
                busy = false;
            }
            await Load();
        }
        catch (Exception ex)
        {
            InstallUi.Error(this, ex);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
