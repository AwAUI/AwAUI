using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.Headless;

/// <summary>
/// 可脚本化的显示器集合（design/04「Screens」）。默认有一块 1920×1080、缩放 1、工作区与边界相同的
/// 主显示器；测试可以整体替换、追加或移除显示器，并观察 <see cref="Screens.Changed"/>。
/// 所有几何量都是逻辑像素，结果完全确定。
/// </summary>
public sealed class HeadlessScreens : Screens
{
    readonly List<Screen> _screens = new();

    public HeadlessScreens()
    {
        _screens.Add(CreateDefaultScreen());
    }

    /// <summary>创建默认的 1920×1080 主显示器。</summary>
    public static Screen CreateDefaultScreen() =>
        new("Headless Display", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080), 1.0, isPrimary: true);

    /// <summary>为 true（默认）时，<see cref="SetScreens"/>、<see cref="Add"/>、<see cref="Remove"/> 与 <see cref="Reset"/> 自动触发 <see cref="Screens.Changed"/>。</summary>
    public bool NotifyOnChange { get; set; } = true;

    public override IReadOnlyList<Screen> All => _screens;

    public override Screen Primary
    {
        get
        {
            foreach (var screen in _screens)
                if (screen.IsPrimary) return screen;
            if (_screens.Count > 0) return _screens[0];
            throw new InvalidOperationException("The headless screen collection is empty.");
        }
    }

    /// <summary>整体替换显示器列表（至少一个）。</summary>
    public void SetScreens(params Screen[] screens)
    {
        if (screens is null) throw new ArgumentNullException(nameof(screens));
        if (screens.Length == 0) throw new ArgumentException("At least one screen is required.", nameof(screens));
        foreach (var screen in screens)
            if (screen is null) throw new ArgumentException("Screens cannot contain null.", nameof(screens));
        _screens.Clear();
        _screens.AddRange(screens);
        Notify();
    }

    /// <summary>追加一块显示器；列表保持给定顺序，主显示器仍由 <see cref="Screen.IsPrimary"/> 决定。</summary>
    public void Add(Screen screen)
    {
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        _screens.Add(screen);
        Notify();
    }

    /// <summary>移除一块显示器。不能移除最后一块（显示器集合至少有一个）；返回是否移除。</summary>
    public bool Remove(Screen screen)
    {
        if (screen is null) throw new ArgumentNullException(nameof(screen));
        if (_screens.Count <= 1) return false;
        if (!_screens.Remove(screen)) return false;
        Notify();
        return true;
    }

    /// <summary>恢复为只有默认显示器的初始状态。</summary>
    public void Reset()
    {
        _screens.Clear();
        _screens.Add(CreateDefaultScreen());
        Notify();
    }

    /// <summary>手动触发 <see cref="Screens.Changed"/>（例如修改了显示器列表之外的工作区数据后）。</summary>
    public void RaiseChanged() => OnChanged();

    void Notify()
    {
        if (NotifyOnChange) OnChanged();
    }
}
