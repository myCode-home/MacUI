using System;

namespace MacUI.Core;

/// <summary>
/// 系统音量(winmm waveOut API,零依赖)。
/// 注:现代 Windows 上 waveOut 音量会映射到默认设备的音量。
/// </summary>
public static class Audio
{
    public static int GetVolumePercent()
    {
        try
        {
            uint vol;
            if (Native.WaveOutGetVolume(0xFFFFFFFF, out vol) != 0) return 0;
            uint right = vol >> 16;
            return Math.Min((int)(right * 100 / 65535), 100);
        }
        catch { return 0; }
    }
}
