using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Transactions;

namespace GTAModManager.App.Installation;

internal sealed class InstallPlanWindow : Window
{
    readonly GameInstall game;
    readonly InstallationRequest request;
    readonly InstallInspection inspection;
    readonly TextBox report = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 330,
    };

    public bool Accepted { get; private set; }

    public InstallPlanWindow(Window owner, GameInstall game, InstallationRequest request, InstallInspection inspection)
    {
        this.game = game;
        this.request = request;
        this.inspection = inspection;
        Owner = owner;
        Title = L.T("ui.installation.installationPlanGtaV", game.Edition);
        Width = 900;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg");
        Foreground = System.Windows.Media.Brushes.White;
        var p = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        p.Children.Add(InstallUi.Text(Path.GetFileName(request.Source), 20));
        p.Children.Add(InstallUi.Text(L.T("ui.installation.targetGtaV", game.Edition, game.Root)));
        bool config =
            request.Mappings.Values.Any(v => v.EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase))
            || Path.GetExtension(request.Source).Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && InstallDocuments.Kind(InstallDocuments.ReadText(request.Source)) == XmlDocumentKind.GameConfig;
        bool rpf = config || inspection.Targets.Any(v => v.Contains(".rpf", StringComparison.OrdinalIgnoreCase));
        var summary = new GTAModManager.App.Localization.LocalizedStringBuilder();
        if (rpf)
        {
            summary.AppendLine(
                File.Exists(Path.Combine(game.ModsDir, @"update\update.rpf"))
                    ? L.T("ui.installation.existingModsUpdateUpdateRpf")
                    : L.T("ui.installation.copyUpdateRpfWillCreated")
            );
            summary.AppendLine(L.T("ui.installation.modsUpdateX64DlcpacksFolder"));
        }
        summary.AppendLine(
            config
                ? L.T("ui.installation.selectedGameconfigWillInstalledModded")
                : L.T("ui.installation.selectedPackageSChangesWill")
        );
        summary.AppendLine(L.T("ui.installation.previousStateWillBackedUp"));
        if (config && AdjusterComponents.Missing(game).Count > 0)
            summary.AppendLine(L.T("ui.installation.afterInstallationWeLlSuggest"));
        p.Children.Add(InstallUi.Text(summary.ToString(), 14));
        p.Children.Add(
            new Expander
            {
                Header = L.T("ui.installation.changeDetails"),
                Content = report,
                Margin = new Thickness(0, 8, 0, 8),
            }
        );
        if (inspection.Description.Length > 0)
            p.Children.Add(
                new Expander
                {
                    Header = L.T("ui.installation.readmeAuthorSInstructions"),
                    Content = new TextBox
                    {
                        Text = inspection.Description,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 220,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    Margin = new Thickness(0, 8, 0, 8),
                }
            );
        var button = InstallUi.Button(L.T("ui.common.install"), () =>
        {
            // The Install button confirms the displayed file changes, without claiming build compatibility.
            request.PrepareMods = true;
            request.ConfirmOverwrite = true;
            request.ConfirmUnverified = true;
            Accepted = true;
            DialogResult = true;
        });
        p.Children.Add(button);
        Loaded += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                report.Text = await Task.Run(Describe);
                button.IsEnabled = true;
            }
            catch (Exception ex)
            {
                report.Text = L.Owned(ex.Message);
            }
        };
    }

    string Describe()
    {
        var text = new GTAModManager.App.Localization.LocalizedStringBuilder();
        text.AppendLine(L.T("ui.installation.allGameDataWrittenMods"));
        foreach (var warning in inspection.Warnings)
            text.AppendLine(warning);
        foreach (var mapping in request.Mappings)
            text.AppendLine(mapping.Key + " → " + mapping.Value);
        foreach (var dep in inspection.Dependencies)
            text.AppendLine(
                L.T(
                    "ui.installation.nameWithDetail",
                    L.Join(
                        "",
                        (
                            dep.Present ? L.T("ui.installation.found")
                            : dep.Required ? L.T("ui.installation.required")
                            : L.T("ui.installation.recommendation")
                        ),
                        dep.Title
                    ),
                    dep.Evidence
                )
            );
        if (Path.GetExtension(request.Source).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            AppendXml(text, InstallDocuments.ReadText(request.Source));
        else if (
            request.Mappings.Count == 1
            && request.Mappings.Values.Single().EndsWith("gameconfig.xml", StringComparison.OrdinalIgnoreCase)
        )
        {
            using var archive = ModArchive.OpenExpanded(request.Source);
            AppendXml(text, Encoding.UTF8.GetString(archive.Read(request.Mappings.Keys.Single())).TrimStart('\uFEFF'));
        }
        request.ExpectedTargets.Clear();
        using var plan = InstallationService.Prepare(game, request, true);
        request.ExpectedTargets.Clear();
        foreach (var change in plan.Changes)
        {
            string full = Path.Combine(game.Root, change.RelativePath);
            request.ExpectedTargets[change.RelativePath] = File.Exists(full) ? FileTransactionEngine.Hash(full) : null;
        }
        if (
            inspection.Targets.Any(t => t.Contains(".rpf", StringComparison.OrdinalIgnoreCase))
            || request.Mappings.Values.Any(t => t.Contains(".rpf|", StringComparison.OrdinalIgnoreCase))
        )
        {
            var workspace = new RpfWorkspace(game);
            if (File.Exists(workspace.FullPath(workspace.WorkingUpdate)))
                request.ExpectedTargets[workspace.WorkingUpdate] = FileTransactionEngine.Hash(
                    workspace.FullPath(workspace.WorkingUpdate)
                );
        }
        foreach (var item in plan.Items)
            text.AppendLine(L.Join("", item.Action, ": ", item.Target, " ", L.Owned(item.Detail)));
        foreach (var line in plan.Log)
            text.AppendLine(line);
        foreach (var error in plan.Errors)
            text.AppendLine(L.T("ui.installation.decisionRequired", error));
        return text.ToString();
    }

    void AppendXml(GTAModManager.App.Localization.LocalizedStringBuilder text, string incoming)
    {
        var kind = InstallDocuments.Kind(incoming);
        if (kind == XmlDocumentKind.Other)
            return;
        var w = new RpfWorkspace(game);
        var name = kind == XmlDocumentKind.DlcList ? "dlclist.xml" : "gameconfig.xml";
        string current = Encoding.UTF8.GetString(w.Read(w.WorkingUpdate, @"common\data\" + name)).TrimStart('\uFEFF');
        if (kind == XmlDocumentKind.DlcList)
        {
            var diff = InstallDocuments.CompareDlc(current, incoming);
            text.AppendLine(
                L.T("ui.installation.dlcListAddingPreservingExisting", diff.Added.Count, diff.Existing.Count)
            );
            foreach (var n in diff.Added)
            {
                text.AppendLine("+ " + n);
                if (
                    !n.StartsWith("platform:/", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(Path.Combine(game.Root, @"mods\update\x64\dlcpacks", n, "dlc.rpf"))
                    && !File.Exists(Path.Combine(game.Root, @"update\x64\dlcpacks", n, "dlc.rpf"))
                )
                    text.AppendLine(L.T("ui.installation.dlcFilesNotFoundAdding", n));
            }
            foreach (var n in diff.Removed)
                text.AppendLine(L.T("ui.installation.willKept", n));
            if (diff.Duplicates.Count > 0)
                text.AppendLine(L.T("ui.installation.duplicatesIncomingFileWillNot", L.Join(", ", diff.Duplicates)));
        }
        else
            foreach (var diff in InstallDocuments.Compare(current, incoming))
                text.AppendLine(diff.Path + ": " + diff.Before + " → " + diff.After + " · " + diff.Explanation);
    }
}
