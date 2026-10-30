using System.ComponentModel;
using System.Globalization;
using System.Threading;

namespace UltimateUpdater.Services;

/// <summary>Live language switch across all supported locales, no restart. Singleton so XAML can bind via x:Static; culture drives Resources.Strings.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationService()
    {
    }

    /// <summary>Indexer binding target: {Binding Source={x:Static services:LocalizationService.Instance}, Path=[Key]}.</summary>
    public string this[string key] => Resources.Strings.Get(key);

    /// <summary>The language codes the app ships resources for.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } =
        ["en", "de", "es", "it", "pt", "pl", "zh-Hans", "ja", "nl", "tr", "ro", "fr"];

    /// <summary>
    /// The setting last applied ("system" or one of <see cref="SupportedLanguages"/>). A worker process starts
    /// with the OS language and has to be told which one the app is actually using, so it can report in the same one.
    /// </summary>
    public string CurrentLanguage { get; private set; } = "system";

    /// <summary>"system" follows the Windows display language when the app ships it and falls back to English otherwise.</summary>
    public void Apply(string languageSetting)
    {
        var culture = SupportedLanguages.Contains(languageSetting, StringComparer.Ordinal)
            ? new CultureInfo(languageSetting)
            : ResolveSystemCulture(CultureInfo.InstalledUICulture);

        CurrentLanguage = languageSetting;
        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // The two assignments above only live in the execution context that made them. Startup applies the
        // language inside an async method, and that context is put back when the method returns, so the UI
        // thread and every pool thread would format sizes and dates in the Windows language again.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Resources.Strings.Culture = culture;

        // empty property name = WPF convention for "refresh every binding sourced from this object"
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// Walks from the specific Windows culture (de-AT, pt-BR, zh-CN) up to its neutral parent until one of the
    /// shipped languages matches. Traditional Chinese is not shipped, so zh-TW and zh-HK fall back to English
    /// rather than to the Simplified script.
    /// </summary>
    internal static CultureInfo ResolveSystemCulture(CultureInfo installed)
    {
        for (var culture = installed; !culture.Equals(CultureInfo.InvariantCulture); culture = culture.Parent)
        {
            if (SupportedLanguages.Contains(culture.Name, StringComparer.OrdinalIgnoreCase))
                return installed;
        }

        return new CultureInfo("en");
    }
}
