using Microsoft.Win32;

namespace DeepSeekHarnessDesktop.Services;

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
}
