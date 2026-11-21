using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MacUI.Core;

namespace MacUI.Windows;

/// <summary>
/// 控制中心:从状态栏按钮呼出的单层圆角毛玻璃面板。
/// 内容:时钟 / 音量滑条(真实主音量)/ 系统深浅色切换 / 网络设置入口 / 显示桌面 / 电量。
/// 鼠标离开面板且不在状态栏上时自动收回;不抢焦点、不影响任何操作。
/// </summary>
public partial class ControlCenterWindow : Window
{
    private const int RadiusDip = 22;       // 面板圆角(窗口区域裁切)
    private const double PanelRight = 10;   // 距屏幕右缘

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _netTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _dismissPoll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double _dpi = 1.0;
    private bool _isOut;
    private bool _initVolume;
    private DateTime? _outsideSince;
    private IntPtr _hwnd;

    /// <summary>面板是否已展开(状态栏据此避免悬停收回竞态)。</summary>
    public bool IsOut => _isOut;

    public ControlCenterWindow()
    {
        InitializeComponent();
        UpdateClock();
        UpdateBattery();
        UpdateNet();
        UpdateThemeTile();

        _clockTimer.Tick += (_, _) => UpdateClock();
        _netTimer.Tick += (_, _) => UpdateNet();
        _batteryTimer.Tick += (_, _) => UpdateBattery();

        VolSlider.ValueChanged += (_, _) =>
        {
            int v = (int)Math.Round(VolSlider.Value);
            VolText.Text = v + "%";
            if (_initVolume) return;
            Audio.SetVolumePercent(v);   // 真实系统主音量(IAudioEndpointVolume)
        };

        // 初始:藏在状态栏后方(屏幕外)
        Left = SystemParameters.PrimaryScreenWidth - Width - PanelRight;
        Top = -900;

        _clockTimer.Start();
        _netTimer.Start();
        _batteryTimer.Start();
        _dismissPoll.Tick += (_, _) => PollDismiss();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _dpi = (HwndSource.FromHwnd(_hwnd)?.CompositionTarget?.TransformToDevice.M11) ?? 1.0;
        Native.MakeNoActivate(_hwnd);
        RepaintGlass();
        // 内容高度由 SizeToContent 决定,量到真实高度后重裁圆角
        SizeChanged += (_, _) =>
        {
            Native.RefreshRegion(this, _hwnd, RadiusDip);
            UpdateBattery();
        };

        _initVolume = true;
        int vol = Audio.GetVolumePercent();
        VolSlider.Value = vol >= 0 ? vol : 50;
        VolText.Text = (vol >= 0 ? vol : 50) + "%";
        _initVolume = false;
    }

    /// <summary>重涂玻璃(主题切换后调用)。圆角靠 SetWindowRgn 裁切,避免双层边框。</summary>
    public void RepaintGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ApplyGlassPanel(this, AppTheme.GlassTint(AppTheme.IsDark, 0.92), RadiusDip);
    }

    // ---------------- 滑出 / 收回 ----------------

    public void SlideOut()
    {
        if (_isOut) { SlideIn(); return; }
        _isOut = true;
        Show();
        Visibility = Visibility.Visible;
        Topmost = true;
        UpdateLayout();   // SizeToContent:先量出真实高度,再算滑入起点

        Left = SystemParameters.PrimaryScreenWidth - Width - PanelRight;
        double h = ActualHeight > 0 ? ActualHeight : 380;
        double from = 30 - h;                  // 从状态栏后方滑出
        double target = 30 + 8;                // 状态栏(30 DIP)下方留 8
        Top = from;
        var anim = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(TopProperty, anim);

        // 通知状态栏:面板展开期间它必须保持可见
        App.SetControlCenterOpen(true);
        _outsideSince = null;
        _dismissPoll.Start();
    }

    public void SlideIn()
    {
        if (!_isOut) return;
        _isOut = false;
        _dismissPoll.Stop();
        App.SetControlCenterOpen(false);
        var anim = new DoubleAnimation(-ActualHeight - 24, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
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
        bool inside = cx >= Left - 8 && cx <= Left + Width + 8
                   && cy >= Top - 8 && cy <= Top + ActualHeight + 8;
        // 鼠标回到顶部状态栏也算"仍在使用"(状态栏本身由它自己管理收回)
        bool onBar = cy >= 0 && cy <= 32;
        if (inside || onBar) { _outsideSince = null; return; }
        _outsideSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _outsideSince.Value).TotalMilliseconds > 900) SlideIn();
    }

    // ---------------- 内容 ----------------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        CcTime.Text = now.ToString("HH:mm");
        CcDate.Text = now.ToString("M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));
    }

    private void UpdateNet()
    {
        bool ok = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        int adapters = 0;
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                && ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                adapters++;
        NetText.Text = ok ? "已连接" : "未连接";
        NetHint.Text = ok ? $"{adapters} 个适配器 · 点击查看" : "点击检查网络";
    }

    private void UpdateBattery()
    {
        var b = SystemStats.GetBattery();
        if (!b.HasBattery)
        {
            BatText.Text = "AC 供电";
            BatIcon.Text = "\uE856";
            BatChip.Background = (Brush)FindResource("SysBlue");
            BatBar.Width = 0;
            return;
        }
        if (b.Percent < 0) { BatText.Text = "?"; BatBar.Width = 0; return; }

        BatText.Text = $"{b.Percent}%{(b.AcOnline ? " ⚡" : "")}";
        BatIcon.Text = ((char)(0xE850 + Math.Min(b.Percent / 10, 10))).ToString();
        BatChip.Background = b.Percent <= 20
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : (Brush)FindResource("SysGreen");

        // 满电时进度条铺满整行(之前末尾固定留 10px 空隙)
        double w = BatFrame.ActualWidth;
        if (w <= 0) { w = Math.Max(0, Width - 2 * 15 - 2 * 4 - 2 * 13 - (ActualWidth > 0 ? 0 : 0)); }
        BatBar.Width = Math.Max(0, w * b.Percent / 100.0);
    }

    private void UpdateThemeTile() => ThemeState.Text = AppTheme.IsDark ? "深色" : "浅色";

    // ---------------- 交互 ----------------

    private void ThemeTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 真正切换 Windows 系统深/浅色(注册表 + 广播 ImmersiveColorSet),我方主题随动
        bool toLight = AppTheme.IsDark;
        AppTheme.SetSystemLightMode(toLight);
        App.Cfg!.Theme = toLight ? "light" : "dark";
        App.Cfg.Save();
        App.ApplyConfig();
        UpdateThemeTile();
        RepaintGlass();
    }

    private void NetTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Windows 11 网络适配器页(旧版回落到网络状态页)
        App.ShellOpen("ms-settings:network-advancedsettings");
    }

    private void DesktopTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            shell.ToggleDesktop();
        }
        catch { /* 忽略 */ }
    }
}
