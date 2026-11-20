using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Selectors;
using CssPseudoStates = AwAUI.Css.Selectors.PseudoStates;

namespace AwAUI.UI;

/// <summary>
/// 把 <see cref="Element"/> 呈现为 CSS 引擎的 <see cref="StyleElement"/>（design/07「Element ↔ StyleElement 适配」）。
/// 每个元素一个，懒创建并保存在 <see cref="RenderBox"/> 中；全部成员直接转发到元素，不复制状态。
/// 兄弟关系只计元素（跳过文本节点），与 <see cref="Element.PreviousElementSibling"/> 一致。
/// </summary>
internal sealed class ElementStyleView : StyleElement
{
    readonly Element _element;
    readonly RenderPipeline _pipeline;

    public ElementStyleView(Element element, RenderPipeline pipeline)
    {
        _element = element;
        _pipeline = pipeline;
    }

    public Element Element => _element;

    // ---------------- 帧管线为元素保存的伪元素与容器查询状态（RenderPipeline.Pseudo.cs / RenderPipeline.Containers.cs）

    /// <summary><c>::before</c> / <c>::after</c> 生成的盒；没有时为 null。</summary>
    internal GeneratedContentElement? Before, After;

    /// <summary>输入框的 <c>::placeholder</c> / <c>::selection</c> 计算样式；没有相应规则时为 null。</summary>
    internal ComputedStyle? PlaceholderStyle, SelectionStyle;

    /// <summary>作为查询容器时上一次用于容器查询的内容盒尺寸（NaN 表示还没有记录）与 <c>container-type / container-name</c>。</summary>
    internal float QueryWidth = float.NaN, QueryHeight = float.NaN;
    internal ComputedStyle? QueryStyle;

    /// <summary>映射布局样式时加上的经典滚动条槽（RenderPipeline.Gutter.cs）与 <c>overflow: auto</c> 的滚动条是否出现。</summary>
    internal float GutterRight, GutterBottom, GutterLeft;
    internal bool AutoGutterX, AutoGutterY;

    static ElementStyleView? Of(Element? e, RenderPipeline pipeline) =>
        e is null ? null : RenderBox.Get(e).GetView(pipeline);

    public override string Tag => _element.Tag;

    public override string? Id => _element.Id;

    public override IReadOnlyList<string> Classes => _element.Classes;

    public override bool HasClass(string name) => _element.HasClass(name);

    public override string? GetAttribute(string name) => _element.GetAttribute(name);

    public override StyleElement? Parent => Of(_element.Parent, _pipeline);

    public override StyleElement? PreviousSibling => Of(_element.PreviousElementSibling, _pipeline);

    public override StyleElement? NextSibling => Of(_element.NextElementSibling, _pipeline);

    /// <summary>第一个元素子节点（跳过文本节点）：<c>:has()</c> 经它与 <see cref="NextSibling"/> 遍历后代。</summary>
    public override StyleElement? FirstChild
    {
        get
        {
            var ch = _element.Children;
            for (int i = 0; i < ch.Count; i++)
                if (!ch[i].IsTextNode) return Of(ch[i], _pipeline);
            return null;
        }
    }

    /// <summary>上一次样式计算的结果（容器查询从祖先读取 <c>container-type / container-name</c> 与字号）。</summary>
    public override ComputedStyle? CurrentStyle => _element.Render?.Style;

    /// <summary>
    /// 作为查询容器的内容盒尺寸：上一次布局的结果（未对齐的内容盒）。还没有布局、或不生成盒（display:none）时返回 false。
    /// </summary>
    public override bool TryGetContainerSize(out float width, out float height)
    {
        if (_element.Render is { HasBox: true, Layout: not null } box)
        {
            width = box.ContentWidth;
            height = box.ContentHeight;
            return true;
        }
        width = height = 0;
        return false;
    }

    /// <summary>
    /// HTML 表现提示：<c>img</c> / <c>svg</c> 的 <c>width</c> / <c>height</c> 属性映射为 <c>width</c>、<c>height</c>，两者都给出时
    /// 另加 <c>aspect-ratio: auto w / h</c>（HTML §15.4.3「映射到 aspect-ratio 的属性」）。
    /// </summary>
    public override CssInlineStyle? PresentationalHints =>
        _element is ReplacedElement r && r.PresentationalHintText is { } text ? _pipeline.GetInlineStyle(text, _element) : null;

    /// <summary>表单与文档状态伪类（<see cref="StyleElement.MatchesStatePseudoClass"/>），按元素类型与属性求值。</summary>
    public override bool MatchesStatePseudoClass(PseudoClass pseudoClass) => StatePseudoClasses.Matches(_element, pseudoClass);

    /// <summary><c>:dir()</c>：最近的带 <c>dir</c> 属性（ltr / rtl）的祖先（含自身）决定方向，默认 ltr。</summary>
    public override StyleDirection Direction
    {
        get
        {
            int guard = 0;
            for (var e = _element; e is not null && guard < 4096; e = e.Parent, guard++)
            {
                var dir = e.GetAttribute("dir");
                if (dir is null) continue;
                if (string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase)) return StyleDirection.Rtl;
                if (string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase)) return StyleDirection.Ltr;
            }
            return StyleDirection.Ltr;
        }
    }

    /// <summary><c>:lang()</c>：元素自身的 <c>lang</c> 属性。</summary>
    public override string? Language => _element.GetAttribute("lang");

    /// <summary>两个枚举的位定义相同（见 <see cref="UI.PseudoStates"/> 的说明），直接按位转换。</summary>
    public override CssPseudoStates States => (CssPseudoStates)(int)_element.States;

    public override bool IsEmpty => _element.IsEmpty;

    public override bool CanBeDisabled => _element.CanBeDisabled;

    public override bool IsRoot => _element.Parent is null;

    public override IReadOnlyList<string> Parts => _element.Parts;

    /// <summary>
    /// <c>::part()</c> 的宿主：暴露部件的组件所在的那个元素，即最近的、由另一个组件产出的祖先元素
    /// （<c>.toolbar::part(label)</c> 中的 <c>.toolbar</c> 是包含该组件的元素）。
    /// </summary>
    public override StyleElement? PartHost
    {
        get
        {
            var owner = _element.Owner;
            for (var p = _element.Parent; p is not null; p = p.Parent)
                if (!ReferenceEquals(p.Owner, owner)) return Of(p, _pipeline);
            return null;
        }
    }

    /// <summary>内联样式：DevTools 的修改（<see cref="RenderBox.InlineOverride"/>）优先于元素自身的文本。</summary>
    public override CssInlineStyle? InlineStyle =>
        (_element.Render?.InlineOverride ?? _element.InlineStyle) is { Length: > 0 } text ? _pipeline.GetInlineStyle(text, _element) : null;

    public override int GetChildIndex() => _element.GetChildIndex();

    public override int GetChildIndexFromEnd() => _element.GetChildIndexFromEnd();

    public override string ToString() => _element.ToString();
}
