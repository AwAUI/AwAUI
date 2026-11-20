using System;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>所有窗口共用的辅助功能偏好；原生观察者与 MacPlatform 的 helper 一样存活到进程结束。</summary>
internal sealed class MacReducedMotionPreference : IReducedMotionPreference
{
    internal const string ChangedSelector = "awAccessibilityDisplayOptionsChanged:";
    readonly IntPtr _workspace;
    readonly IntPtr _getter;
    readonly IntPtr _notification;

    MacReducedMotionPreference(IntPtr workspace, IntPtr getter, IntPtr notification)
    {
        _workspace = workspace;
        _getter = getter;
        _notification = notification;
    }

    internal static MacReducedMotionPreference? TryCreate()
    {
        var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        var getter = Sel("accessibilityDisplayShouldReduceMotion");
        if (SendByte(workspace, Sel("respondsToSelector:"), getter) == 0) return null;
        var notification = CG.GetConstant(MacPlatform.AppKit, "NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification");
        return notification == IntPtr.Zero ? null : new MacReducedMotionPreference(workspace, getter, notification);
    }

    internal void Observe(IntPtr observer)
    {
        var center = Send(_workspace, Sel("notificationCenter"));
        SendVoid(center, Sel("addObserver:selector:name:object:"), observer, Sel(ChangedSelector), _notification, _workspace);
        Refresh();
    }

    public bool PrefersReducedMotion { get; private set; }
    public event Action<bool>? PrefersReducedMotionChanged;

    internal void Refresh()
    {
        // 通知也会因其他显示选项改变而触发；读取实际偏好，只报告变化。
        bool value = SendByte(_workspace, _getter) != 0;
        if (value == PrefersReducedMotion) return;
        PrefersReducedMotion = value;
        PrefersReducedMotionChanged?.Invoke(value);
    }
}
