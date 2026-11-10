using System.Globalization;

namespace Gf2ExiliumTranslationLauncher.Localization;

public sealed class LanguageOption
{
    public LanguageOption(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
        Culture = CultureInfo.GetCultureInfo(code);
    }

    public string Code { get; }

    public string DisplayName { get; }

    public CultureInfo Culture { get; }

    public override string ToString() => DisplayName;
}
