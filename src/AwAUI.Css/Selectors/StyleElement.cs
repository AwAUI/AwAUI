using System;
using System.Collections.Generic;

namespace AwAUI.Css.Selectors;

/// <summary>由框架维护、供伪类匹配的元素状态。</summary>
[Flags]
public enum PseudoStates
{
    None = 0,
    Hover = 1 << 0,
    Active = 1 << 1,
    Focus = 1 << 2,
    FocusVisible = 1 << 3,
    FocusWithin = 1 << 4,
    Disabled = 1 << 5,
    Checked = 1 << 6,
    PlaceholderShown = 1 << 7,
}

/// <summary>
/// 选择器匹配所需的元素视图。CSS 引擎不依赖元素树的具体类型（design/01），UI 层的元素树、
/// 测试中的内存树或 DevTools 的快照都通过派生本类接入。兄弟关系只计元素（不含文本节点）。
/// </summary>
public abstract class StyleElement
{
    /// <summary>元素类型名，类型选择器按 ASCII 大小写不敏感匹配它。</summary>
    public abstract string Tag { get; }

    /// <summary>元素 ID；没有则为 null。</summary>
    public abstract string? Id { get; }

    /// <summary>class 列表（供规则索引枚举）。</summary>
    public abstract IReadOnlyList<string> Classes { get; }

    /// <summary>是否含有某个 class（大小写敏感）。默认线性查找 <see cref="Classes"/>，可重写为集合查找。</summary>
    public virtual bool HasClass(string name)
    {
        var classes = Classes;
        for (int i = 0; i < classes.Count; i++)
            if (string.Equals(classes[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>属性值；属性不存在时返回 null。<paramref name="name"/> 为选择器中书写的属性名。</summary>
    public abstract string? GetAttribute(string name);

    /// <summary>父元素；根元素为 null。</summary>
    public abstract StyleElement? Parent { get; }

    /// <summary>前一个元素兄弟。</summary>
    public abstract StyleElement? PreviousSibling { get; }

    /// <summary>后一个元素兄弟。</summary>
    public abstract StyleElement? NextSibling { get; }

    /// <summary>当前伪状态（含 DevTools 强制的状态）。</summary>
    public abstract PseudoStates States { get; }

    /// <summary><c>:empty</c>：没有子元素，也没有文本内容。</summary>
    public abstract bool IsEmpty { get; }

    /// <summary>该元素能否被禁用（决定 <c>:enabled</c> 是否可能匹配）。默认 false。</summary>
    public virtual bool CanBeDisabled => false;

    /// <summary><c>:root</c>：默认为没有父元素的元素。</summary>
    public virtual bool IsRoot => Parent is null;

    /// <summary>该元素作为组件部件暴露的名称（供 <c>::part()</c>）。</summary>
    public virtual IReadOnlyList<string> Parts => Array.Empty<string>();

    /// <summary>暴露本元素为部件的组件宿主元素（<c>x::part(name)</c> 中 <c>x</c> 所匹配的元素）。</summary>
    public virtual StyleElement? PartHost => null;

    /// <summary>元素的内联样式（style 属性）；没有则为 null。级联把它排在所有作者规则之上。</summary>
    public virtual CssInlineStyle? InlineStyle => null;

    /// <summary>在元素兄弟中的 1 基位置。默认沿兄弟链计数，可重写为 O(1)。</summary>
    public virtual int GetChildIndex()
    {
        int i = 1;
        for (var s = PreviousSibling; s is not null; s = s.PreviousSibling) i++;
        return i;
    }

    /// <summary>从末尾数起的 1 基位置。</summary>
    public virtual int GetChildIndexFromEnd()
    {
        int i = 1;
        for (var s = NextSibling; s is not null; s = s.NextSibling) i++;
        return i;
    }
}
