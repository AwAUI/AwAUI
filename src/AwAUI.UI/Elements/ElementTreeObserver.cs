namespace AwAUI.UI;

/// <summary>
/// 元素树变更的观察者：样式失效、布局、DevTools 与无障碍桥接通过它订阅（<see cref="UIRoot.AddObserver"/>）。
/// 只报告挂在根上的元素的变化；新插入的子树只报告其顶层元素（观察者自行遍历子树）。
/// 回调在 UI 线程上、在协调过程中同步调用，不应修改元素树。
/// </summary>
public abstract class ElementTreeObserver
{
    /// <summary><paramref name="child"/> 被插入到 <paramref name="parent"/>。</summary>
    public virtual void OnChildInserted(Element parent, Element child) { }

    /// <summary><paramref name="child"/> 从 <paramref name="parent"/> 移除（整棵子树随之摘离）。</summary>
    public virtual void OnChildRemoved(Element parent, Element child) { }

    /// <summary><paramref name="child"/> 在 <paramref name="parent"/> 内移动了位置（协调器保证移动次数最少）。</summary>
    public virtual void OnChildMoved(Element parent, Element child) { }

    /// <summary>
    /// <paramref name="child"/> 在 <paramref name="parent"/> 内移动到 <paramref name="before"/> 之前（null 表示末尾）。
    /// 一次同步中先从后往前报告全部移动、再从后往前报告全部插入：<paramref name="before"/> 是其后第一个不是本次新插入的兄弟，
    /// 插入通知时其后的兄弟都已报告过。逐步维护子节点顺序的观察者（DevTools 元素树）据此在每一步都能定位。
    /// 默认转发到 <see cref="OnChildMoved(Element, Element)"/>。
    /// </summary>
    public virtual void OnChildMoved(Element parent, Element child, Element? before) => OnChildMoved(parent, child);

    public virtual void OnIdChanged(Element element, string? oldId) { }

    public virtual void OnClassesChanged(Element element) { }

    /// <summary>自定义属性变化；<paramref name="oldValue"/> 为 null 表示新增。</summary>
    public virtual void OnAttributeChanged(Element element, string name, string? oldValue) { }

    public virtual void OnInlineStyleChanged(Element element) { }

    public virtual void OnPseudoStatesChanged(Element element, PseudoStates oldStates) { }

    /// <summary>文本节点的文本或 <see cref="TextElement.Content"/> 变化。</summary>
    public virtual void OnTextChanged(Element element) { }

    /// <summary>反射为属性的内建字段变化（<c>role</c>、<c>tabindex</c>、<c>src</c>、<c>disabled</c>、<c>value</c> ...）。</summary>
    public virtual void OnPropertyChanged(Element element, string name) { }
}
