using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MacUI.Core;

namespace MacUI.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.DarkTitle(hwnd, AppTheme.IsDark);
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var cfg = App.Cfg!;
        ChkStatus.IsChecked = cfg.StatusBarEnabled;
        ChkSpotlight.IsChecked = cfg.SpotlightEnabled;
        HotkeyBox.Text = HotkeyHelper.Normalize(cfg.Hotkey);
        ThemeBox.SelectedIndex = cfg.Theme switch { "dark" => 1, "light" => 2, _ => 0 };
        DelayBox.Text = cfg.StatusBarAutoHideDelay.ToString();
        ChkAutostart.IsChecked = cfg.StartWithWindows;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Cfg!;
        cfg.StatusBarEnabled = ChkStatus.IsChecked == true;
        cfg.SpotlightEnabled = ChkSpotlight.IsChecked == true;

        var hk = HotkeyBox.Text.Trim();
        if (HotkeyHelper.Parse(hk).Ok) cfg.Hotkey = HotkeyHelper.Normalize(hk);

        cfg.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        if (int.TryParse(DelayBox.Text.Trim(), out int delayMs))
            cfg.StatusBarAutoHideDelay = Math.Clamp(delayMs, 200, 10000);
        cfg.StartWithWindows = ChkAutostart.IsChecked == true;

        App.SetAutostart(cfg.StartWithWindows);
        App.ApplyConfig();
        cfg.Save();
        MessageBox.Show(this, "已应用并保存。", "MacUI", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
