using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace MacUI.Core;

public class AppItem
{
    public string Name { get; set; } = "";
    public string ShortcutPath { get; set; } = "";   // .lnk 或直接 exe
    public string TargetPath { get; set; } = "";     // 解析出的 exe / 文档路径
    public ImageSource? Icon { get; set; }

    /// <summary>启动用路径(launch 参数,可为 shell: 前缀)。</summary>
    public string LaunchPath => TargetPath.Length > 0 ? TargetPath : ShortcutPath;
}

/// <summary>开始菜单应用目录:枚举、图标缓存、模糊搜索。</summary>
public static class AppCatalog
{
    private static List<AppItem>? _apps;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TargetCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AppItem> Apps
    {
        get { LazyLoad(); return _apps!; }
    }

    private static void LazyLoad()
    {
        lock (Lock)
        {
            if (_apps != null) return;
            _apps = new List<AppItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),      // %ProgramData% 开始菜单
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),            // 当前用户开始菜单
            };
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.Length == 0) continue;
                    var low = name.ToLowerInvariant();
                    // 过滤卸载项
                    if (low.Contains("uninstall") || low.Contains("卸载") || low.Contains("帮助") || low.Contains("readme"))
                        continue;
                    if (!seen.Add(name)) continue;
                    _apps.Add(new AppItem { Name = name, ShortcutPath = f });
                }
            }
            _apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        }
    }

    /// <summary>解析 .lnk 目标(用 WScript.Shell COM,零依赖)。</summary>
    public static string ResolveTarget(string lnkPath)
    {
        if (TargetCache.TryGetValue(lnkPath, out var hit)) return hit;
        string target = "";
        try
        {
            if (Path.GetExtension(lnkPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic shortcut = shell.CreateShortcut(lnkPath);
                    target = (string)shortcut.TargetPath;
                    if (target.Length > 0 && !File.Exists(target)) target = "";
                }
            }
            else target = lnkPath;
        }
        catch { target = ""; }
        TargetCache[lnkPath] = target;
        return target;
    }

    /// <summary>图标缓存:优先取目标文件图标(无快捷方式小箭头),失败退回 lnk 自身。</summary>
    public static ImageSource? GetIcon(AppItem app)
    {
        if (app.Icon != null) return app.Icon;
        string key = app.TargetPath.Length > 0 ? app.TargetPath : app.ShortcutPath;
        if (!IconCache.TryGetValue(key, out var icon))
        {
            icon = Native.ExtractIcon(key) ?? Native.ExtractIcon(app.ShortcutPath);
            IconCache[key] = icon;
        }
        app.Icon = icon;
        return icon;
    }

    /// <summary>确保常用 AppItem 填好 TargetPath 与 Icon(用于 Dock / 桌面图标)。</summary>
    public static void Prepare(AppItem app)
    {
        if (app.TargetPath.Length == 0) app.TargetPath = ResolveTarget(app.ShortcutPath);
        GetIcon(app);
    }

    /// <summary>按名称找目录项(用于把 config 里存的路径匹配到 AppItem)。</summary>
    public static AppItem? FindByPath(string path)
    {
        LazyLoad();
        return _apps!.FirstOrDefault(a =>
            string.Equals(a.ShortcutPath, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.TargetPath, path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(a.ShortcutPath), path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>根据启动路径构造临时项(直接 exe / shell: 命令)。</summary>
    public static AppItem MakeItem(string name, string path)
    {
        var it = FindByPath(path) ?? new AppItem { Name = name, ShortcutPath = path, TargetPath = path };
        if (string.IsNullOrEmpty(it.Name)) it.Name = name;
        Prepare(it);
        return it;
    }

    /// <summary>子序列模糊匹配打分。</summary>
    public static List<(AppItem App, int Score)> FuzzySearch(string query, int max)
    {
        LazyLoad();
        var result = new List<(AppItem, int)>();
        if (string.IsNullOrWhiteSpace(query))
            return _apps!.Take(max).Select(a => (a, 0)).ToList();

        string q = query.Trim().ToLowerInvariant();
        foreach (var app in _apps!)
        {
            int s = Score(app.Name, q);
            if (s <= 0)
            {
                var target = app.TargetPath.Length > 0 ? app.TargetPath : app.ShortcutPath;
                s = Score(Path.GetFileNameWithoutExtension(target), q);
            }
            if (s > 0) result.Add((app, s));
        }
        return result.OrderByDescending(r => r.Item2).Take(max).ToList();
    }

    private static int Score(string name, string q)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        string n = name.ToLowerInvariant();
        if (n == q) return 1000;
        if (n.StartsWith(q)) return 800 - Math.Min(n.Length, 300);
        if (n.Contains(q)) return 600 - Math.Min(n.Length, 300);
        // 子序列匹配
        int ni = 0, qi = 0, streak = 0, best = 0;
        while (ni < n.Length && qi < q.Length)
        {
            if (n[ni] == q[qi]) { qi++; streak++; best = Math.Max(best, streak); }
            else streak = 0;
            ni++;
        }
        if (qi < q.Length) return 0;
        return 400 - Math.Min(n.Length, 200) + best * 10;
    }
}
