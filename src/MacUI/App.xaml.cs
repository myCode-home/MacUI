using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MacUI.Core;
using MacUI.Windows;
using WinForms = System.Windows.Forms;

namespace MacUI;

/// <summary>
/// MacUI — Spotlight 式全局搜索(默认 Ctrl+Space 呼出)。
/// 其余桌面模块已按需求移除;托盘图标提供设置与退出。
/// </summary>
public partial class App : Application
{
    private const int HotkeyId = 0xB00B;
    private const int HotkeySwitcher = 0xB00C;
    private const int HotkeySwitcherBack = 0xB00D;
    private static Mutex? _mutex;

    public static AppConfig? Cfg { get; private set; }
    private static SpotlightWindow? _spotlight;
    private static SettingsWindow? _settings;
    private static StatusBarWindow? _statusBar;
    private static IntPtr _spotHwnd;
    private static bool _hotkeyOn;
    private static WinForms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "MacUI_SingleInstance", out bool isNew);
        if (!isNew) { Shutdown(); return; }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;   // 常驻工具,尽量不崩
        };

        Cfg = AppConfig.Load();

        // 清理旧版本遗留的任务栏自动隐藏状态(不再管理任务栏)
        if (Cfg.TaskbarOriginalAutoHide != null)
        {
            try { Native.SetTaskbarAutoHide(Cfg.TaskbarOriginalAutoHide == true); } catch { }
            Cfg.TaskbarOriginalAutoHide = null;
            Cfg.Save();
        }

        AppTheme.SetMode(Cfg.Theme);

        _spotlight = new SpotlightWindow();
        _spotHwnd = new WindowInteropHelper(_spotlight).EnsureHandle();
        HwndSource.FromHwnd(_spotHwnd)!.AddHook(WndProc);
        RegisterHotkey();
        RegisterSwitcherHotkeys();

        if (Cfg.StatusBarEnabled) { _statusBar = new StatusBarWindow(); }   // 触顶呼出,不主动显示

        InitTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cfg?.Save();
        if (_hotkeyOn && _spotHwnd != IntPtr.Zero) Native.UnregisterHotKey(_spotHwnd, HotkeyId);
        if (_switcherHotkeysOn && _spotHwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_spotHwnd, HotkeySwitcher);
            Native.UnregisterHotKey(_spotHwnd, HotkeySwitcherBack);
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        try { _mutex?.ReleaseMutex(); } catch { /* 非拥有线程 */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---------------- 全局热键 ----------------

    private static void RegisterHotkey()
    {
        if (_hotkeyOn && _spotHwnd != IntPtr.Zero) Native.UnregisterHotKey(_spotHwnd, HotkeyId);
        _hotkeyOn = false;
        if (_spotHwnd == IntPtr.Zero || !Cfg!.SpotlightEnabled) return;

        var (mods, vk, ok) = HotkeyHelper.Parse(Cfg.Hotkey);
        if (!ok) return;
        _hotkeyOn = Native.RegisterHotKey(_spotHwnd, HotkeyId, mods, vk);
    }

    private static bool _switcherHotkeysOn;

    /// <summary>前台调度热键:Ctrl+Tab(前进)与 Ctrl+Shift+Tab(后退)。</summary>
    private static void RegisterSwitcherHotkeys()
    {
        if (_switcherHotkeysOn && _spotHwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_spotHwnd, HotkeySwitcher);
            Native.UnregisterHotKey(_spotHwnd, HotkeySwitcherBack);
        }
        _switcherHotkeysOn = false;
        if (_spotHwnd == IntPtr.Zero || !Cfg!.SwitcherEnabled) return;

        var (mods, vk, ok) = HotkeyHelper.Parse(Cfg.SwitcherHotkey);
        if (!ok) return;
        _switcherHotkeysOn =
            Native.RegisterHotKey(_spotHwnd, HotkeySwitcher, mods, vk) &&
            Native.RegisterHotKey(_spotHwnd, HotkeySwitcherBack, mods | Native.MOD_SHIFT, vk);
    }

    /// <summary>设置修改后重新应用(热键/主题/状态栏)。</summary>
    public static void ApplyConfig()
    {
        AppTheme.SetMode(Cfg!.Theme);
        RegisterHotkey();
        RegisterSwitcherHotkeys();
        RefreshGlass();

        if (Cfg.StatusBarEnabled && _statusBar == null) { _statusBar = new StatusBarWindow(); }
        else if (!Cfg.StatusBarEnabled && _statusBar != null) { _statusBar.Close(); _statusBar = null; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyId) { _spotlight?.Toggle(); handled = true; }
            else if (id == HotkeySwitcher) { AppSwitcherWindow.HandleHotkey(false); handled = true; }
            else if (id == HotkeySwitcherBack) { AppSwitcherWindow.HandleHotkey(true); handled = true; }
        }
        return IntPtr.Zero;
    }

    // ---------------- 启动应用 ----------------

    public static void LaunchOrActivate(AppItem app)
    {
        try
        {
            string exe = System.IO.Path.GetFileNameWithoutExtension(
                app.TargetPath.Length > 0 ? app.TargetPath : app.LaunchPath);

            if (RunningApps.Activate(exe)) return;
            ProcessStart(app.LaunchPath);
        }
        catch (Exception ex) { LogError(ex); }
    }

    public static void ShellOpen(string pathOrUrl)
    {
        try { ProcessStart(pathOrUrl); }
        catch (Exception ex) { LogError(ex); }
    }

    private static void ProcessStart(string path)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    // ---------------- 托盘 ----------------

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MacUI — Spotlight 搜索 (" + HotkeyHelper.Normalize(Cfg!.Hotkey) + ")",
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(TrayCheck("顶部状态栏(触顶呼出)", Cfg.StatusBarEnabled, () =>
        {
            Cfg!.StatusBarEnabled = !Cfg.StatusBarEnabled;
            ApplyConfig();
            Cfg.Save();
        }));

        menu.Items.Add(TrayCheck("前台调度 (Ctrl+Tab)", Cfg.SwitcherEnabled, () =>
        {
            Cfg!.SwitcherEnabled = !Cfg.SwitcherEnabled;
            RegisterSwitcherHotkeys();
            Cfg.Save();
        }));

        menu.Items.Add(TrayCheck("开机自启动", Cfg.StartWithWindows, () =>
        {
            Cfg!.StartWithWindows = !Cfg.StartWithWindows;
            SetAutostart(Cfg.StartWithWindows);
            Cfg.Save();
        }));

        menu.Items.Add(new WinForms.ToolStripSeparator());
        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private static WinForms.ToolStripMenuItem TrayCheck(string text, bool @checked, Action onClick)
    {
        var item = new WinForms.ToolStripMenuItem(text) { CheckOnClick = true, Checked = @checked };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("MacUI", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("MacUI", false);
        }
        catch (Exception ex) { LogError(ex); }
    }

    // ---------------- 其他 ----------------

    public static void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>主题切换后重涂 Spotlight 玻璃。</summary>
    public static void RefreshGlass()
    {
        if (_spotlight == null) return;
        try
        {
            var h = new WindowInteropHelper(_spotlight).Handle;
            if (h != IntPtr.Zero) Native.EnableAcrylic(h, AppTheme.GlassTint(AppTheme.IsDark, 0.85));
        }
        catch { /* 忽略 */ }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n\r\n");
        }
        catch { /* 忽略 */ }
    }
}
