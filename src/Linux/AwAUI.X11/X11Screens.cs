using System;
using System.Collections.Generic;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// X11 显示器集合。几何来自 Xlib 的屏幕列表（<c>XScreenCount</c> / <c>XDisplayWidth</c> / <c>XDisplayHeight</c>）
/// 与 EWMH 的 <c>_NET_WORKAREA</c>（扣除面板/任务栏的可用区域）；缩放使用平台的全局 <see cref="X11Platform.Scale"/>。
/// <para>
/// 已知限制：X11 只有一个默认屏幕参与窗口创建，同一 X 屏幕内由 XRandR/Xinerama 合并的多台显示器不会被分开列出，
/// 每台显示器工作区也只有一个全局 <c>_NET_WORKAREA</c>；需要逐显示器的布局时由上层按 <see cref="Screen.Bounds"/> 细分。
/// 订阅了 RRScreenChangeNotify（libXrandr 可用时）与根窗口的 ConfigureNotify，布局/工作区变化时触发 <see cref="Screens.Changed"/>。
/// </para>
/// 只在平台线程上构造与刷新。
/// </summary>
internal sealed unsafe class X11Screens : Screens
{
    readonly X11Platform _platform;
    readonly IntPtr _display;
    readonly ulong _root;
    readonly ulong _workAreaAtom;
    readonly ulong _currentDesktopAtom;
    readonly int _rrEventBase;

    IReadOnlyList<Screen> _all;
    Screen _primary;

    internal X11Screens(X11Platform platform)
    {
        _platform = platform;
        _display = platform.Display;
        _root = platform.Root;
        _workAreaAtom = InternAtom(_display, "_NET_WORKAREA");
        _currentDesktopAtom = InternAtom(_display, "_NET_CURRENT_DESKTOP");

        var fallback = CreateFallback();
        _all = new[] { fallback };
        _primary = fallback;

        _rrEventBase = SubscribeToRandr();
        // 根窗口的尺寸变化与 _NET_WORKAREA / 当前桌面变化；与 XSETTINGS 订阅的 PropertyChangeMask 合并（XSelectInput 覆盖本客户端的掩码）
        XSelectInput(_display, _root, PropertyChangeMask | StructureNotifyMask);
        Refresh();
    }

    public override IReadOnlyList<Screen> All => _all;

    public override Screen Primary => _primary;

    /// <summary>处理根窗口与 XRandR 事件；返回 true 表示已消费。</summary>
    internal bool HandleEvent(XEvent* ev)
    {
        if (_rrEventBase >= 0 && ev->type == _rrEventBase + Xrandr.ScreenChangeNotify)
        {
            try { Xrandr.XRRUpdateConfiguration(ev); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Refresh();
            OnChanged();
            return true;
        }
        if (ev->type == ConfigureNotify && ev->xconfigure.window == _root)
        {
            Refresh();
            OnChanged();
            return true;
        }
        if (ev->type == PropertyNotify && ev->xproperty.window == _root)
        {
            ulong atom = ev->xproperty.atom;
            if (atom != _workAreaAtom && atom != _currentDesktopAtom) return false;
            Refresh();
            OnChanged();
            return true;
        }
        return false;
    }

    /// <summary>重新查询显示器与工作区；至少保留一个显示器。</summary>
    internal void Refresh()
    {
        double scale = _platform.Scale;
        if (!(scale > 0) || double.IsInfinity(scale) || double.IsNaN(scale)) scale = 1;

        var workAreas = ReadCardinals(_workAreaAtom, 4 * 64);
        var desktops = ReadCardinals(_currentDesktopAtom, 1);
        int desktop = desktops.Length > 0 ? (int)Math.Max(0, Math.Min(int.MaxValue, desktops[0])) : 0;
        Rect workAreaPx = X11WindowGeometry.ParseWorkArea(workAreas, desktop);

        int count = XScreenCount(_display);
        if (count <= 0) count = 1;
        var list = new List<Screen>(count);
        Screen? primary = null;
        for (int i = 0; i < count; i++)
        {
            int width = XDisplayWidth(_display, i);
            int height = XDisplayHeight(_display, i);
            if (width <= 0 || height <= 0) continue;
            var bounds = new Rect(0, 0, (float)(width / scale), (float)(height / scale));
            Rect work = bounds;
            if (i == _platform.Screen && !workAreaPx.IsEmpty)
            {
                var logical = new Rect((float)(workAreaPx.X / scale), (float)(workAreaPx.Y / scale),
                    (float)(workAreaPx.Width / scale), (float)(workAreaPx.Height / scale));
                work = X11WindowGeometry.ClampWorkArea(bounds, logical);
            }
            bool isPrimary = i == _platform.Screen;
            var screen = new Screen(isPrimary ? "default" : "screen" + i, bounds, work, scale, isPrimary);
            if (isPrimary) primary = screen;
            list.Add(screen);
        }

        if (list.Count == 0)
        {
            var fallback = CreateFallback();
            _all = new[] { fallback };
            _primary = fallback;
            return;
        }
        _all = list;
        _primary = primary ?? list[0];
    }

    Screen CreateFallback()
    {
        int width = XDisplayWidth(_display, _platform.Screen);
        int height = XDisplayHeight(_display, _platform.Screen);
        if (width <= 0) width = 1;
        if (height <= 0) height = 1;
        double scale = _platform.Scale > 0 ? _platform.Scale : 1;
        var bounds = new Rect(0, 0, (float)(width / scale), (float)(height / scale));
        return new Screen("default", bounds, bounds, scale, isPrimary: true);
    }

    int SubscribeToRandr()
    {
        try
        {
            if (Xrandr.XRRQueryExtension(_display, out int eventBase, out _) == 0) return -1;
            Xrandr.XRRSelectInput(_display, _root, Xrandr.ScreenChangeNotifyMask);
            return eventBase;
        }
        catch (DllNotFoundException) { return -1; }
        catch (EntryPointNotFoundException) { return -1; }
    }

    long[] ReadCardinals(ulong property, int maxItems)
    {
        ulong actualType, nitems, bytesAfter;
        int format;
        byte* data = null;
        int status = XGetWindowProperty(_display, _root, property, 0, Math.Max(1, maxItems), 0, AnyPropertyType,
            &actualType, &format, &nitems, &bytesAfter, &data);
        try
        {
            if (status != 0 || data == null || format != 32) return Array.Empty<long>();
            int n = (int)Math.Min(nitems, (ulong)Math.Max(0, maxItems));
            if (n <= 0) return Array.Empty<long>();
            var values = new long[n];
            long* p = (long*)data;
            for (int i = 0; i < n; i++) values[i] = p[i];
            return values;
        }
        finally
        {
            if (data != null) XFree(data);
        }
    }
}
