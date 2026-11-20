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

    /// <summary>
    /// 第一个子元素（只计元素）。<c>:has()</c> 经它与 <see cref="NextSibling"/> 遍历后代；默认 null（适配器不提供时 <c>:has()</c>
    /// 的后代形式永不匹配，兄弟形式不受影响）。
    /// </summary>
    public virtual StyleElement? FirstChild => null;

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

    /// <summary>
    /// HTML 表现提示（如 <c>&lt;img width=100 height=50&gt;</c> 映射的 <c>width: 100px; height: 50px; aspect-ratio: auto 100 / 50</c>）。
    /// 级联中位于 UA 普通声明与作者普通声明之间、特异性为 0（CSS Cascade 5 §6.1、HTML §15.3），任何作者规则都能覆盖它；
    /// 按作者来源参与 <c>revert</c>。<c>!important</c> 在表现提示中被忽略。默认 null。
    /// </summary>
    public virtual CssInlineStyle? PresentationalHints => null;

    /// <summary>
    /// 表单与文档状态伪类（<c>:required :optional :valid :invalid :user-valid :user-invalid :read-only :read-write :indeterminate
    /// :default :in-range :out-of-range :autofill :open :closed :popover-open :modal :fullscreen :target :target-within :visited
    /// :link :any-link :local-link :defined :playing :paused</c>）是否匹配。
    /// 默认：<c>:read-only</c> 与 <c>:defined</c> 匹配（规范中「不可编辑」与「已定义」是默认状态），其余都不匹配。
    /// 状态变化时宿主负责使样式失效（与 <see cref="States"/> 相同）。
    /// </summary>
    public virtual bool MatchesStatePseudoClass(PseudoClass pseudoClass) => pseudoClass is PseudoClass.ReadOnly or PseudoClass.Defined;

    /// <summary>元素的方向性（<c>:dir()</c>），默认 ltr。</summary>
    public virtual StyleDirection Direction => StyleDirection.Ltr;

    /// <summary>元素自身声明的语言（如 HTML 的 lang 属性）；没有则为 null，<c>:lang()</c> 继续向上查找。</summary>
    public virtual string? Language => null;

    /// <summary>
    /// 元素当前的计算样式（上一次样式计算的结果）。容器查询与容器长度单位从祖先读取 <c>container-type</c>、<c>container-name</c>
    /// 与字体大小；默认 null（该元素不能作为查询容器）。
    /// </summary>
    public virtual ComputedStyle? CurrentStyle => null;

    /// <summary>
    /// 元素作为查询容器时的内容盒尺寸（上一次布局的结果，CSS px）。尚未布局时返回 false，此时依赖它的容器查询不成立、
    /// 容器单位按小视口换算；布局后宿主应重新计算带 <see cref="ComputedStyleFlags.DependsOnContainer"/> 的元素（见 design/08）。
    /// </summary>
    public virtual bool TryGetContainerSize(out float width, out float height)
    {
        width = height = 0;
        return false;
    }

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
