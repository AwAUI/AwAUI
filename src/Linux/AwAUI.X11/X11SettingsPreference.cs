using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// XSETTINGS（freedesktop.org）提供的桌面偏好：<c>Net/ThemeName</c> 名称含 “dark” 视为深色外观，
/// <c>Gtk/EnableAnimations</c> 为 0 视为减少动态效果。
/// <para>
/// 读取设置管理器窗口（<c>_XSETTINGS_S0</c> 选择的所有者）上的 <c>_XSETTINGS_SETTINGS</c> 属性，
/// 并在属性变化后重新读取；设置管理器窗口销毁时重新检查所有者。所有调用都在平台线程上。
/// 没有设置管理器（或属性不可读）时保持浅色外观与正常动效。
/// </para>
/// </summary>
internal sealed unsafe class X11SettingsPreference : IAppearancePreference, IReducedMotionPreference
{
    readonly IntPtr _display;
    readonly ulong _root;
    readonly ulong _selectionAtom;
    readonly ulong _settingsAtom;

    ulong _settingsWindow;
    ulong _lastOwnerCheck;
    Dictionary<string, XSettingValue>? _values;

    public bool PrefersDark { get; private set; }

    public bool PrefersReducedMotion { get; private set; }

    public event Action<bool>? PrefersDarkChanged;

    public event Action<bool>? PrefersReducedMotionChanged;

    public X11SettingsPreference(IntPtr display, ulong root)
    {
        _display = display;
        _root = root;
        _selectionAtom = InternAtom(display, "_XSETTINGS_S0");
        _settingsAtom = InternAtom(display, "_XSETTINGS_SETTINGS");
        XSelectInput(_display, _root, PropertyChangeMask);
        AttachOwner();
    }

    /// <summary>
    /// 处理根窗口与设置管理器窗口的通知。其他窗口的事件返回 false，由平台继续分发。
    /// </summary>
    public bool HandleEvent(XEvent* ev)
    {
        if (ev->type == DestroyNotify)
        {
            if (_settingsWindow == 0 || ev->xany.window != _settingsWindow) return false;
            AttachOwner(force: true); // 管理器退出：可能已有新所有者，否则回到默认值
            return true;
        }
        if (ev->type != PropertyNotify) return false;

        if (ev->xproperty.window == _root)
        {
            // 标准做法是经 XFixes 订阅选择所有者变化；为不引入额外扩展依赖，这里在根窗口属性变化时重查，
            // 并用服务器时间把往返限制在每秒一次（窗口管理器频繁更新根属性时不会反复查询）。
            ulong time = ev->xproperty.time;
            if (_lastOwnerCheck != 0 && time - _lastOwnerCheck < 1000) return true;
            _lastOwnerCheck = time;
            AttachOwner();
            return true;
        }

        if (_settingsWindow != 0 && ev->xproperty.window == _settingsWindow && ev->xproperty.atom == _settingsAtom)
        {
            ReadAndApply();
            return true;
        }
        return false;
    }

    void AttachOwner(bool force = false)
    {
        ulong owner = XGetSelectionOwner(_display, _selectionAtom);
        if (!force && owner == _settingsWindow) return;
        _settingsWindow = owner;
        _values = null;
        if (owner == 0)
        {
            Apply();
            return;
        }
        XSelectInput(_display, owner, PropertyChangeMask | StructureNotifyMask);
        ReadAndApply();
    }

    void ReadAndApply()
    {
        _values = ReadSettings(_settingsWindow);
        Apply();
    }

    void Apply()
    {
        bool dark = XSettingsSemantics.IsDarkTheme(GetString("Net/ThemeName"));
        if (dark != PrefersDark)
        {
            PrefersDark = dark;
            PrefersDarkChanged?.Invoke(dark);
        }

        bool reduced = XSettingsSemantics.IsReducedMotion(GetInteger("Gtk/EnableAnimations"));
        if (reduced != PrefersReducedMotion)
        {
            PrefersReducedMotion = reduced;
            PrefersReducedMotionChanged?.Invoke(reduced);
        }
    }

    string? GetString(string name) =>
        _values != null && _values.TryGetValue(name, out var value) && value.Kind == XSettingKind.String ? value.Text : null;

    int? GetInteger(string name) =>
        _values != null && _values.TryGetValue(name, out var value) && value.Kind == XSettingKind.Integer ? value.Integer : null;

    Dictionary<string, XSettingValue>? ReadSettings(ulong window)
    {
        if (window == 0) return null;

        // 所有者可能恰好在读取前退出：把 BadWindow 收进错误陷阱，避免打印到 stderr
        XErrors.Trap();
        ulong actualType = 0, count = 0, after = 0;
        int format = 0;
        byte* data = null;
        int status = XGetWindowProperty(_display, window, _settingsAtom, 0, 0x1FFFFFFF, 0, AnyPropertyType,
            &actualType, &format, &count, &after, &data);
        XSync(_display, 0);
        int error = XErrors.Untrap();
        try
        {
            if (error != 0 || status != 0 || actualType != _settingsAtom || format != 8) return null;
            if (after != 0 || count == 0 || count > int.MaxValue) return null;
            var bytes = new byte[(int)count];
            Marshal.Copy((IntPtr)data, bytes, 0, bytes.Length);
            return XSettingsParser.TryParse(bytes, out var values) ? values : null;
        }
        finally
        {
            if (data != null) XFree(data);
        }
    }
}
