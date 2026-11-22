using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace MacUI.Core;

/// <summary>主题:跟随系统深浅色,切换资源字典与玻璃色调。</summary>
public static class AppTheme
{
    private static bool? _dark;

    public static bool IsDark => (_dark ??= Detect());

    private static bool Detect()
    {
        return Cfg?.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersLight() == false,
        };
    }

    private static AppConfig? Cfg => App.Cfg;

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i != 0;
        }
        catch { return true; }
    }

    public static void SetMode(string mode)
    {
        _dark = mode switch { "dark" => true, "light" => false, _ => (bool?)null };
        Apply();
    }

    /// <summary>替换 Theme 主题字典并刷新各窗口玻璃色调。</summary>
    public static void Apply()
    {
        bool dark = IsDark;
        var uri = new Uri(dark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var merged = Application.Current.Resources.MergedDictionaries;
        // 第一个字典是 Theme
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);

        if (Application.Current != null) App.RefreshGlass();
    }

    /// <summary>真正切换 Windows 系统深/浅色模式(注册表 + 广播 ImmersiveColorSet)。</summary>
    public static void SetSystemLightMode(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            if (key != null)
            {
                key.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            }
            Native.BroadcastSettingChange("ImmersiveColorSet");
        }
        catch { /* 尽力而为 */ }
    }

    public static Color GlassTint(bool dark, double alphaScale = 1.0)
    {
        return dark
            ? Color.FromArgb((byte)Math.Clamp(0xC4 * alphaScale, 0, 255), 0x20, 0x22, 0x28)   // 深色玻璃
            : Color.FromArgb((byte)Math.Clamp(0xF0 * alphaScale, 0, 255), 0xFF, 0xFF, 0xFF);  // 浅色玻璃(纯白低透明)
    }
}
