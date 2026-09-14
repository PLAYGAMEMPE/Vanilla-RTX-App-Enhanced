using System;
using System.Linq;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Applies/resolves the app's UI language, mirroring how ThemeService handles the
/// light/dark theme. Unlike theme (which can flip live), a language change needs
/// the app to restart: WinUI resolves .resw strings once at startup (window
/// construction, x:Uid lookups, anything already assigned to a TextBlock.Text in
/// code), and there's no reliable way to force every already-rendered string in a
/// ~10k line UI to re-resolve without just rebuilding the visual tree from
/// scratch — which is what a restart already does for free. The caller (the
/// language flyout in MainWindow) is expected to prompt for a restart after
/// calling ApplyLanguage.
/// </summary>
public static class LanguageService
{
    /// <summary>Language tags this build ships translations for, in menu order.</summary>
    public static readonly (string Tag, string DisplayName)[] SupportedLanguages =
    {
        ("en-US", "English"),
        ("es-ES", "Espanol"),
    };

    /// <summary>Hard fallback when nothing on the user's preferred-languages list matches a shipped translation.</summary>
    private const string FallbackTag = "en-US";

    /// <summary>
    /// The language tag Loc.Get resolves strings against right now - always one of
    /// SupportedLanguages' tags, never "System" (ApplyLanguage resolves that
    /// sentinel to a real tag before this is read). Loc.cs applies this on every
    /// lookup via a ResourceContext.QualifierValues override, which works whether
    /// or not the app has package identity - see ApplyLanguage below for why that
    /// matters.
    /// </summary>
    public static string CurrentLanguageTag { get; private set; } = FallbackTag;

    /// <summary>
    /// "System" resolves to whichever shipped language best matches the user's
    /// Windows display-language preferences (see DetectSystemLanguage) - resolved
    /// explicitly here rather than left as an implicit fallback, so the result is
    /// always deterministically one of SupportedLanguages.
    ///
    /// Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride is the API
    /// that makes x:Uid-resolved XAML strings follow the chosen language (Loc.Get
    /// calls instead read CurrentLanguageTag above, via Loc.cs's own
    /// ResourceContext override, which doesn't need this at all) - but it requires
    /// MSIX package identity and fails via an unrecoverable native crash, not a
    /// catchable exception, without one. Only touch it when actually packaged.
    /// Unpackaged (a portable .exe), x:Uid content falls back to whatever language
    /// Windows/the PRI's own fallback resolves to; Loc.Get-driven content still
    /// honors the user's choice correctly either way.
    ///
    /// Must run before any window/resource gets constructed to take effect for
    /// that launch - see App.OnLaunched.
    /// </summary>
    public static void ApplyLanguage(string tag)
    {
        CurrentLanguageTag = tag == "System" ? DetectSystemLanguage() : tag;

        if (PackageContext.IsPackaged)
        {
            try { ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguageTag; }
            catch { /* best effort - Loc.Get-driven content still gets the right language either way */ }
        }
    }

    /// <summary>
    /// Best-effort match between the user's Windows language preferences (ordered
    /// most-preferred first) and the languages this app ships translations for.
    /// Compares by primary subtag only (e.g. "es" out of "es-MX") since we only
    /// ship one regional variant per language - so a user set to Mexican, Spanish,
    /// or Argentinian Windows all land on our single es-ES translation instead of
    /// silently missing it over a region mismatch. Falls back to English if the
    /// user's list contains no language we've translated.
    /// </summary>
    public static string DetectSystemLanguage()
    {
        try
        {
            foreach (var preferred in GlobalizationPreferences.Languages)
            {
                var primary = preferred.Split('-')[0];
                var match = SupportedLanguages.FirstOrDefault(lang =>
                    string.Equals(lang.Tag.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));
                if (match.Tag != null)
                    return match.Tag;
            }
        }
        catch
        {
            // GlobalizationPreferences is a simple OS read that shouldn't fail, but
            // language detection is not worth ever crashing startup over.
        }

        return FallbackTag;
    }

    /// <summary>Reads the persisted language choice ("System" if never set).</summary>
    public static string ResolveSavedLanguage() =>
        string.IsNullOrEmpty(EnvironmentVariables.Persistent.AppLanguage) ? "System" : EnvironmentVariables.Persistent.AppLanguage;
}
