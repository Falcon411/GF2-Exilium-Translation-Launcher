using System.Globalization;
using System.Reflection;
using System.Resources;
using Gf2ExiliumTranslationLauncher.Diagnostics;

namespace Gf2ExiliumTranslationLauncher.Localization;

public static class AppText
{
    private const string EnglishCode = "en-US";
    private const string RussianCode = "ru-RU";

    private static readonly ResourceManager ResourceManager =
        new("Gf2ExiliumTranslationLauncher.Localization.Strings", Assembly.GetExecutingAssembly());

    private static LanguageOption _currentLanguage;

    static AppText()
    {
        SupportedLanguages =
        [
            new LanguageOption(EnglishCode, "English"),
            new LanguageOption(RussianCode, "Русский")
        ];

        _currentLanguage = ResolveLanguage("");
        ApplyCulture(_currentLanguage.Culture);
    }

    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; }

    public static LanguageOption CurrentLanguage => _currentLanguage;

    public static void Initialize(string? configuredLanguageCode)
        => SetLanguage(configuredLanguageCode);

    public static void SetLanguage(string? languageCode)
    {
        var language = ResolveLanguage(languageCode);
        _currentLanguage = language;
        ApplyCulture(language.Culture);
        AppLog.Info($"UI language set. Requested={languageCode ?? "<null>"}, Selected={language.Code}");
    }

    public static LanguageOption ResolveLanguage(string? languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            var configuredLanguage = SupportedLanguages.FirstOrDefault(language =>
                string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(language.Culture.TwoLetterISOLanguageName, languageCode, StringComparison.OrdinalIgnoreCase));

            if (configuredLanguage is not null)
            {
                return configuredLanguage;
            }
        }

        return IsRussianCulture(CultureInfo.CurrentUICulture)
            ? SupportedLanguages.Single(language => language.Code == RussianCode)
            : SupportedLanguages.Single(language => language.Code == EnglishCode);
    }

    public static string Get(string key)
        => ResourceManager.GetString(key, _currentLanguage.Culture) ?? $"!{key}!";

    public static string Format(string key, params object?[] args)
        => string.Format(_currentLanguage.Culture, Get(key), args);

    private static bool IsRussianCulture(CultureInfo culture)
        => string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
