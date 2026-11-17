using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacUI.Core;

namespace MacUI.Windows;

/// <summary>
/// 控制中心:从状态栏按钮呼出的毛玻璃弹出面板(macOS 控制中心风格)。
/// 内容:时钟 / 音量滑条(实时生效)/ 主题切换 / 网络状态 / 显示桌面 / 电量。
/// 鼠标离开面板 1.2 秒自动收回;不抢焦点、不影响任何操作。
/// </summary>
public partial class ControlCenterWindow : Window
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _netTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _dismissPoll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _dpi = 1.0;
    private bool _isOut;
    private bool _initVolume;
    private DateTime? _outsideSince;

    public ControlCenterWindow()
    {
        InitializeComponent();
        CcDate.Text = DateTime.Now.ToString("yyyy年M月d日 dddd");
        UpdateBattery();
        UpdateNet();
        UpdateThemeButton();

        _clockTimer.Tick += (_, _) => UpdateClock();
        _netTimer.Tick += (_, _) => UpdateNet();
        _batteryTimer.Tick += (_, _) => UpdateBattery();

        VolSlider.ValueChanged += (_, _) =>
        {
            int v = (int)VolSlider.Value;
            VolText.Text = v + "%";
            if (_initVolume) return;
            uint raw = (uint)(v / 100.0 * 65535);
            uint both = (raw << 16) | raw;
            Native.WaveOutSetVolume(0xFFFFFFFF, both);
        };
        ThemeBtn.Click += (_, _) =>
        {
            string next = AppTheme.IsDark ? "light" : "dark";
            App.Cfg!.Theme = next;
            App.Cfg.Save();
            App.ApplyConfig();
            UpdateThemeButton();
        };
        DesktopBtn.Click += (_, _) => ToggleDesktop();

        // 初始:藏在状态栏后方(屏幕外)
        Left = SystemParameters.PrimaryScreenWidth - Width - 8;
        Top = -ActualHeight - 8;

        // 面板可见时才需要这些定时器;但轮询常驻(判定点击外部收回)
        _clockTimer.Start();
        _netTimer.Start();
        _batteryTimer.Start();
        _dismissPoll.Tick += (_, _) => PollDismiss();
        _dismissPoll.Start();
        UpdateClock();
        Loaded += OnLoaded;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        CcTime.Text = now.ToString("HH:mm");
        CcDate.Text = now.ToString("yyyy年M月d日 dddd");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _dpi = (HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11) ?? 1.0;
        Native.EnableAcrylic(hwnd, AppTheme.GlassTint(AppTheme.IsDark, 0.85));
        Native.RoundCorners(hwnd, round: true);
        Native.MakeNoActivate(hwnd);

        _initVolume = true;
        VolSlider.Value = Audio.GetVolumePercent();
        _initVolume = false;
    }

    // ---------------- 滑出 / 收回 ----------------

    public void SlideOut()
    {
        if (_isOut) { SlideIn(); return; }
        _isOut = true;
        Show();
        Visibility = Visibility.Visible;
        Topmost = true;

        Left = SystemParameters.PrimaryScreenWidth - Width - 8;
        Top = -ActualHeight - 8;
        double target = 34;   // 状态栏(30 DIP)下方
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        BeginAnimation(TopProperty, anim);
        _outsideSince = null;
        _dismissPoll.Start();
    }

    public void SlideIn()
    {
        if (!_isOut) return;
        _isOut = false;
        _dismissPoll.Stop();
        var anim = new DoubleAnimation(-ActualHeight - 8, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => { if (!_isOut) Hide(); };
        BeginAnimation(TopProperty, anim);
    }

    public void Toggle() { if (_isOut) SlideIn(); else SlideOut(); }

    private void PollDismiss()
    {
        if (!_isOut) return;
        if (!Native.GetCursorPos(out var p)) return;
        double dpi = _dpi > 0 ? _dpi : 1.0;
        double cx = p.X / dpi, cy = p.Y / dpi;
        bool inside = cx >= Left - 16 && cx <= Left + Width + 16
                   && cy >= Top - 16 && cy <= Top + ActualHeight + 16;
        // 鼠标在状态栏区域(顶部 30 DIP 内)也算"仍在使用"
        bool onBar = cy >= 0 && cy <= 34;
        if (inside || onBar) { _outsideSince = null; return; }
        _outsideSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _outsideSince.Value).TotalMilliseconds > 1200) SlideIn();
    }

    // ---------------- 内容 ----------------

    private void UpdateNet()
    {
        bool ok = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        int adapters = 0;
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                && ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                adapters++;
        NetText.Text = ok ? $"已连接({adapters} 个适配器)" : "未连接";
    }

    private void UpdateBattery()
    {
        var b = SystemStats.GetBattery();
        if (!b.HasBattery) { BatText.Text = "AC 供电"; BatIcon.Text = "\uE856"; BatBar.Width = 0; return; }
        if (b.Percent < 0) { BatText.Text = "?"; BatBar.Width = 0; return; }
        BatText.Text = $"{b.Percent}%{(b.AcOnline ? " ⚡" : "")}";
        BatBar.Width = (BatFrame.ActualWidth > 10 ? BatFrame.ActualWidth - 10 : 280) * b.Percent / 100.0;
    }

    private void UpdateThemeButton()
    {
        ThemeBtn.Content = AppTheme.IsDark ? "切换到浅色" : "切换到深色";
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        string next = AppTheme.IsDark ? "light" : "dark";
        App.Cfg!.Theme = next;
        App.Cfg.Save();
        App.ApplyConfig();
        UpdateThemeButton();
    }

    private void DesktopBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            shell.ToggleDesktop();
        }
        catch { /* 忽略 */ }
    }

    private void ToggleDesktop()
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            shell.ToggleDesktop();
        }
        catch { /* 忽略 */ }
    }
}
