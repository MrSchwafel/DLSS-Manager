using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using DLSSManager.Core;

namespace DLSSManager.App;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public LocExtension() : this("") { }

    public string? Path { get; set; }
    public string? Fallback { get; set; }
    public RelativeSource? RelativeSource { get; set; }
    public bool KeyFromPath { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var text = new Binding($"[{key}]") { Source = AppStrings.Instance, Mode = BindingMode.OneWay };
        if (Path is null) return text.ProvideValue(serviceProvider);

        var value = new Binding(Path) { Mode = BindingMode.OneWay };
        if (RelativeSource is not null) value.RelativeSource = RelativeSource;

        var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new LocalizedValue(key, Fallback, KeyFromPath) };
        binding.Bindings.Add(value);
        binding.Bindings.Add(new Binding(nameof(AppStrings.Language)) { Source = AppStrings.Instance });
        return binding.ProvideValue(serviceProvider);
    }

    private sealed class LocalizedValue(string key, string? fallback, bool keyFromPath) : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var missing = values[0] is null || values[0] == DependencyProperty.UnsetValue;
            if (keyFromPath) return values[0] is string resourceKey ? LocalizationService.GetText(resourceKey) : "";
            if (fallback is not null) return missing ? LocalizationService.GetText(fallback) : values[0];
            if (key.Length == 0 && values[0] is LocalizedText message) return message.Text;
            return LocalizationService.GetText(key, missing ? 0 : values[0]);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

internal sealed class AppStrings : INotifyPropertyChanged
{
    public static AppStrings Instance { get; } = new();

    private AppStrings() => LocalizationService.LanguageChanged += (_, _) =>
    {
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
    };

    public string this[string key] => LocalizationService.GetText(key);
    public string Language => LocalizationService.Language;
    public event PropertyChangedEventHandler? PropertyChanged;
}
