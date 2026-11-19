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
