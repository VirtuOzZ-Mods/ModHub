using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Localization;

/// <summary>
/// Keeps explicitly localized programmatic values live. A string is eligible only when
/// the localization engine created that exact object. Mod data and user input are never
/// looked up by their text. SetCurrentValue preserves existing WPF binding expressions.
/// </summary>
public static class LiveLocalization
{
    private static readonly ConditionalWeakTable<DependencyObject, Target> Targets = new();
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;
        LocalizationService.Current.LanguageChanged += RefreshWindows;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded),
            true
        );
        EventManager.RegisterClassHandler(
            typeof(FrameworkContentElement),
            FrameworkContentElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded),
            true
        );
    }

    private static void RefreshWindows(object? sender, EventArgs e)
    {
        var application = Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted)
            return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(() => RefreshWindows(sender, e));
            return;
        }
        // WPF only broadcasts Loaded to subtrees which request it. A class handler
        // alone does not mark every programmatically created child for broadcast.
        var visited = new HashSet<DependencyObject>();
        foreach (Window window in application.Windows)
            RefreshTree(window, visited);
    }

    private static void RefreshTree(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;
        var target = Targets.GetValue(node, static d => new Target(d));
        target.Connect();
        target.Refresh();
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray())
            RefreshTree(child, visited);
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                RefreshTree(VisualTreeHelper.GetChild(node, i), visited);
        if (node is FrameworkElement element)
        {
            if (element.ContextMenu is { } menu)
                RefreshTree(menu, visited);
            if (element.ToolTip is DependencyObject tooltip)
                RefreshTree(tooltip, visited);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject target)
            Track(target);
    }

    /// <summary>Also used by the UI harness for controls not attached to a window.</summary>
    public static void Track(DependencyObject target) => Targets.GetValue(target, static d => new Target(d)).Connect();

    // Run's text container stores characters rather than the original string object.
    // Capture explicit provenance before it enters the text container.
    public static Run CreateRun(string text)
    {
        var run = new Run();
        if (LocalizationService.Current.Describe(text) is { } message)
            BindingOperations.SetBinding(
                run,
                Run.TextProperty,
                new Binding(nameof(LocalizedBinding.Value))
                {
                    Source = new LocalizedBinding(message),
                    Mode = BindingMode.OneWay,
                }
            );
        else
            run.Text = text;
        return run;
    }

    private sealed class Target
    {
        private readonly DependencyObject _target;
        private readonly List<Value> _values = [];
        private bool _connected;

        public Target(DependencyObject target)
        {
            _target = target;
            if (target is FrameworkElement fe)
                fe.Unloaded += OnUnloaded;
            else if (target is FrameworkContentElement fce)
                fce.Unloaded += OnUnloaded;
            if (target is TextBlock)
                Add(TextBlock.TextProperty);
            if (target is AccessText)
                Add(AccessText.TextProperty);
            if (target is Run)
                Add(Run.TextProperty);
            if (target is TextBox)
                Add(TextBox.TextProperty);
            if (target is ContentControl)
                Add(ContentControl.ContentProperty);
            if (target is HeaderedContentControl)
                Add(HeaderedContentControl.HeaderProperty);
            if (target is HeaderedItemsControl)
                Add(HeaderedItemsControl.HeaderProperty);
            if (target is Window)
                Add(Window.TitleProperty);
            if (target is FrameworkElement or FrameworkContentElement)
            {
                Add(ToolTipService.ToolTipProperty);
                Add(AutomationProperties.NameProperty);
                Add(AutomationProperties.HelpTextProperty);
            }
        }

        private void Add(DependencyProperty property) => _values.Add(new Value(_target, property));

        public void Connect()
        {
            if (_connected)
                return;
            _connected = true;
            foreach (var value in _values)
                value.Connect();
            WeakEventManager<LocalizationService, EventArgs>.AddHandler(
                LocalizationService.Current,
                nameof(LocalizationService.LanguageChanged),
                OnLanguageChanged
            );
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_connected)
                return;
            foreach (var value in _values)
                value.Disconnect();
            WeakEventManager<LocalizationService, EventArgs>.RemoveHandler(
                LocalizationService.Current,
                nameof(LocalizationService.LanguageChanged),
                OnLanguageChanged
            );
            _connected = false;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            if (_target.Dispatcher.HasShutdownStarted)
                return;
            if (!_target.Dispatcher.CheckAccess())
            {
                _target.Dispatcher.BeginInvoke(() => OnLanguageChanged(sender, e));
                return;
            }
            Refresh();
        }

        public void Refresh()
        {
            foreach (var value in _values)
                value.Refresh();
        }
    }

    private sealed class Value(DependencyObject target, DependencyProperty property)
    {
        public void Connect() => Refresh();

        public void Disconnect() { }

        public void Refresh()
        {
            // Do not cache provenance: WPF suppresses DP notifications for equal text,
            // including replacements with another resource key or external content.
            var message = target.GetValue(property) is string current
                ? LocalizationService.Current.Describe(current)
                : null;
            if (message is not null)
                target.SetCurrentValue(property, LocalizationService.Current.Render(message));
        }
    }
}
