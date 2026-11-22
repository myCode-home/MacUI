using System;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacUI.Core;

namespace MacUI.Windows;

/// <summary>
/// 顶部状态栏(macOS 菜单栏风格,触顶呼出式):
/// 平时完全隐藏;鼠标顶到屏幕上缘时从顶部滑出(全宽毛玻璃),
/// 显示输入法 / 运行中应用图标 / 网络 / 音量 / 电量 / 时间;鼠标离开 2 秒自动收回。
/// </summary>
public partial class StatusBarWindow : Window
{
    private static readonly CultureInfo Zh = new("zh-CN");
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _appsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private double _dpi = 1.0;
    private bool _shown;
    private DateTime? _retractSince;

    public StatusBarWindow()
    {
        InitializeComponent();
        Top = -Height - 8;   // 藏在屏幕上方
        _clockTimer.Tick += (_, _) => { UpdateClock(); UpdateIme(); UpdateVolume(); };
        _slowTimer.Tick += (_, _) => { UpdateBattery(); UpdateNetwork(); };
        _appsTimer.Tick += (_, _) => RefreshAppsRow();
        _pollTimer.Tick += (_, _) => PollCursor();
        // 窗口常驻隐藏(不 Show),轮询必须在构造时就启动
        _clockTimer.Start();
        _slowTimer.Start();
        _appsTimer.Start();
        _pollTimer.Start();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _dpi = (HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11) ?? 1.0;
        Native.EnableAcrylic(hwnd, AppTheme.GlassTint(AppTheme.IsDark, 0.78));
        Native.DarkTitle(hwnd, AppTheme.IsDark);
        Native.MakeNoActivate(hwnd);

        Width = SystemParameters.PrimaryScreenWidth;
        Left = 0;
        Top = -Height - 8;

        UpdateClock();
        UpdateBattery();
        UpdateNetwork();
        UpdateIme();
        UpdateVolume();
        RefreshAppsRow();
    }

    /// <summary>主题切换后重涂玻璃。</summary>
    public void RepaintGlass()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.EnableAcrylic(hwnd, AppTheme.GlassTint(AppTheme.IsDark, 0.78));
        Native.DarkTitle(hwnd, AppTheme.IsDark);
    }

    /// <summary>某个模块要求状态栏保持显示(如控制中心展开时)。</summary>
    public void RevealFor(string reason)
    {
        _revealReasons.Add(reason);
        if (!_shown) SlideDown();
        else _retractSince = null;
    }

    public void ReleaseReveal(string reason)
    {
        _revealReasons.Remove(reason);
    }

    private readonly System.Collections.Generic.HashSet<string> _revealReasons = new();

    // ---------------- 触顶呼出 / 离开收回 ----------------

    private void PollCursor()
    {
        if (!Native.GetCursorPos(out var p)) return;
        double dpi = _dpi > 0 ? _dpi : 1.0;
        double cy = p.Y / dpi, cx = p.X / dpi;

        // 右上角 10% 屏宽是窗口关闭/最小化/最大化按钮的区域:
        // 仅在「隐藏状态」下阻止呼出(防止贴过去盖住按钮);
        // 已展开时鼠标只要还悬停在状态栏上,面板就一直保持显示
        double excludeFrom = SystemParameters.PrimaryScreenWidth * 0.90;

        if (!_shown)
        {
            // 鼠标顶到屏幕最上缘 → 滑出(右上角排除区除外)
            if (cy <= 4 && cx < excludeFrom) SlideDown();
            return;
        }

        // 控制中心展开期间状态栏常显:鼠标从状态栏下移到面板时不能把状态栏收走
        if (App.IsControlCenterOpen || _revealReasons.Count > 0) { _retractSince = null; return; }

        bool inside = cy >= Top - 6 && cy <= Top + Height + 24;   // 全宽,只看纵向
        if (inside) { _retractSince = null; return; }
        _retractSince ??= DateTime.UtcNow;
        int delay = App.Cfg?.StatusBarAutoHideDelay ?? 600;
        if ((DateTime.UtcNow - _retractSince.Value).TotalMilliseconds > delay) SlideUp();
    }

    private void SlideDown()
    {
        _shown = true;
        _retractSince = null;
        Show();
        Visibility = Visibility.Visible;
        Topmost = true;
        // 滑出瞬间强制刷新一轮(隐藏期间所有刷新都是暂停的)
        UpdateClock();
        UpdateBattery();
        UpdateNetwork();
        UpdateIme();
        UpdateVolume();
        RefreshAppsRow();
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        BeginAnimation(TopProperty, anim);
    }

    private void SlideUp()
    {
        _shown = false;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(-Height - 8, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => { if (!_shown) Hide(); };
        BeginAnimation(TopProperty, anim);
    }

    // ---------------- 内容刷新 ----------------

    private void UpdateClock()
    {
        if (!_shown) return;   // 隐藏期间暂停刷新(AttachThreadInput 等会干扰前台应用焦点)
        var now = DateTime.Now;
        TimeText.Text = $"{now:M月d日} {now.ToString("ddd", Zh)}  {now:HH:mm}";
    }

    private void UpdateBattery()
    {
        if (!_shown) return;
        var b = SystemStats.GetBattery();
        if (!b.HasBattery) { BatteryText.Text = "AC"; BatteryIcon.Text = "\uE856"; return; }
        if (b.Percent < 0) { BatteryText.Text = "?"; BatteryIcon.Text = "\uE850"; return; }
        BatteryText.Text = $"{b.Percent}%{(b.AcOnline ? " ⚡" : "")}";
        BatteryIcon.Text = ((char)(0xE850 + Math.Min(b.Percent / 10, 10))).ToString();
    }

    private void UpdateNetwork()
    {
        if (!_shown) return;
        bool online = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        NetIcon.Text = online ? "\uE701" : "\uE839";
        NetIcon.Foreground = online
            ? (Brush)FindResource("PrimaryTextBrush")
            : (Brush)FindResource("DangerBrush");
    }

    private void UpdateIme()
    {
        if (!_shown) return;   // AttachThreadInput 会干扰前台输入队列,只在滑出时调用
        ImeText.Text = Native.GetForegroundIme();
    }

    private void UpdateVolume()
    {
        if (!_shown) return;
        VolumeIcon.Text = "\uE767";
        int vol = Audio.GetVolumePercent();
        VolumeText.Text = vol >= 0 ? $"{vol}%" : "--";
    }

    private void RefreshAppsRow()
    {
        if (!_shown) return;   // 隐藏期间不重建(避免无谓开销)
        var apps = RunningApps.GetRunningApps(includeMinimized: true);
        AppsRow.Children.Clear();
        int shown = 0;
        foreach (var app in apps)
        {
            if (shown >= 14) break;
            var hIcon = Native.GetWindowIcon(app.Hwnd);
            var img = new Image
            {
                Width = 22, Height = 22,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = app,
                ToolTip = app.Title.Length > 0 ? app.Title : app.Process,
            };
            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    img.Source = bmp;
                }
                catch { /* 忽略 */ }
            }
            img.MouseLeftButtonUp += (_, _) => ActivateApp(app);
            AppsRow.Children.Add(img);
            shown++;
        }
    }

    private void ActivateApp(RunningApp app)
    {
        try
        {
            if (Native.IsIconic(app.Hwnd)) Native.ShowWindow(app.Hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(app.Hwnd);
        }
        catch { /* 目标可能已退出 */ }
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
        => App.ShowSettings();

    private void CcBtn_Click(object sender, RoutedEventArgs e)
        => App.ToggleControlCenter();
}
