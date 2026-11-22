using System;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>所有窗口共用的系统外观（浅色 / 深色）；原生观察者与 MacPlatform 的 helper 一样存活到进程结束。</summary>
internal sealed class MacAppearancePreference : IAppearancePreference
{
    internal const string ChangedSelector = "awEffectiveAppearanceChanged:";
    readonly IntPtr _app;
    readonly IntPtr _notification;

    MacAppearancePreference(IntPtr app, IntPtr notification)
    {
        _app = app;
        _notification = notification;
    }

    internal static MacAppearancePreference? TryCreate()
    {
        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        // effectiveAppearance 从 macOS 10.14 起可用；更早的系统没有深色外观，报告浅色即可。
        if (SendByte(app, Sel("respondsToSelector:"), Sel("effectiveAppearance")) == 0) return null;
        var notification = CG.GetConstant(MacPlatform.AppKit, "NSApplicationDidChangeEffectiveAppearanceNotification");
        return notification == IntPtr.Zero ? null : new MacAppearancePreference(app, notification);
    }

    internal void Observe(IntPtr observer)
    {
        var center = Send(Class("NSNotificationCenter"), Sel("defaultCenter"));
        SendVoid(center, Sel("addObserver:selector:name:object:"), observer, Sel(ChangedSelector), _notification, _app);
        Refresh();
    }

    public bool PrefersDark { get; private set; }

    public event Action<bool>? PrefersDarkChanged;

    internal void Refresh()
    {
        // 通知也会因强调色等其他外观属性改变而触发；读取实际外观，只报告变化。
        // 深色家族的名称（NSAppearanceNameDarkAqua、VibrantDark、AccessibilityHighContrastDarkAqua）都含 Dark。
        var appearance = Send(_app, Sel("effectiveAppearance"));
        string? name = GetString(Send(appearance, Sel("name")));
        bool value = name is not null && name.Contains("Dark", StringComparison.Ordinal);
        if (value == PrefersDark) return;
        PrefersDark = value;
        PrefersDarkChanged?.Invoke(value);
    }
}
