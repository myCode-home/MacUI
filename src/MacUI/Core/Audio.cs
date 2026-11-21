using System;
using System.Runtime.InteropServices;

namespace MacUI.Core;

/// <summary>
/// 系统主音量(默认输出设备,与 Windows 音量面板同源)。
/// 采用裸 vtable 调用 IAudioEndpointVolume:
/// 某些系统上类型化 COM 互操作会在 QueryInterface(IMMDevice) 上返回 E_NOINTERFACE,
/// 而裸 vtable 路径稳定可用(实测可读写真实主音量,Windows 快捷面板同步变化)。
/// 注意:旧的 winmm waveOut 音量与真实主音量是两个独立的值,不可用于此处。
/// </summary>
public static class Audio
{
    private delegate int FnGetDefault(IntPtr self, int flow, int role, out IntPtr dev);
    private delegate int FnActivate(IntPtr self, ref Guid iid, uint clsctx, IntPtr p3, out IntPtr iface);
    private delegate int FnGetScalar(IntPtr self, out float level);
    private delegate int FnSetScalar(IntPtr self, float level, ref Guid ctx);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid, out IntPtr obj);

    private static IntPtr _vol;

    private static bool Ensure()
    {
        if (_vol != IntPtr.Zero) return true;
        try
        {
            var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");   // MMDeviceEnumerator
            var iidEn = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");   // IMMDeviceEnumerator
            if (CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_ALL*/, ref iidEn, out IntPtr en) != 0
                || en == IntPtr.Zero) return false;

            IntPtr vtbl = Marshal.ReadIntPtr(en);
            var getDefault = (FnGetDefault)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size), typeof(FnGetDefault));   // slot 4 = GetDefaultAudioEndpoint
            if (getDefault(en, 0 /*eRender*/, 0 /*eConsole*/, out IntPtr dev) != 0 || dev == IntPtr.Zero)
                return false;

            IntPtr devVtbl = Marshal.ReadIntPtr(dev);
            var activate = (FnActivate)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(devVtbl, 3 * IntPtr.Size), typeof(FnActivate)); // slot 3 = Activate
            var iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");          // IAudioEndpointVolume
            if (activate(dev, ref iidVol, 1, IntPtr.Zero, out IntPtr vol) != 0 || vol == IntPtr.Zero)
                return false;

            _vol = vol;
            return true;
        }
        catch { _vol = IntPtr.Zero; return false; }
    }

    /// <summary>主音量百分比(0-100);失败返回 -1。</summary>
    public static int GetVolumePercent()
    {
        if (!Ensure()) return -1;
        try
        {
            IntPtr vt = Marshal.ReadIntPtr(_vol);
            var f = (FnGetScalar)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vt, 9 * IntPtr.Size), typeof(FnGetScalar));      // slot 9 = GetMasterVolumeLevelScalar
            if (f(_vol, out float lvl) != 0) { _vol = IntPtr.Zero; return -1; }
            return Math.Clamp((int)Math.Round(lvl * 100), 0, 100);
        }
        catch { _vol = IntPtr.Zero; return -1; }
    }

    /// <summary>设置主音量百分比(0-100)。</summary>
    public static bool SetVolumePercent(int percent)
    {
        if (!Ensure()) return false;
        try
        {
            IntPtr vt = Marshal.ReadIntPtr(_vol);
            var f = (FnSetScalar)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(vt, 7 * IntPtr.Size), typeof(FnSetScalar));      // slot 7 = SetMasterVolumeLevelScalar
            Guid ctx = Guid.Empty;
            if (f(_vol, Math.Clamp(percent, 0, 100) / 100f, ref ctx) != 0) { _vol = IntPtr.Zero; return false; }
            return true;
        }
        catch { _vol = IntPtr.Zero; return false; }
    }
}
