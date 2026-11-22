using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacUI.Core;

namespace MacUI.Windows;

/// <summary>
/// 控制中心:iOS 风格单层圆角磨砂面板,从状态栏下方「飞出 + 缩放」展开。
/// 内容:时钟 / 音量滑条(真实主音量,点击定位)/ 系统深浅色 / 网络设置 / 显示桌面 / 电量。
/// 鼠标移出 0.3 秒自动收回;面板展开期间状态栏保持可见。
///
/// 动画用手动逐帧驱动而不是 WPF 属性动画:实测 Window.Height 的 BeginAnimation
/// 在 WindowChrome 窗口上不生效(只有 Width/Left/Top 动),逐帧 Set 尺寸 100% 可靠。
/// </summary>
public partial class ControlCenterWindow : Window
{
    private const int RadiusDip = 24;          // 面板圆角(窗口区域裁切)
    private const double ContentWidth = 340;   // 内容基准宽度(Viewbox 缩放的基准)
    private const double PanelRight = 10;      // 距屏幕右缘
    private const double TopTarget = 38;       // 状态栏(30 DIP)下方留 8
    private const double OpenScale = 0.88;     // 展开起始缩放
    private const double CloseScale = 0.90;    // 收起终止缩放
    private const double FlyOffset = 56;       // 从顶部飞出的起始偏移(DIP)

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _netTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _batteryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _dismissPoll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _dpi = 1.0;
    private bool _isOut;
    private bool _initVolume;
    private DateTime? _outsideSince;
    private IntPtr _hwnd;
    private double _contentH;

    // ---- 手动动画状态 ----
    private readonly Stopwatch _animClock = new();
    private bool _animating;
    private bool _opening;
    private double _animMs = 1;
    private double _startScale = 1;
    private double _currentScale = 1;
    private bool _renderHooked;

    /// <summary>面板是否已展开(状态栏据此避免悬停收回竞态)。</summary>
    public bool IsOut => _isOut;

    public ControlCenterWindow()
    {
        InitializeComponent();

        // 先量出内容高度:窗口尺寸动画需要确定的基准尺寸
        Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        _contentH = Math.Ceiling(Root.DesiredSize.Height);
        // 不设 WPF Width/Height:尺寸完全由原生 SetWindowPos 控制,
        // 否则布局回合同步会把窗口尺寸拉回 WPF 值,导致动画抖动
        Width = double.NaN;
        Height = double.NaN;

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

        // 初始:完整尺寸,但藏在屏幕上方(不可见)
        Left = SystemParameters.PrimaryScreenWidth - ContentWidth - PanelRight;
        Top = -_contentH - 80;

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
        // 字体/DPI 在 Loaded 后才最终确定:重新量一次内容高度,避免内容被纵向压扁
        RemeasureContent();
        ApplyVisual(1.0, 0);

        _initVolume = true;
        int vol = Audio.GetVolumePercent();
        VolSlider.Value = vol >= 0 ? vol : 50;
        VolText.Text = (vol >= 0 ? vol : 50) + "%";
        _initVolume = false;
    }

    /// <summary>重新测量内容自然高度(窗口固定高度 + 内容缩放动画的基准)。</summary>
    private void RemeasureContent()
    {
        Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        double h = Math.Ceiling(Root.DesiredSize.Height);
        if (h > 40) _contentH = h;
    }

