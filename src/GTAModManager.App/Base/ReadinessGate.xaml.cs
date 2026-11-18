using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Base;

public enum GateStep
{
    FirstLaunch,
    BattlEye,
    BasePackage,
    Ready,
}

/// <summary>
/// First-run safety screen over the install pages: launch the game once → BattlEye off → base package.
/// It neither checks nor installs anything itself: it only shows the state and forwards clicks to the main window.
/// </summary>
public partial class ReadinessGate : UserControl
{
    public event Action? ActionRequested;
    public event Action? CheckRequested;
    public event Action? SkipRequested;

    public GateStep Step { get; private set; } = GateStep.Ready;

    /// <summary>The ScriptHookVDotNet build picked for Legacy: the official Nightly or the Legacy + Enhanced build.</summary>
    public ShvdnVariant LegacyShvdn => ShvdnEnhanced.IsChecked == true ? ShvdnVariant.Enhanced : ShvdnVariant.Nightly;

    public ReadinessGate() => InitializeComponent();

    private static readonly Brush DoneFill = Frozen(
        new LinearGradientBrush(Color.FromRgb(0x6E, 0xF0, 0xA6), Color.FromRgb(0x22, 0xA8, 0x5E), 90)
    );
    private static readonly Brush PendingFill = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2E, 0x34)));
    private static readonly Brush DarkText = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x0C, 0x02)));

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    public void Show(
        GameEdition edition,
        GateStep step,
        BattlEyeState battlEye,
        StarterStatus? status,
        bool installFailed
    )
    {
        Step = step;
        var name = "GTA V " + edition;
        Kicker.Text = L.T("ui.base.preparing", name.ToUpperInvariant());
        Mark(Mark1, Mark1Text, "1", step, GateStep.FirstLaunch);
        Mark(Mark2, Mark2Text, "2", step, GateStep.BattlEye);
        Mark(Mark3, Mark3Text, "3", step, GateStep.BasePackage);

        Detail1.Text =
            step == GateStep.FirstLaunch
                ? L.T("ui.base.gameHasNeverBeenLaunched")
                : L.T("ui.base.doneGameProfileFound");
        Detail2.Text = step switch
        {
            GateStep.FirstLaunch => L.T("ui.base.weLlCheckAfterFirst"),
            GateStep.BattlEye => battlEye == BattlEyeState.Enabled
                ? L.T("ui.base.gameWasLastLaunchedBattleye")
                : L.T("ui.base.noGameLaunchBattleyeDisabled"),
            _ => battlEye == BattlEyeState.Disabled
                ? L.T("ui.base.disabledLastLaunchWasWithout")
                : L.T("ui.base.skippedManually"),
        };
        Detail3.Text =
            status is null ? L.T("ui.base.weLlCheckAfterBattleye")
            : status.Complete ? L.T("ui.base.everythingInstalled")
            : L.T("ui.base.missing", L.Join(", ", status.Missing));

        SkipButton.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Visible;
        // Legacy runs either ScriptHookVDotNet build; Enhanced has only one, so there is nothing to choose.
        ShvdnChoice.Visibility =
            step == GateStep.BasePackage && edition == GameEdition.Legacy && status is { Base.Shvdn: false }
                ? Visibility.Visible
                : Visibility.Collapsed;
        switch (step)
        {
            case GateStep.FirstLaunch:
                NowTitle.Text = L.T("ui.base.launchGameOnce");
                NowText.Text = L.T("ui.base.n1LaunchAsUsualThrough", name);
                ActionButton.Content = L.T("ui.base.launchGame");
                break;
            case GateStep.BattlEye:
                NowTitle.Text = L.T("ui.base.disableBattleye");
                NowText.Text = L.T(
                    "ui.base.modsDoNotLoadWhile",
                    (edition == GameEdition.Enhanced ? " Enhanced" : " Legacy")
                );
                ActionButton.Content = L.T("ui.base.launchGame");
                SkipButton.Content = L.T("ui.base.iVeAlreadyDisabledSkip");
                SkipButton.Visibility = Visibility.Visible;
                break;
            case GateStep.BasePackage:
                NowTitle.Text = L.T("ui.base.installBasePackage");
                NowText.Text = L.T(
                    "ui.base.oneButtonInstallsEverythingMods",
                    (edition == GameEdition.Enhanced ? L.T("ui.base.modkitLimitAdjusterStaticBounds") : "")
                );
                ActionButton.Content = L.T("ui.base.installBasePackage2");
                if (installFailed)
                {
                    SkipButton.Content = L.T("ui.base.continueWithoutBasePackage");
                    SkipButton.Visibility = Visibility.Visible;
                }
                break;
        }
    }

    public void SetBusy(bool busy, string status)
    {
        ActionButton.IsEnabled = CheckButton.IsEnabled = SkipButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private void Mark(Border mark, TextBlock text, string number, GateStep current, GateStep own)
    {
        bool done = current > own,
            active = current == own;
        mark.Background =
            done ? DoneFill
            : active ? (Brush)FindResource("GlossOrange")
            : PendingFill;
        mark.BorderBrush = active ? (Brush)FindResource("GlossOrangeEdge") : null;
        mark.BorderThickness = new Thickness(active ? 1 : 0);
        text.Text = done ? "✓" : number;
        text.Foreground = done || active ? DarkText : (Brush)FindResource("Muted");
    }

    private void OnAction(object sender, RoutedEventArgs e) => ActionRequested?.Invoke();

    private void OnCheck(object sender, RoutedEventArgs e) => CheckRequested?.Invoke();

    private void OnSkip(object sender, RoutedEventArgs e) => SkipRequested?.Invoke();
}
