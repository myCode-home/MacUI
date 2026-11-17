using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacUI.Core;

namespace MacUI.Windows;

/// <summary>
/// macOS 风格前台调度(Cmd+Tab 的 Ctrl+Tab 版):
/// Ctrl+Tab 打开居中应用条并选中下一个应用;按住 Ctrl 连按 Tab 循环、
/// Shift+Tab 反向;**鼠标悬停在某个图标上也会选中它**;
/// 松开 Ctrl 切换到当前选中(悬停优先)的应用;Esc 取消。
/// 面板为极低透明度的深色 frost,不遮背景。
/// </summary>
public partial class AppSwitcherWindow : Window
{
    private static AppSwitcherWindow? _current;
    private readonly DispatcherTimer _ctrlPoll;
    private List<RunningApp> _apps = new();
    private List<Border> _slots = new();
    private int _selected;
    private bool _prevLeft, _prevRight;

    public AppSwitcherWindow()
    {
        InitializeComponent();
        _ctrlPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _ctrlPoll.Tick += (_, _) =>
        {
            // 松开 Ctrl → 提交切换(macOS 手感)
            if ((Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) == 0) { Commit(); return; }

            // 左右方向键切换选择(边沿触发,面板不持焦点也能全局响应)
            bool left = (Native.GetAsyncKeyState(0x25) & 0x8000) != 0;   // VK_LEFT
            bool right = (Native.GetAsyncKeyState(0x27) & 0x8000) != 0;  // VK_RIGHT
            if (right && !_prevRight) Advance(+1);
            if (left && !_prevLeft) Advance(-1);
            _prevRight = right;
            _prevLeft = left;
        };
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // 极低透明度的深色 frost(约 16%),毛玻璃透出背景
            Native.EnableAcrylic(hwnd, Color.FromArgb(0x2A, 0x10, 0x10, 0x10));
            Native.RoundCorners(hwnd, round: true);
            Native.MakeNoActivate(hwnd);
        };
    }

    public static void HandleHotkey(bool backwards)
    {
        if (_current == null)
        {
            _current = new AppSwitcherWindow();
            _current.Open(backwards);
        }
        else _current.Advance(backwards ? -1 : +1);
    }

    private void Open(bool backwards)
    {
        _apps = RunningApps.GetRunningApps(includeMinimized: true);
        if (_apps.Count == 0) return;

        // 选中起点:当前前台应用的下一个(macOS 行为)
        var fg = Native.GetForegroundWindow();
        int fgIndex = _apps.FindIndex(a => a.Hwnd == fg);
        _selected = _apps.Count > 1
            ? (fgIndex >= 0 ? (fgIndex + 1) % _apps.Count : 0)
            : 0;

        double w = Math.Min(_apps.Count * 70 + 32, 900);
        Width = w;
        Left = (SystemParameters.PrimaryScreenWidth - w) / 2;
        Top = SystemParameters.PrimaryScreenHeight * 0.42;
        Opacity = 0;
        Show();
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, anim);
        Rebuild();
        _ctrlPoll.Start();
    }

    private void Advance(int delta)
    {
        if (_apps.Count == 0) return;
        _selected = ((_selected + delta) % _apps.Count + _apps.Count) % _apps.Count;
        UpdateSelectionVisuals();
    }

    private void Commit()
    {
        _ctrlPoll.Stop();
        var app = _selected >= 0 && _selected < _apps.Count ? _apps[_selected] : null;
        CloseSelf();
        if (app == null) return;
        try
        {
            if (Native.IsIconic(app.Hwnd)) Native.ShowWindow(app.Hwnd, Native.SW_RESTORE);
            Native.SetForegroundWindow(app.Hwnd);
        }
        catch { /* 目标可能已退出 */ }
    }

    private void CloseSelf()
    {
        _ctrlPoll.Stop();
        Hide();
        _current = null;
    }

    // ---------------- 条目构建 / 悬停选中 ----------------

    private void Rebuild()
    {
        Row.Children.Clear();
        _slots.Clear();
        for (int i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            int index = i;

            var icon = new Image
            {
                Width = 46, Height = 46,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var hIcon = Native.GetWindowIcon(app.Hwnd);
            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    icon.Source = bmp;
                }
                catch { /* 图标提取失败则留空 */ }
            }

            var label = new TextBlock
            {
                // 常驻占位(空文本保留高度),选中时显示进程名
                Text = "",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 62,
            };
            var slot = new Border
            {
                Width = 64,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4, 6, 4, 3),
                Background = Brushes.Transparent,
                Child = new StackPanel { Children = { icon, label } },
                Cursor = Cursors.Hand,
                Tag = app,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            slot.MouseEnter += (_, _) =>
            {
                // 鼠标悬停即选中:松开 Ctrl 就切到悬停的这个
                if (_selected != index) { _selected = index; UpdateSelectionVisuals(); }
            };
            slot.MouseLeftButtonUp += (_, _) => { _selected = index; Commit(); };
            Row.Children.Add(slot);
            _slots.Add(slot);
        }
        UpdateSelectionVisuals();
    }

    /// <summary>原地刷新选中态(不重建可视树,避免悬停抖动)。</summary>
    private void UpdateSelectionVisuals()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            bool selected = i == _selected;
            var slot = _slots[i];
            slot.Background = selected ? (Brush)FindResource("SelectedBrush") : Brushes.Transparent;
            slot.RenderTransform = selected ? new ScaleTransform(1.12, 1.12) : null;
            if (slot.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                tb.Text = selected ? ((RunningApp)slot.Tag).Title : "";
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseSelf(); e.Handled = true; }
    }

    private void OnDeactivated(object sender, EventArgs e) => CloseSelf();
}
