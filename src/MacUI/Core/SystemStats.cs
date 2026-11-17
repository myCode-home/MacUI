using System;
using System.Runtime.InteropServices;

namespace MacUI.Core;

public class BatteryInfo
{
    public bool HasBattery;
    public bool AcOnline;
    public int Percent;      // 0-100,未知为 -1
}

/// <summary>电池采样(纯 Win32,零依赖)。</summary>
public static class SystemStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 1=已接通电源 0=电池
        public byte BatteryFlag;         // 128=无电池
        public byte BatteryLifePercent;  // 0-100,255=未知
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS s);

    public static BatteryInfo GetBattery()
    {
        var b = new BatteryInfo { Percent = -1 };
        SYSTEM_POWER_STATUS s = default;
        if (!GetSystemPowerStatus(ref s)) return b;

        b.AcOnline = s.ACLineStatus == 1;
        b.HasBattery = (s.BatteryFlag & 128) == 0;
        if (b.HasBattery && s.BatteryLifePercent is > 0 and < 255)
            b.Percent = s.BatteryLifePercent;
        return b;
    }
}
