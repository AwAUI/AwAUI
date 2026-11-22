using System;
using System.Collections.Generic;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// macOS 显示器集合：<c>[NSScreen screens]</c> / <c>+mainScreen</c> 的 frame、visibleFrame 与 backingScaleFactor。
/// Cocoa 的全局屏幕坐标原点在主显示器左下、y 向上；这里换算为框架的左上原点、y 向下（见 <see cref="MacScreenMath"/>）。
/// 屏幕布局变化（<c>NSApplicationDidChangeScreenParametersNotification</c>）时重新读取并触发 <see cref="Screens.Changed"/>。
/// </summary>
internal sealed class MacScreens : Screens
{
    readonly MacPlatform _platform;
    IReadOnlyList<Screen> _all = Array.Empty<Screen>();
    Screen? _primary;
    double _primaryTop;

    internal MacScreens(MacPlatform platform)
    {
        _platform = platform;
        RefreshCore();
    }

    public override IReadOnlyList<Screen> All
    {
        get
        {
            // AppKit 只能在主线程上访问；其他线程读到的是最近一次缓存。
            if (_platform.Dispatcher.CheckAccess()) RefreshCore();
            return _all;
        }
    }

    public override Screen Primary
    {
        get
        {
            if (_platform.Dispatcher.CheckAccess()) RefreshCore();
            return _primary ?? new Screen("", default, default, 1, isPrimary: true);
        }
    }

    /// <summary>主显示器在 Cocoa 坐标中的上边（用于窗口位置换算）。</summary>
    internal double PrimaryTop => _primaryTop;

    /// <summary>屏幕参数变化通知（主线程）：重新读取并通知订阅者。</summary>
    internal void OnScreensChanged()
    {
        RefreshCore();
        OnChanged();
    }

    void RefreshCore()
    {
        var screens = Send(Class("NSScreen"), Sel("screens"));
        int count = screens == IntPtr.Zero ? 0 : (int)Math.Min(SendULong(screens, Sel("count")), int.MaxValue);

        // 正常情况下至少有一个显示器；没有时退回 mainScreen，仍报告一个屏幕而不是空集合。
        if (count == 0)
        {
            var main = Send(Class("NSScreen"), Sel("mainScreen"));
            if (main != IntPtr.Zero)
            {
                screens = Send(Class("NSArray"), Sel("arrayWithObject:"), main);
                count = 1;
            }
        }
        if (count == 0)
        {
            _all = Array.Empty<Screen>();
            _primary = null;
            _primaryTop = 0;
            return;
        }

        var primary = Send(screens, Sel("objectAtIndex:"), 0L);
        var primaryFrame = SendRect(primary, Sel("frame"));
        _primaryTop = primaryFrame.Origin.Y + primaryFrame.Size.Height;

        var list = new List<Screen>(count);
        for (int i = 0; i < count; i++)
        {
            var s = Send(screens, Sel("objectAtIndex:"), (long)i);
            var frame = SendRect(s, Sel("frame"));
            var visible = SendRect(s, Sel("visibleFrame"));
            double scale = SendDouble(s, Sel("backingScaleFactor"));
            list.Add(new Screen(
                NameOf(s),
                MacScreenMath.FromCocoa(frame.Origin.X, frame.Origin.Y, frame.Size.Width, frame.Size.Height, _primaryTop),
                MacScreenMath.FromCocoa(visible.Origin.X, visible.Origin.Y, visible.Size.Width, visible.Size.Height, _primaryTop),
                scale,
                isPrimary: i == 0));
        }
        _all = list;
        _primary = list[0];
    }

    /// <summary>localizedName 从 macOS 10.15 起可用；更早的系统报告空名称。</summary>
    static string NameOf(IntPtr screen)
    {
        if (SendByte(screen, Sel("respondsToSelector:"), Sel("localizedName")) == 0) return "";
        return GetString(Send(screen, Sel("localizedName")), 256) ?? "";
    }
}
