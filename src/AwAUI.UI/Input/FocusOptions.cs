using System;

namespace AwAUI.UI;

/// <summary><see cref="Element.Focus(FocusOptions)"/> 的选项（DOM 的 <c>FocusOptions</c>，另加 Radix 的 <c>select</c>）。</summary>
public sealed class FocusOptions
{
    /// <summary>默认选项：程序聚焦、滚动到可见、不选中文本。</summary>
    public static readonly FocusOptions Default = new();

    /// <summary>不把获得焦点的元素滚动到可见（DOM 的 <c>preventScroll</c>）。</summary>
    public bool PreventScroll { get; init; }

    /// <summary>聚焦输入框后全选其文本（Radix FocusScope 的 <c>focus(el, { select: true })</c>）；对其他元素无效。</summary>
    public bool SelectText { get; init; }

    /// <summary>焦点的来源（影响 <c>:focus-visible</c> 与 <see cref="FocusManager.FocusChanged"/> 报告的来源）。</summary>
    public FocusOrigin Origin { get; init; } = FocusOrigin.Programmatic;

    /// <summary>显式指定是否显示焦点环（DOM 的 <c>focusVisible</c>）；null 时按 <see cref="Origin"/> 的启发式。</summary>
    public bool? FocusVisible { get; init; }
}

/// <summary><see cref="FocusManager.FocusChanged"/> 的参数。</summary>
public sealed class FocusChangedEventArgs : EventArgs
{
    internal FocusChangedEventArgs(Element? oldFocus, Element? newFocus, FocusOrigin origin, bool removed)
    {
        OldFocus = oldFocus;
        NewFocus = newFocus;
        Origin = origin;
        ElementRemoved = removed;
    }

    /// <summary>失去焦点的元素（之前没有焦点时为 null）。</summary>
    public Element? OldFocus { get; }

    /// <summary>获得焦点的元素（焦点被清除时为 null）。</summary>
    public Element? NewFocus { get; }

    /// <summary>焦点变化的来源：键盘（Tab）、指针按下或程序。</summary>
    public FocusOrigin Origin { get; }

    /// <summary>
    /// 焦点元素所在的子树被移除（静默清除，没有 blur 事件）：<see cref="OldFocus"/> 已不在树上，<see cref="NewFocus"/> 为 null。
    /// 在该帧的提交阶段之后报告（Radix FocusScope 据此把焦点交还容器）。
    /// </summary>
    public bool ElementRemoved { get; }

    public override string ToString() => (OldFocus?.ToString() ?? "null") + " → " + (NewFocus?.ToString() ?? "null") + " (" + Origin + (ElementRemoved ? ", removed" : "") + ")";
}
