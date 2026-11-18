using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Localization;

/// <summary>A resource binding which updates in place when the language pack changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CreateBinding(Key).ProvideValue(serviceProvider);

    public static Binding CreateBinding(string key, params object?[] arguments) =>
        new(nameof(LocalizedBinding.Value))
        {
            Source = new LocalizedBinding(key, arguments),
            Mode = BindingMode.OneWay,
        };
}

public sealed class LocalizedBinding : INotifyPropertyChanged
{
    private readonly LocalizedText _text;

    public LocalizedBinding(string key, params object?[] arguments)
        : this(new LocalizedText(key, arguments)) { }

    public LocalizedBinding(LocalizedText text)
    {
        _text = text;
        WeakEventManager<LocalizationService, EventArgs>.AddHandler(
            LocalizationService.Current,
            nameof(LocalizationService.LanguageChanged),
            OnLanguageChanged
        );
    }

    public string Value => LocalizationService.Current.Render(_text);
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) => PropertyChanged?.Invoke(this, new(nameof(Value)));
}
