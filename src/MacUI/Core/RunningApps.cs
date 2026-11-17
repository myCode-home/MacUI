using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MacUI.Core;

public class RunningApp
{
    public IntPtr Hwnd;
    public uint Pid;
    public string Title = "";
    public string Process = "";
    public bool Minimized;
}

/// <summary>轮询当前运行中的可见应用窗口(用于 Dock 运行指示点与激活)。</summary>
public static class RunningApps
{
    /// <summary>
    /// 运行中的应用列表(按 Z 序 = 最近使用序,顶在前;每个进程取最靠前的主窗口)。
    /// 用于 Ctrl+Tab 切换器与左缘缩略条。
    /// </summary>
    public static List<RunningApp> GetRunningApps(bool includeMinimized = false)
    {
        var list = new List<RunningApp>();
        var seenPids = new HashSet<uint>();
        var pidNames = new Dictionary<uint, string>();
        var selfPid = (uint)Environment.ProcessId;
        var clsBuf = new System.Text.StringBuilder(64);

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (Native.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return true;
            if (pid == selfPid) return true;
            if (!includeMinimized && Native.IsIconic(hwnd)) return true;
            if (seenPids.Contains(pid)) return true;

            // 排除 explorer 外壳窗口
            clsBuf.Clear();
            Native.GetClassName(hwnd, clsBuf, 64);
            string cls = clsBuf.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland") return true;

            bool iconic = Native.IsIconic(hwnd);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!pidNames.TryGetValue(pid, out var name)) { name = p.ProcessName; pidNames[pid] = name; }
                string title = p.MainWindowTitle.Length > 0 ? p.MainWindowTitle : name;
                list.Add(new RunningApp { Hwnd = hwnd, Pid = pid, Title = title, Process = name, Minimized = iconic });
                seenPids.Add(pid);
            }
            catch { /* 进程可能已退出 */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    /// <summary>返回运行中应用的进程名集合(小写,不含 .exe)。</summary>
    public static HashSet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pidCache = new Dictionary<uint, string>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            if (!pidCache.TryGetValue(pid, out string? name))
            {
                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
                catch { name = null; }
                pidCache[pid] = name ?? "";
            }
            if (!string.IsNullOrEmpty(name)) names.Add(name);
            return true;
        }, IntPtr.Zero);
        return names;
    }

    /// <summary>找某进程名对应的主窗口句柄(优先非最小化的可见窗口)。</summary>
    public static IntPtr FindMainWindow(string processName)
    {
        IntPtr best = IntPtr.Zero, iconic = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }
            if (Native.IsIconic(hwnd)) { if (iconic == IntPtr.Zero) iconic = hwnd; }
            else if (best == IntPtr.Zero) best = hwnd;
            return best == IntPtr.Zero;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : iconic;
    }

    /// <summary>把某应用的主窗口切到前台;返回是否找到窗口。</summary>
    public static bool Activate(string processName)
    {
        var h = FindMainWindow(processName);
        if (h == IntPtr.Zero) return false;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        Native.SetForegroundWindow(h);
        return true;
    }
}
