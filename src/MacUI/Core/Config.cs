using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacUI.Core;

/// <summary>应用配置(仅 Spotlight 搜索相关)。</summary>
public class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Space";   // Spotlight 呼出热键
    public bool SpotlightEnabled { get; set; } = true;
    public bool SwitcherEnabled { get; set; } = true;    // Ctrl+Tab 前台调度
    public string SwitcherHotkey { get; set; } = "Ctrl+Tab";
    public bool StatusBarEnabled { get; set; } = true;   // 顶部状态栏(触顶呼出)
    public int StatusBarAutoHideDelay { get; set; } = 600; // 鼠标离开后自动收回的延迟(毫秒)
    public string Theme { get; set; } = "system";        // system | dark | light
    public bool StartWithWindows { get; set; } = false;
    public bool? TaskbarOriginalAutoHide { get; set; }   // 旧版本遗留字段,仅用于清理

    private static readonly JsonSerializerOptions JOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacUI");
    public static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JOpts);
                if (cfg != null) { cfg.EnsureDefaults(); return cfg; }
            }
        }
        catch { /* 损坏则重建 */ }
        var def = new AppConfig();
        def.EnsureDefaults();
        return def;
    }

    public void EnsureDefaults()
    {
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* 尽力而为 */ }
    }
}
