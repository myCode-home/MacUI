using System;
using System.Windows.Input;

namespace MacUI.Core;

/// <summary>解析并注册全局热键,如 "Ctrl+Space"、"Alt+Q"。</summary>
public static class HotkeyHelper
{
    /// <summary>"Ctrl+Space" → (MOD_CONTROL|MOD_NOREPEAT, 0x20)</summary>
    public static (uint Mods, uint Vk, bool Ok) Parse(string hotkey)
    {
        uint mods = Native.MOD_NOREPEAT, vk = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return (0, 0, false);
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return (0, 0, false);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            mods |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => Native.MOD_CONTROL,
                "alt" => Native.MOD_ALT,
                "shift" => Native.MOD_SHIFT,
                "win" or "windows" => Native.MOD_WIN,
                _ => 0xFFFFFFFF,
            };
            if (mods == 0xFFFFFFFF) return (0, 0, false);
        }

        vk = parts[^1].ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "home" => 0x24, "end" => 0x23, "pgup" => 0x21, "pgdn" => 0x22,
            "ins" or "insert" => 0x2D, "del" or "delete" => 0x2E,
            _ => KeyToVk(parts[^1]),
        };
        return (mods, vk, vk != 0);
    }

    private static uint KeyToVk(string s)
    {
        s = s.ToLowerInvariant();
        if (s.Length == 1)
        {
            char c = s[0];
            if (c is >= 'a' and <= 'z') return (uint)char.ToUpper(c);
            if (c is >= '0' and <= '9') return c;
            return 0;
        }
        if (s.Length == 2 && s[0] == 'f' && s[1] is >= '1' and <= '9') return 0x70u + (uint)(s[1] - '1'); // F1-F9
        if (s.Length == 3 && s.StartsWith("f") && int.TryParse(s.AsSpan(1), out int f) && f is >= 10 and <= 12)
            return 0x70u + (uint)(f - 1); // F10-F12
        return 0;
    }

    public static string Normalize(string hotkey)
    {
        var (mods, vk, ok) = Parse(hotkey);
        if (!ok) return hotkey;
        var (m, _) = (mods & ~Native.MOD_NOREPEAT, 0);
        string s = "";
        if ((m & Native.MOD_CONTROL) != 0) s += "Ctrl+";
        if ((m & Native.MOD_ALT) != 0) s += "Alt+";
        if ((m & Native.MOD_SHIFT) != 0) s += "Shift+";
        if ((m & Native.MOD_WIN) != 0) s += "Win+";
        return s + VkName(vk);
    }

    private static string VkName(uint vk) => vk switch
    {
        0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc",
        _ when vk is >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ when vk is >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        _ when vk is >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        _ => vk.ToString(),
    };
}
