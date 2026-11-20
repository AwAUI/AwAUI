using System;
using System.Collections.Generic;
using System.Threading;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>部件（Trigger、Content、Close ...）把调用方给出的类、样式与展开属性合并到自己产出的元素节点上。</summary>
internal static class PartProps
{
    /// <summary>
    /// 合并调用方的 <paramref name="cls"/>、<paramref name="style"/> 与 <paramref name="attributes"/> 到部件节点 <paramref name="node"/>：
    /// 类与样式拼接在部件自己的之后；展开属性中的事件处理器（<c>onclick</c> ...）与部件的处理器组合（调用方在前，
    /// 未 <see cref="Event.PreventDefault"/> 时再执行部件的，即 Radix 的 <c>composeEventHandlers</c>）；其余属性按
    /// <see cref="ElementNode.MergeAttributes(IEnumerable{KeyValuePair{string, object}})"/> 合并（部件已设置的保持不变）。
    /// </summary>
    public static T Apply<T>(T node, ClassValue cls, StyleValue style, AttributeDictionary? attributes) where T : ElementNode
    {
        if (!cls.IsEmpty) node.Class = node.Class.IsEmpty ? cls : new ClassValue(node.Class.Text + " " + cls.Text);
        if (!style.IsEmpty)
        {
            string s = style.Text!.Trim();
            node.Style = node.Style.IsEmpty ? new StyleValue(s) : new StyleValue(node.Style.Text!.Trim().TrimEnd(';') + "; " + s);
        }
        if (attributes is null || attributes.Count == 0) return node;
        List<KeyValuePair<string, object?>>? rest = null;
        foreach (var kv in attributes)
        {
            if (kv.Value is Delegate d && EventSlots.Find(kv.Key) is { } slot && slot.ComposeExternal(node, d)) continue;
            (rest ??= new List<KeyValuePair<string, object?>>()).Add(kv);
        }
        if (rest is not null) node.MergeAttributes(rest);
        return node;
    }

    /// <summary>
    /// 部件内部的处理器（Radix 中总是经 <c>composeEventHandlers</c> 包装）：事件已被 <see cref="Event.PreventDefault"/> 时不执行。
    /// <c>AsChild</c> 时 <see cref="Slot"/> 先调用子元素的处理器、再调用它，子元素因此可以阻止部件的默认行为（如触发器的切换）。
    /// </summary>
    public static Action<T> Internal<T>(Action action) where T : Event => e =>
    {
        if (!e.DefaultPrevented) action();
    };

    /// <summary><see cref="Internal{T}(Action)"/> 的指针事件版本。</summary>
    public static Action<PointerEvent> Internal(Action action) => Internal<PointerEvent>(action);

    /// <summary>
    /// 外部指针按下是否是右键（或 Ctrl+左键，macOS 上的上下文菜单点击）：Radix 的模态 Dialog 据此不关闭，模态 Popover 据此不把焦点还给触发器。
    /// </summary>
    public static bool IsRightClick(DismissableLayerEvent<Element> e) =>
        e.OriginalEvent is PointerEvent p
        && (p.Button == PointerButtons.Right || (p.Button == PointerButtons.Left && (p.Modifiers & KeyModifiers.Control) != 0));

    /// <summary>把 bool 写成 ARIA / data 属性值。</summary>
    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary><c>data-state</c> 的 open / closed。</summary>
    public static string OpenState(bool open) => open ? "open" : "closed";
}

/// <summary>部件之间互相引用用的 id（Radix 的 <c>useId</c>）：进程内唯一。</summary>
internal static class PrimitiveIds
{
    static long s_next;

    public static string Next(string prefix) => "aw-" + prefix + "-" + Interlocked.Increment(ref s_next).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
