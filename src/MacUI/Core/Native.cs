using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacUI.Core;

/// <summary>全部 Win32 互操作 + 毛玻璃/圆角辅助。</summary>
internal static class Native
{
    // ---------- DWM ----------
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMCP_DONOTROUND = 1;
    public const int DWMCP_ROUND = 2;

    // ---------- Acrylic 模糊(SetWindowCompositionAttribute) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public uint AccentState; public uint AccentFlags; public uint GradientColor; public uint AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfObject; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int WCA_ACCENT_POLICY = 19;
    private const uint ACCENT_ENABLE_BLURBEHIND = 3;          // 纯模糊(无 tint,颜色由 WPF 层控制)
    private const uint ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;   // 带 tint 的 Acrylic

    /// <summary>给无边框窗口开启系统级 Acrylic 毛玻璃(带色调)。</summary>
    public static void EnableAcrylic(IntPtr hwnd, Color tint)
        => SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, tint, 2);

    /// <summary>
    /// 只做背景模糊、不加系统 tint。Win11 上 Acrylic(4) 会被系统按主题染成
    /// 灰白/灰黑,自定义透明度几乎不起作用;纯 blur(3) 则真实透出壁纸颜色,
    /// 再由 WPF 层的半透明底色控制观感 —— iOS 观感就用这条路径。
    /// </summary>
    public static void EnablePlainBlur(IntPtr hwnd)
        => SetAccent(hwnd, ACCENT_ENABLE_BLURBEHIND, Colors.Transparent, 0);

    private static void SetAccent(IntPtr hwnd, uint state, Color tint, uint flags)
    {
        var accent = new AccentPolicy
        {
            AccentState = state,
            AccentFlags = flags,
            // DWORD 格式为 0xAABBGGRR
            GradientColor = (uint)(tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R),
        };
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfObject = Marshal.SizeOf(accent),
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void RoundCorners(IntPtr hwnd, bool round)
    {
        int v = round ? DWMCP_ROUND : DWMCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
    }

    public static void DarkTitle(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4);
    }

    /// <summary>去掉 Win11 窗口的 DWM 外边框(panel 只保留内层圆角)。</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    public static void RemoveWindowBorder(IntPtr hwnd)
    {
        int none = unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, 4);
    }

    /// <summary>
    /// 用窗口区域把无边框窗口裁成单一圆角(物理像素)。
    /// 毛玻璃面板必须 AllowsTransparency=False 才能保留 Acrylic,于是 XAML 的 CornerRadius
    /// 无法裁窗形 —— 只剩 DWM 自带的 8px 圆角,看起来就是「外面还有一圈边框」。
    /// 这里用 SetWindowRgn 裁出与内层一致的圆角,外圈彻底消失。
    /// </summary>
    public static void SetRoundRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
        => SetRoundRegionAt(hwnd, 0, 0, widthPx, heightPx, radiusPx);

    /// <summary>同 <see cref="SetRoundRegion"/>,但可指定区域在窗口内的偏移(用于右侧锚定的缩放动画)。</summary>
    public static void SetRoundRegionAt(IntPtr hwnd, int x, int y, int widthPx, int heightPx, int radiusPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return;
        int d = Math.Max(0, radiusPx) * 2;
        // +1:CreateRoundRectRgn 的 right/bottom 是开区间,不加 1 会缺最右/最下一像素
        IntPtr rgn = CreateRoundRectRgn(x, y, x + widthPx + 1, y + heightPx + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        SetWindowRgn(hwnd, rgn, true);   // 成功后区域所有权移交系统,不要再 DeleteObject
    }

    /// <summary>毛玻璃面板统一外观:Acrylic + 去掉 DWM 外框与圆角 + 按实际尺寸裁圆角。
    /// 尺寸变化后需由调用方再调 <see cref="RefreshRegion"/>(SizeToContent 面板会变高)。</summary>
    public static void ApplyGlassPanel(Window window, Color tint, int radiusDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        EnableAcrylic(hwnd, tint);
        RemoveWindowBorder(hwnd);
        RoundCorners(hwnd, round: false);   // 关掉 DWM 圆角,交给 SetWindowRgn
        RefreshRegion(window, hwnd, radiusDip);
    }

    /// <summary>窗口尺寸变化后重裁圆角(WPF 的 DPI 已由 CreateRoundRectRgn 的物理像素补偿)。</summary>
    public static void RefreshRegion(Window window, IntPtr hwnd, int radiusDip)
    {
        if (hwnd == IntPtr.Zero) return;
        var src = HwndSource.FromHwnd(hwnd);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        int w = (int)Math.Round(window.ActualWidth * scale);
        int h = (int)Math.Round(window.ActualHeight * scale);
        SetRoundRegion(hwnd, w, h, (int)Math.Round(radiusDip * scale));
    }

    // ---------- 系统设置变更广播(切换深/浅色后通知所有窗口) ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public const uint WM_SETTINGCHANGE = 0x001A;

    public static void BroadcastSettingChange(string area)
    {
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, 2 /*SMTO_ABORTIFHUNG*/, 1500, out _);
    }

    // ---------- 全局热键 ----------
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    // ---------- 光标 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // ---------- 系统:CPU 时间 / 内存 / 电池 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint Low; public uint High; }

    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX b);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 1=已接通电源 0=电池
        public byte BatteryFlag;       // 128=无电池
        public byte BatteryLifePercent; // 0-100,255=未知
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS s);

    // ---------- 窗口枚举 / 激活 ----------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---------- 按键状态(App Switcher 松开检测) ----------
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    public const int VK_CONTROL = 0x11;

    // ---------- DWM 缩略图(实时窗口预览,Stage Manager 左条) ----------
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbId);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbId, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbId);
    public const uint DWM_TNP_RECTDESTINATION = 1;
    public const uint DWM_TNP_RECTSOURCE = 2;
    public const uint DWM_TNP_OPACITY = 4;
    public const uint DWM_TNP_VISIBLE = 8;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    // ---------- 应用窗口图标 ----------
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public const uint WM_GETICON = 0x7F;
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")] public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    // ---------- 输入法检测(取前台窗口的键盘布局) ----------
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);

    // 系统音量已改用 Core Audio(见 Core/Audio.cs);winmm waveOut 音量与真实主音量无关,不再使用

    /// <summary>前台窗口当前输入法:简体中文返回 "中",否则返回 "EN"。</summary>
    public static string GetForegroundIme()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            if (fgThread == 0) return "EN";
            uint cur = GetCurrentThreadId();
            bool attached = false;
            if (fgThread != cur) { attached = AttachThreadInput(cur, fgThread, true); }
            try { var hkl = GetKeyboardLayout(fgThread); return ((ushort)hkl & 0xFFFF) == 0x0804 ? "中" : "EN"; }
            finally { if (attached) AttachThreadInput(cur, fgThread, false); }
        }
        catch { return "EN"; }
    }

    /// <summary>取窗口自身图标(WM_GETICON → 类图标),失败返回 IntPtr.Zero。</summary>
    public static IntPtr GetWindowIcon(IntPtr hwnd)
    {
        IntPtr h = SendMessage(hwnd, WM_GETICON, (IntPtr)1, IntPtr.Zero);   // ICON_BIG
        if (h == IntPtr.Zero) h = SendMessage(hwnd, WM_GETICON, IntPtr.Zero, IntPtr.Zero); // ICON_SMALL
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICON);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICONSM);
        return h;
    }

    /// <summary>给窗口加 WS_EX_NOACTIVATE(点击不抢焦点,且从 Win+Tab 任务视图/Alt+Tab 中排除)。</summary>
    public static void MakeNoActivate(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
    }

    // 钉到桌面层(所有应用窗口之下、壁纸之上),对应 Rainmeter「桌面层」/ Windows 小工具
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOMOVE = 0x2;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const uint SWP_SHOWWINDOW = 0x40;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MINIMIZE = 0xF020;
    public const int SC_MASK = 0xFFF0;
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd; public IntPtr hwndInsertAfter;
        public int x, y, cx, cy; public uint flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const uint GW_HWNDPREV = 3;   // Z 序中的上一个(更高)
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] public static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);

    /// <summary>把顶层 WPF 窗口改成 Progman 的 WS_CHILD —— 真正嵌入桌面场景:
    /// 与桌面图标(DefView,同为 Progman 子窗口)同一合成树,「显示桌面」状态下照常渲染
    /// (Win11 25H2 的显示桌面是合成器级抑制,任何顶层窗口都会被隐藏,唯 shell 子窗口幸免)。
    /// 返回是否成功;Z 序置于桌面图标层之上。</summary>
    public static bool EmbedIntoDesktop(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
        SetParent(hwnd, progman);
        var defview = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        SetWindowPos(hwnd, defview != IntPtr.Zero ? defview : IntPtr.Zero,
            0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return true;
    }

    /// <summary>子窗口圆角(替代 DWM 圆角,子窗口不适用)。</summary>
    public static void RoundChild(IntPtr hwnd, double widthDip, double heightDip, double dpi, double radiusDip)
    {
        int w = (int)Math.Round(widthDip * dpi), h = (int)Math.Round(heightDip * dpi);
        int r = (int)Math.Round(radiusDip * dpi);
        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w, h, r, r), true);
    }

    /// <summary>钩子内拒绝 SC_MINIMIZE:配合 StateChanged 复原,实现「显示桌面」免疫。</summary>
    public static bool IsMinimizeCommand(IntPtr hwnd, int msg, IntPtr wParam)
        => msg == WM_SYSCOMMAND && (wParam.ToInt32() & SC_MASK) == SC_MINIMIZE;

    /// <summary>桌面层锚点:壁纸层之上、应用窗口之下的 Z 位置(顶层窗口用)。
    /// explorer 会在 Progman 之上动态创建/重建壁纸 WorkerW(切换显示桌面时常见),锚点需动态计算。</summary>
    public static IntPtr GetDesktopAnchor()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        IntPtr anchor = progman;
        IntPtr h = GetWindow(progman, GW_HWNDPREV);
        var sb = new StringBuilder(64);
        while (h != IntPtr.Zero)
        {
            sb.Clear();
            GetClassName(h, sb, 64);
            if (sb.ToString() != "WorkerW") break;   // 只跟随紧邻的壁纸层
            anchor = h;
            h = GetWindow(h, GW_HWNDPREV);
        }
        return anchor;
    }

    /// <summary>把顶层窗口压回桌面层(壁纸之上、应用之下)。不改可见性。</summary>
    public static void RepinToDesktop(IntPtr hwnd)
    {
        var after = GetDesktopAnchor();
        if (after == IntPtr.Zero) return;
        SetWindowPos(hwnd, after, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>向系统申请贴左全高工作区(缩略进程条),遵循 MSDN AppBar 流程。</summary>
    public static RECT AppBarSetPosLeft(IntPtr hwnd, int widthPhys)
    {
        int screenH = GetSystemMetrics(1);
        var d = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uEdge = ABE_LEFT,
            rc = new RECT { Left = 0, Top = 0, Right = widthPhys, Bottom = screenH },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.Right = d.rc.Left + widthPhys;   // 保留系统分配的左缘,宽度用申请值
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    /// <summary>向系统申请贴右全高工作区(小组件栏),遵循 MSDN AppBar 流程。</summary>
    public static RECT AppBarSetPosRight(IntPtr hwnd, int widthPhys)
    {
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        var d = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uEdge = ABE_RIGHT,
            rc = new RECT { Left = screenW - widthPhys, Top = 0, Right = screenW, Bottom = screenH },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.Left = d.rc.Right - widthPhys;   // 保留系统分配的右缘,宽度用申请值
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    /// <summary>把桌面子窗口插到「图标层(DefView)之下、壁纸层(WorkerW)之上」。
    /// Progman 子栈自顶向下:DefView → WorkerW(全屏壁纸) → 其余;卡片必须夹在中间才可见。
    /// explorer 会在切换显示桌面时重建这些层,需要周期性重放。</summary>
    public static void PlaceAboveWallpaper(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return;
        var defview = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        SetWindowPos(hwnd, defview != IntPtr.Zero ? defview : IntPtr.Zero,
            0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- AppBar(Dock 向系统保留底部工作区,最大化窗口不再被遮盖) ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage;
        public uint uEdge; public RECT rc; public IntPtr lParam;
    }
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
    public const uint ABM_NEW = 0x0;
    public const uint ABM_REMOVE = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS = 0x3;
    public const uint ABM_GETSTATE = 0x4;
    public const uint ABM_SETSTATE = 0xA;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_LEFT = 0;
    public const uint ABE_BOTTOM = 3;
    public const int ABN_POSCHANGED = 1;   // 回调 wParam
    public const int ABS_AUTOHIDE = 0x1;

    public static bool AppBarNew(IntPtr hwnd, uint callbackMsg)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uCallbackMessage = callbackMsg };
        return SHAppBarMessage(ABM_NEW, ref d) != IntPtr.Zero;
    }

    public static void AppBarRemove(IntPtr hwnd)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref d);
    }

    /// <summary>向系统申请贴底全宽工作区(物理像素),返回实际分配的区域(遵循 MSDN AppBar 流程)。</summary>
    public static RECT AppBarSetPosBottom(IntPtr hwnd, int heightPhys)
    {
        int screenW = GetSystemMetrics(0);   // SM_CXSCREEN
        int screenH = GetSystemMetrics(1);   // SM_CYSCREEN
        var d = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uEdge = ABE_BOTTOM,
            rc = new RECT { Left = 0, Top = screenH - heightPhys, Right = screenW, Bottom = screenH },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        // 保留系统分配的底边(会避开任务栏等其他 AppBar),高度用申请值
        d.rc.Top = d.rc.Bottom - heightPhys;
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    /// <summary>向系统申请贴顶全宽工作区(状态栏,macOS 菜单栏同理)。</summary>
    public static RECT AppBarSetPosTop(IntPtr hwnd, int heightPhys)
    {
        int screenW = GetSystemMetrics(0);
        var d = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = hwnd,
            uEdge = ABE_TOP,
            rc = new RECT { Left = 0, Top = 0, Right = screenW, Bottom = heightPhys },
        };
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.Bottom = d.rc.Top + heightPhys;   // 保留系统分配的顶边,高度用申请值
        SHAppBarMessage(ABM_SETPOS, ref d);
        return d.rc;
    }

    public static bool GetTaskbarAutoHide()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return ((int)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    public static void SetTaskbarAutoHide(bool on)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), lParam = (IntPtr)(on ? ABS_AUTOHIDE : 0) };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    // ---------- 图标提取 ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFOW
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint attrs, ref SHFILEINFOW info, uint cbSize, uint flags);
    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_LARGEICON = 0x0;
    public const uint SHGFI_SYSICONINDEX = 0x4000;
    [DllImport("shell32.dll")] public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    public const int SHIL_EXTRALARGE = 0x2;  // 48px
    public const int SHIL_JUMBO = 0x4;       // 256px
    public const int ILD_TRANSPARENT = 1;

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998D8095BC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out IntPtr picon);
        [PreserveSig] int AddMasked(IntPtr hbmMask, uint crForeground, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddNamed(IntPtr pszName, out int pi);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    /// <summary>提取应用大图标(优先 Jumbo 256px,失败退回 32px),返回已 Freeze 的 BitmapSource。</summary>
    public static System.Windows.Media.ImageSource? ExtractIcon(string path)
    {
        try
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998D8095BC");
                if (SHGetImageList(SHIL_JUMBO, ref iid, out IntPtr pList) == 0 && pList != IntPtr.Zero)
                {
                    var shfi = new SHFILEINFOW();
                    if (SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_SYSICONINDEX) != IntPtr.Zero)
                    {
                        var list = (IImageList)Marshal.GetObjectForIUnknown(pList);
                        list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out hIcon);
                        Marshal.Release(pList);
                    }
                }
            }
            catch { hIcon = IntPtr.Zero; }

            if (hIcon == IntPtr.Zero)
            {
                var shfi = new SHFILEINFOW();
                if (SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero)
                    hIcon = shfi.hIcon;
                else return null;
            }

            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            DestroyIcon(hIcon);
            // 缩到 128px 上限再冻结(显示尺寸最大约 120 设备像素),省一半图标内存
            double s = 128.0 / Math.Max(src.PixelWidth, src.PixelHeight);
            if (s < 1)
            {
                var scaled = new System.Windows.Media.Imaging.TransformedBitmap(
                    src, new System.Windows.Media.ScaleTransform(s, s));
                scaled.Freeze();
                return scaled;
            }
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
