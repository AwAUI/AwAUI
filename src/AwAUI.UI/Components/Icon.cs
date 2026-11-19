using System;
using AwAUI.Svg;

namespace AwAUI.UI;

/// <summary>
/// 图标（design/22「图标：Icon 组件渲染 SVG 路径，颜色随 currentColor」）：渲染一个 <see cref="Svg"/> 元素，带 class <c>aw-icon</c>
/// （默认样式表：<c>1em × 1em</c>、不收缩，于是图标随字号缩放、随文字颜色着色）。
/// 有 <see cref="Label"/> 时角色为 <see cref="UI.Role.Image"/> 并以它为无障碍名称；没有时是装饰性的（<c>aria-hidden</c>）。
/// <code>
/// new Button { Children = [ new Icon(Icons.Search) { Label = "搜索" } ] }
/// </code>
/// </summary>
public sealed class Icon : Component
{
    public Icon() { }

    public Icon(SvgDocument? document) => Document = document;

    /// <summary>图标的 SVG 文档（不可变，通常是应用里的静态字段，按引用比较）。</summary>
    public SvgDocument? Document { get; set; }

    /// <summary>尺寸（CSS 长度，如 <c>16px</c>、<c>1.25em</c>）；null 时为默认样式表的 <c>1em</c>。</summary>
    public string? Size { get; set; }

    /// <summary>无障碍名称；null 表示装饰性图标。</summary>
    public string? Label { get; set; }

    /// <summary>附加的 class（与 <c>aw-icon</c> 合并）。</summary>
    public ClassValue Class { get; set; }

    protected override Node Build()
    {
        var svg = new Svg(Document)
        {
            Class = Class.IsEmpty ? (ClassValue)"aw-icon" : (ClassValue)("aw-icon " + Class),
            Role = Label is null ? Role.None : Role.Image,
            AriaLabel = Label,
        };
        if (Size is { Length: > 0 } size && IsSafeLength(size)) svg.Style = "width:" + size + ";height:" + size;
        if (Label is null) svg.Attributes["aria-hidden"] = "true";
        return svg;
    }

    /// <summary>尺寸只接受不含声明分隔符的单个值，避免拼接出额外的内联声明。</summary>
    static bool IsSafeLength(string s)
    {
        foreach (char c in s)
            if (c is ';' or '{' or '}' or ':' or '!' or '\n' or '\r') return false;
        return true;
    }

    // 没有 init Props：手写的 Props 胶水（运行时程序集不经过源生成器）

    protected override bool PropsEqual(Component other) =>
        other is Icon o && ReferenceEquals(o.Document, Document) && string.Equals(o.Size, Size, StringComparison.Ordinal)
        && string.Equals(o.Label, Label, StringComparison.Ordinal) && o.Class == Class;

    protected override void AdoptProps(Component other)
    {
        var o = (Icon)other;
        Document = o.Document;
        Size = o.Size;
        Label = o.Label;
        Class = o.Class;
    }
}
