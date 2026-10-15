using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Localization;

/// <summary>
/// <c>{l:T Win.Key}</c>: a UI string in the current language, read once when the control is built — as the code-built
/// dialogs read <c>T(key)</c> when they opened (epic #89: dialogs moved to AXAML templates). For text that follows
/// a language change while the window is open use <c>{DynamicResource Ui.Key}</c>. Every key used this way must exist
/// in both languages (<c>LocalizationKeyUsageTests</c>).
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => UiLocalizationService.Text(Key);
}
