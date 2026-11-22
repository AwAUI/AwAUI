using System;
using System.Collections.Generic;

namespace AwAUI.MacOS.Accessibility;

/// <summary>
/// 原生指针 → 托管对象的注册表。NSAccessibility 的回调只有裸指针（<c>self</c>），
/// 这里把代理元素映射回 <see cref="MacA11yNode"/>、把根视图映射回 <see cref="MacAccessibilityBridge"/>。
/// <para>
/// 字典是静态强引用：原生对象不持有托管对象，若不在此保留，托管节点会被 GC 回收而回调仍在发生。
/// 所有访问都在锁内，AppKit 主线程与 UI 线程的更新可以交错。
/// </para>
/// </summary>
internal static class MacAccessibilityRegistry
{
    static readonly object Gate = new object();
    static readonly Dictionary<IntPtr, MacA11yNode> Nodes = new Dictionary<IntPtr, MacA11yNode>();
    static readonly Dictionary<IntPtr, MacAccessibilityBridge> Views = new Dictionary<IntPtr, MacAccessibilityBridge>();

    public static void RegisterNode(IntPtr handle, MacA11yNode node)
    {
        if (handle == IntPtr.Zero || node is null) return;
        lock (Gate) Nodes[handle] = node;
    }

    public static void UnregisterNode(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        lock (Gate) Nodes.Remove(handle);
    }

    public static bool TryGetNode(IntPtr handle, out MacA11yNode? node)
    {
        node = null;
        if (handle == IntPtr.Zero) return false;
        lock (Gate) return Nodes.TryGetValue(handle, out node);
    }

    public static void RegisterView(IntPtr handle, MacAccessibilityBridge bridge)
    {
        if (handle == IntPtr.Zero || bridge is null) return;
        lock (Gate) Views[handle] = bridge;
    }

    public static void UnregisterView(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        lock (Gate) Views.Remove(handle);
    }

    public static bool TryGetView(IntPtr handle, out MacAccessibilityBridge? bridge)
    {
        bridge = null;
        if (handle == IntPtr.Zero) return false;
        lock (Gate) return Views.TryGetValue(handle, out bridge);
    }
}
