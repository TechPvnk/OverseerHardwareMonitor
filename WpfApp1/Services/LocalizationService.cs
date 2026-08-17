using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Markup;

namespace Overseer.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private const string DefaultCultureName = "en";
    private const string SpanishCultureName = "es";
    private readonly ResourceManager _resources = new("Overseer.Resources.Strings", typeof(LocalizationService).Assembly);
    private CultureInfo _culture;

    private LocalizationService()
    {
        _culture = CreateCulture(ReadSavedCulture());
        ApplyCulture(_culture);
    }

    public static LocalizationService Instance { get; } = new();

    public CultureInfo Culture => _culture;
    public bool IsEnglish => _culture.TwoLetterISOLanguageName == DefaultCultureName;
    public bool IsSpanish => _culture.TwoLetterISOLanguageName == SpanishCultureName;

    public string this[string key] =>
        _resources.GetString(key, _culture)
        ?? _resources.GetString(key, CultureInfo.GetCultureInfo(DefaultCultureName))
        ?? key;

    public void SetCulture(string cultureName)
    {
        CultureInfo culture = CreateCulture(cultureName);
        if (string.Equals(culture.Name, _culture.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _culture = culture;
        ApplyCulture(culture);
        SaveCulture(culture.TwoLetterISOLanguageName);
        OnPropertyChanged(nameof(Culture));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsSpanish));
        OnPropertyChanged("Item[]");
    }

    private static CultureInfo CreateCulture(string? cultureName)
    {
        return string.Equals(cultureName, SpanishCultureName, StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo(SpanishCultureName)
            : CultureInfo.GetCultureInfo(DefaultCultureName);
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static string ReadSavedCulture()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? File.ReadAllText(SettingsPath).Trim()
                : DefaultCultureName;
        }
        catch
        {
            return DefaultCultureName;
        }
    }

    private static void SaveCulture(string cultureName)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, cultureName);
        }
        catch
        {
            // Localization preferences are non-critical; retain the session selection.
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TechPvnk",
        "Overseer",
        "language.txt");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        Binding binding = new($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}


public sealed class LocFormatExtension : MarkupExtension
{
    public LocFormatExtension()
    {
    }

    public LocFormatExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        MultiBinding binding = new()
        {
            Converter = new LocalizedFormatConverter(),
            ConverterParameter = Key
        };

        binding.Bindings.Add(new Binding(Path));
        binding.Bindings.Add(new Binding(nameof(LocalizationService.Culture))
        {
            Source = LocalizationService.Instance
        });

        return binding.ProvideValue(serviceProvider);
    }
}

public sealed class LocalizedFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string key = parameter?.ToString() ?? string.Empty;
        object[] arguments = values.Where(value => value is not CultureInfo).ToArray();
        return string.Format(LocalizationService.Instance[key], arguments);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
