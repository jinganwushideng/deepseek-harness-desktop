using Microsoft.Win32;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using MediaColor = System.Windows.Media.Color;

namespace DeepSeekHarnessDesktop.Services;

public sealed record ShellSkinPalette(
    bool IsLight,
    MediaColor Background,
    MediaColor Surface,
    MediaColor Border,
    MediaColor Accent,
    MediaColor AccentText,
    MediaColor Text,
    MediaColor Muted);

public static class ShellThemeService
{
    public const string FollowWeb = "FollowWeb";
    public const string FollowSystem = "FollowSystem";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string NormalizeMode(string? value) => value?.Trim() switch
    {
        FollowSystem => FollowSystem,
        Light => Light,
        Dark => Dark,
        _ => FollowWeb
    };

    public static bool ResolveLight(string? mode, bool? webIsLight, bool systemIsLight)
    {
        return NormalizeMode(mode) switch
        {
            Light => true,
            Dark => false,
            FollowSystem => systemIsLight,
            _ => webIsLight ?? systemIsLight
        };
    }

    public static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch { return false; }
    }

    public static void ApplyPalette(bool light)
    {
        var resources = System.Windows.Application.Current.Resources;
        var dictionary = new System.Windows.ResourceDictionary
        {
            Source = new Uri(light ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml", UriKind.Relative)
        };
        if (resources.MergedDictionaries.Count == 0) resources.MergedDictionaries.Add(dictionary);
        else resources.MergedDictionaries[0] = dictionary;
    }

    public static ShellSkinPalette BuildSkinPalette(WebSkinAppearance appearance)
    {
        var fallbackBackground = appearance.IsLight ? MediaColor.FromRgb(245, 246, 248) : MediaColor.FromRgb(8, 9, 12);
        var background = ParseOr(appearance.Background, fallbackBackground);
        var isLight = RelativeLuminance(background) >= 0.42;
        var fallbackText = isLight ? MediaColor.FromRgb(23, 26, 32) : MediaColor.FromRgb(245, 246, 248);
        var text = ParseOr(appearance.Text, fallbackText);
        if (ContrastRatio(text, background) < 4.5) text = BestContrastingText(background);

        var derivedSurface = Mix(background, text, isLight ? 0.035 : 0.07);
        var surfaceHint = ParseOr(appearance.Surface, derivedSurface);
        var surface = Mix(derivedSurface, surfaceHint, 0.28);
        if (ContrastRatio(surface, background) > 1.38) surface = derivedSurface;

        // Some skins expose a white divider as their first border value. It is
        // only a hint here, so one CSS token cannot turn every native card white.
        var derivedBorder = Mix(background, text, isLight ? 0.14 : 0.18);
        var borderHint = ParseOr(appearance.Border, derivedBorder);
        var border = Mix(derivedBorder, borderHint, 0.14);
        if (ContrastRatio(border, background) > 1.8 || ContrastRatio(border, background) < 1.08) border = derivedBorder;

        var fallbackAccent = isLight ? MediaColor.FromRgb(75, 96, 224) : MediaColor.FromRgb(124, 142, 255);
        var accent = ParseOr(appearance.Accent, fallbackAccent);
        if (!IsUsableAccent(accent, background)) accent = fallbackAccent;
        if (!IsUsableAccent(accent, background)) accent = isLight ? MediaColor.FromRgb(49, 86, 210) : MediaColor.FromRgb(139, 158, 255);
        var accentText = BestContrastingText(accent);

        var derivedMuted = Mix(text, background, isLight ? 0.43 : 0.44);
        var muted = ParseOr(appearance.Muted, derivedMuted);
        if (ContrastRatio(muted, background) < 3.0 || ContrastRatio(muted, background) > ContrastRatio(text, background)) muted = derivedMuted;
        return new ShellSkinPalette(isLight, background, surface, border, accent, accentText, text, muted);
    }

    public static void ApplySkinAppearance(WebSkinAppearance appearance)
    {
        var resources = System.Windows.Application.Current.Resources;
        if (resources.MergedDictionaries.Count == 0) return;
        var palette = resources.MergedDictionaries[0];
        var skin = BuildSkinPalette(appearance);
        var background = skin.Background;
        var surface = skin.Surface;
        var border = skin.Border;
        var accent = skin.Accent;
        var text = skin.Text;
        var muted = skin.Muted;
        var isLight = skin.IsLight;

        Set(palette, "Bg", background); Set(palette, "ChromeBg", Mix(background, surface, 0.35));
        Set(palette, "HeaderBg", Mix(background, surface, 0.55)); Set(palette, "RailBg", Mix(background, surface, 0.42));
        Set(palette, "Panel", surface); Set(palette, "Panel2", Mix(surface, text, isLight ? 0.045 : 0.06));
        Set(palette, "InputBg", Mix(background, surface, 0.66)); Set(palette, "Border", border);
        Set(palette, "ControlBorder", Mix(border, text, 0.13)); Set(palette, "Accent", accent);
        Set(palette, "AccentText", skin.AccentText); Set(palette, "FocusBorder", accent);
        Set(palette, "ControlHoverBorder", Mix(accent, text, 0.08)); Set(palette, "Text", text);
        Set(palette, "SecondaryText", Mix(text, background, 0.18)); Set(palette, "StrongSecondaryText", Mix(text, background, 0.1));
        Set(palette, "SecondaryButtonText", Mix(text, background, 0.12)); Set(palette, "Muted", muted);
        Set(palette, "SubtleText", Mix(muted, background, 0.16)); Set(palette, "IconForeground", Mix(text, background, 0.24));
        Set(palette, "SecondaryButtonBg", Mix(surface, text, isLight ? 0.055 : 0.08));
        Set(palette, "IconHoverBg", Mix(surface, text, isLight ? 0.06 : 0.08)); Set(palette, "IconPressedBg", Mix(surface, text, isLight ? 0.11 : 0.14));
        Set(palette, "ListHover", Mix(surface, accent, isLight ? 0.07 : 0.1)); Set(palette, "ListSelected", Mix(surface, accent, isLight ? 0.15 : 0.2));
        Set(palette, "NavSelected", Mix(surface, accent, isLight ? 0.15 : 0.2)); Set(palette, "SegmentSelected", Mix(surface, accent, isLight ? 0.18 : 0.23));
        Set(palette, "TableHeader", Mix(background, surface, 0.72)); Set(palette, "ScrollThumb", Mix(background, text, isLight ? 0.25 : 0.27));
        Set(palette, "ScrollThumbHover", Mix(background, text, isLight ? 0.4 : 0.42)); Set(palette, "Selection", Mix(accent, background, isLight ? 0.35 : 0.2));
        Set(palette, "StatusChipBg", Mix(surface, text, isLight ? 0.04 : 0.06)); Set(palette, "StatusChipText", Mix(text, background, 0.26));
        Set(palette, "OfficialPanel", Mix(surface, accent, isLight ? 0.025 : 0.035)); Set(palette, "OfficialBorder", Mix(border, accent, 0.13));
        Set(palette, "InfoIconBg", Mix(surface, accent, isLight ? 0.09 : 0.13)); Set(palette, "StatusPanelBg", Mix(surface, accent, isLight ? 0.025 : 0.045));
        Set(palette, "StatusPanelBorder", Mix(border, accent, 0.12)); Set(palette, "StatusPanelText", Mix(text, accent, 0.18));
        Set(palette, "TitleStatusText", muted); Set(palette, "LoadingCaption", muted); Set(palette, "LoadingDot", Mix(background, text, 0.3));
        Set(palette, "SetupHint", muted); Set(palette, "LoadingTrack", Mix(background, text, isLight ? 0.13 : 0.15));
        Set(palette, "LogoBackdrop", Mix(background, accent, isLight ? 0.12 : 0.18));
    }

    public static double ContrastRatio(MediaColor first, MediaColor second)
    {
        var bright = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var dark = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (bright + 0.05) / (dark + 0.05);
    }

    public static double RelativeLuminance(MediaColor color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static bool IsUsableAccent(MediaColor accent, MediaColor background)
    {
        var channels = new[] { accent.R, accent.G, accent.B };
        var chroma = channels.Max() - channels.Min();
        return chroma >= 26 && ContrastRatio(accent, background) >= 1.65 && ContrastRatio(accent, background) <= 12;
    }

    private static MediaColor BestContrastingText(MediaColor background)
    {
        var black = MediaColor.FromRgb(12, 15, 20);
        var white = MediaColor.FromRgb(250, 251, 253);
        return ContrastRatio(black, background) >= ContrastRatio(white, background) ? black : white;
    }

    private static MediaColor ParseOr(string value, MediaColor fallback) => WebThemeMonitor.TryParseCssColor(value, out var parsed) ? parsed : fallback;
    private static void Set(System.Windows.ResourceDictionary dictionary, string key, MediaColor color) => dictionary[key] = new SolidColorBrush(color);
    private static MediaColor Mix(MediaColor from, MediaColor to, double amount) => MediaColor.FromRgb(
        (byte)Math.Clamp(Math.Round(from.R + (to.R - from.R) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(from.G + (to.G - from.G) * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(from.B + (to.B - from.B) * amount), 0, 255));
}
