using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// 显示器集合（design/04「Screens」）。用 EnumDisplayMonitors + GetMonitorInfoW 枚举，按 Per-Monitor V2 的
/// GetDpiForMonitor 取每个显示器的有效 DPI，把物理矩形换算为逻辑像素。名称取设备名（\\.\DISPLAY1），
/// 旧系统上 GetDpiForMonitor 不可用时回退到系统 DPI。
/// </summary>
public sealed unsafe class Win32Screens : Screens
{
    IReadOnlyList<Screen>? _all;

    internal Win32Screens()
    {
        _all = Enumerate();
    }

    public override IReadOnlyList<Screen> All => _all ??= Enumerate();

    public override Screen Primary
    {
        get
        {
            var all = All;
            for (int i = 0; i < all.Count; i++)
                if (all[i].IsPrimary) return all[i];
            return all[0];
        }
    }

    /// <summary>重新枚举显示器；布局、工作区或缩放变化时触发 <see cref="Screens.Changed"/>。只在 UI 线程调用。</summary>
    public void Refresh()
    {
        var next = Enumerate();
        var current = _all;
        _all = next;
        if (current is not null && !Same(current, next)) OnChanged();
    }

    static bool Same(IReadOnlyList<Screen> left, IReadOnlyList<Screen> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.Name != b.Name || a.Bounds != b.Bounds || a.WorkingArea != b.WorkingArea ||
                a.Scaling != b.Scaling || a.IsPrimary != b.IsPrimary) return false;
        }
        return true;
    }

    static List<Screen> Enumerate()
    {
        var list = new List<Screen>(4);
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(list);
            User32.EnumDisplayMonitors(IntPtr.Zero, null, s_callback, GCHandle.ToIntPtr(handle));
        }
        catch (Exception)
        {
            // DllNotFound / EntryPointNotFound：退回到单显示器
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }
        if (list.Count == 0) list.Add(Fallback());
        return list;
    }

    static readonly User32.MonitorEnumProc s_callback = Callback;

    static unsafe int Callback(IntPtr monitor, IntPtr hdc, RECT* clip, IntPtr data)
    {
        // 异常绝不能穿过原生栈帧。
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not List<Screen> list) return 1;
            var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
            if (User32.GetMonitorInfoExW(monitor, &info) == 0) return 1;
            double scale = GetMonitorScale(monitor);
            if (!(scale > 0)) scale = 1;
            var bounds = ToLogical(info.rcMonitor, scale);
            var work = ToLogical(info.rcWork, scale);
            bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
            list.Add(new Screen(DeviceName(&info), bounds, work, scale, primary));
        }
        catch (Exception)
        {
        }
        return 1;
    }

    static Rect ToLogical(RECT r, double scale) =>
        Rect.FromLTRB((float)(r.Left / scale), (float)(r.Top / scale), (float)(r.Right / scale), (float)(r.Bottom / scale));

    static unsafe string DeviceName(MONITORINFOEXW* info)
    {
        char* p = info->szDevice;
        int n = 0;
        while (n < 32 && p[n] != '\0') n++;
        return n == 0 ? "" : new string(p, 0, n);
    }

    static bool s_noGetDpiForMonitor;

    static double GetMonitorScale(IntPtr monitor)
    {
        if (!s_noGetDpiForMonitor)
        {
            try
            {
                int hr = Shcore.GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
                if (hr == 0 && dpiX > 0) return dpiX / (double)USER_DEFAULT_SCREEN_DPI;
            }
            catch (DllNotFoundException) { s_noGetDpiForMonitor = true; }
            catch (EntryPointNotFoundException) { s_noGetDpiForMonitor = true; }
        }
        return SystemDpi() / (double)USER_DEFAULT_SCREEN_DPI;
    }

    static uint SystemDpi()
    {
        var dc = User32.GetDC(IntPtr.Zero);
        int dpi = dc != IntPtr.Zero ? Gdi32.GetDeviceCaps(dc, LOGPIXELSX) : 0;
        if (dc != IntPtr.Zero) User32.ReleaseDC(IntPtr.Zero, dc);
        return dpi > 0 ? (uint)dpi : USER_DEFAULT_SCREEN_DPI;
    }

    static Screen Fallback()
    {
        int width = User32.GetSystemMetrics(SM_CXSCREEN);
        int height = User32.GetSystemMetrics(SM_CYSCREEN);
        double scale = SystemDpi() / (double)USER_DEFAULT_SCREEN_DPI;
        if (width <= 0 || height <= 0)
        {
            width = 1024;
            height = 768;
        }
        var rect = Rect.FromLTRB(0, 0, (float)(width / scale), (float)(height / scale));
        return new Screen("Primary", rect, rect, scale, isPrimary: true);
    }
}