    /// <summary>重涂玻璃(主题切换后调用)。
    /// 用纯模糊(不带系统 tint):Win11 上 Acrylic(4) 会忽略自定义 tint 只给固定灰白材质,
    /// 浅色模式因此变成一块不透明白板。纯 blur(3) 是真正透明的模糊,
    /// 颜色由 XAML 的 PanelBgBrush(半透明底色)控制 —— 两种主题都能透出壁纸。</summary>
    public void RepaintGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.EnablePlainBlur(_hwnd);
        Native.RemoveWindowBorder(_hwnd);
        Native.RoundCorners(_hwnd, round: false);
        ApplyVisual(1.0, 0);
    }

    // ---------------- 飞出 + 缩放动画 ----------------

    /// <summary>
    /// 应用一帧视觉状态:原生 SetWindowPos 改窗口真实尺寸(右上缘锚定),Viewbox 等比缩放内容。
    /// DWM 磨砂跟随窗口矩形,所以玻璃必然跟着缩放。
    /// </summary>
    private void ApplyVisual(double s, double topOffset = 0)
    {
        _currentScale = s;
        if (_hwnd == IntPtr.Zero) return;
        double dpi = _dpi > 0 ? _dpi : 1.0;
        double wDip = ContentWidth * s;
        double hDip = _contentH * s;
        double leftDip = SystemParameters.PrimaryScreenWidth - wDip - PanelRight;
        double topDip = TopTarget + topOffset;

        int wPx = Math.Max(8, (int)Math.Round(wDip * dpi));
        int hPx = Math.Max(8, (int)Math.Round(hDip * dpi));
        int xPx = (int)Math.Round(leftDip * dpi);
        int yPx = (int)Math.Round(topDip * dpi);

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, xPx, yPx, wPx, hPx,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        int rPx = Math.Max(4, (int)Math.Round(RadiusDip * s * dpi));
        Native.SetRoundRegion(_hwnd, wPx, hPx, rPx);
    }

    public void SlideOut()
    {
        if (_isOut) { SlideIn(); return; }
        if (_animating && _opening) return;

        _isOut = true;
        bool interrupted = _animating;
        if (!interrupted) RemeasureContent();

        Show();
        Visibility = Visibility.Visible;
        Topmost = true;

        double startScale = OpenScale;
        ApplyVisual(startScale, -FlyOffset * (interrupted ? 1 : 1));

        App.SetControlCenterOpen(true);
        _outsideSince = null;
        _dismissPoll.Start();

        _startScale = startScale;
        _opening = true;
        _animMs = 380;
        _animClock.Restart();
        _animating = true;
        if (!_renderHooked) { CompositionTarget.Rendering += OnFrame; _renderHooked = true; }
    }

    public void SlideIn()
    {
        if (!_isOut) return;
        if (_animating && !_opening) return;

        _isOut = false;
        _dismissPoll.Stop();
        App.SetControlCenterOpen(false);

        _startScale = _currentScale;
        _opening = false;
        _animMs = 150;
        _animClock.Restart();
        _animating = true;
        if (!_renderHooked) { CompositionTarget.Rendering += OnFrame; _renderHooked = true; }
    }

    public void Toggle() { if (_isOut) SlideIn(); else SlideOut(); }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_animating) { UnhookFrames(); return; }

        double t = Math.Clamp(_animClock.Elapsed.TotalMilliseconds / _animMs, 0, 1);

        if (_opening)
        {
            // 缩放:0.88 → 1(轻微回弹);同时从顶部滑入
            double s = _startScale + (1 - _startScale) * EaseOutBack(t, 1.05);
            double topOff = -FlyOffset * (1 - EaseOutCubic(t));
            ApplyVisual(s, topOff);

            if (t >= 1)
            {
                ApplyVisual(1.0, 0);
                _animating = false;
                UnhookFrames();
            }
        }
        else
        {
            // 收起:缩到 0.9 + 上移 + 结束隐藏
            double s = _startScale - (_startScale - CloseScale) * EaseInCubic(t);
            double topOff = -40 * EaseInCubic(t);
            ApplyVisual(s, topOff);

            if (t >= 1)
            {
                _animating = false;
                UnhookFrames();
                Hide();
                ApplyVisual(1.0, 0);   // 复位,下次展开从干净状态开始
            }
        }
    }

    private void UnhookFrames()
    {
        if (!_renderHooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _renderHooked = false;
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseInCubic(double t) => t * t * t;

    /// <summary>轻微回弹缓动:结束时略超过 1 再回落(c1 控制回弹幅度)。</summary>
    private static double EaseOutBack(double t, double c1)
    {
        double c3 = c1 + 1;
        double u = t - 1;
        return 1 + c3 * u * u * u + c1 * u * u;
    }

    private void PollDismiss()
    {
        if (!_isOut) return;
        if (!Native.GetCursorPos(out var p)) return;
        if (!Native.GetWindowRect(_hwnd, out var r)) return;

        // 全部用物理像素比较:鼠标位置与窗口矩形同坐标系
        bool inside = p.X >= r.Left - 12 && p.X <= r.Right + 12
                   && p.Y >= r.Top - 12 && p.Y <= r.Bottom + 12;
        // 鼠标回到顶部状态栏也算"仍在使用"
        double dpi = _dpi > 0 ? _dpi : 1.0;
        double cy = p.Y / dpi;
        bool onBar = cy >= 0 && cy <= 32;
        if (inside || onBar) { _outsideSince = null; return; }
        _outsideSince ??= DateTime.UtcNow;
        // 用户要求:移出 0.3 秒即收起
        if ((DateTime.UtcNow - _outsideSince.Value).TotalMilliseconds > 300) SlideIn();
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

        // 满电时进度条铺满整行
        double w = BatFrame.ActualWidth;
        if (w <= 0) w = ContentWidth - 2 * 15 - 2 * 4 - 2 * 13;
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
