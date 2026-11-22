using System;
using System.Collections.Generic;

namespace AwAUI.Platform;

/// <summary>
/// 一个显示器（design/04「Screens」）。所有几何量都是逻辑像素（DIP），缩放为逻辑像素到设备像素的倍率。
/// </summary>
public sealed class Screen
{
    public Screen(string name, Rect bounds, Rect workingArea, double scaling, bool isPrimary = false)
    {
        Name = name ?? "";
        Bounds = bounds;
        WorkingArea = workingArea;
        Scaling = scaling > 0 && !double.IsInfinity(scaling) ? scaling : 1;
        IsPrimary = isPrimary;
    }

    /// <summary>平台名称（可能为空）。</summary>
    public string Name { get; }

    /// <summary>显示器完整边界。</summary>
    public Rect Bounds { get; }

    /// <summary>扣除任务栏/程序坞/面板后的可用工作区。</summary>
    public Rect WorkingArea { get; }

    /// <summary>逻辑像素到设备像素的缩放。</summary>
    public double Scaling { get; }

    /// <summary>是否为主显示器。</summary>
    public bool IsPrimary { get; }

    public override string ToString() => $"{Name} {Bounds} @{Scaling}x{(IsPrimary ? " (primary)" : "")}";
}

/// <summary>
/// 显示器集合（design/04）。由平台后端作为服务提供；窗口放置、全屏与多显示器界面通过它查询。
/// 事件在 UI 线程触发；没有实现时平台返回 null，调用方按单显示器处理。
/// </summary>
public abstract class Screens
{
    /// <summary>全部显示器（至少一个）。</summary>
    public abstract IReadOnlyList<Screen> All { get; }

    /// <summary>主显示器。</summary>
    public abstract Screen Primary { get; }

    /// <summary>包含指定点的显示器；没有时返回 null（例如点位于显示器之间的空隙）。</summary>
    public virtual Screen? FromPoint(Point point)
    {
        foreach (var screen in All)
            if (screen.Bounds.Contains(point)) return screen;
        return null;
    }

    /// <summary>窗口大部分所在的显示器；没有窗口位置信息时返回 null。</summary>
    public virtual Screen? FromWindow(WindowImpl window) => window is null ? null : FromPoint(window.Position);

    /// <summary>显示器布局、缩放或工作区变化。</summary>
    public event Action? Changed;

    /// <summary>派生类在显示器列表变化后调用。</summary>
    protected void OnChanged() => Changed?.Invoke();
}
