using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Css;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>
/// <c>::before</c> / <c>::after</c> 生成的盒（CSS Generated Content 3 / CSS Pseudo-Elements 4）。它是帧管线私有的元素：
/// <see cref="Element.Parent"/> 是原始元素（继承、包含块链、裁剪链与事件冒泡都经过它），但不在原始元素的
/// <see cref="Element.Children"/> 中——协调器、选择器（<c>:first-child</c>、<c>:empty</c> ...）、焦点与 DevTools 的元素树都看不到它。
/// <para>
/// 原始元素是容器时它作为第一个 / 最后一个布局子节点（与 <c>display</c>、<c>position</c> 等属性一起像普通盒一样布局与绘制）；
/// 原始元素是段落（<c>text</c>、段落中的 <c>span</c>）时只把生成的文字以伪元素的样式加进段落。命中测试命中它时报告原始元素。
/// </para>
/// </summary>
internal sealed class GeneratedContentElement : Element
{
    public GeneratedContentElement(Element originating, PseudoElementKind kind) : base(kind == PseudoElementKind.Before ? "::before" : "::after")
    {
        Originating = originating;
        Kind = kind;
        _parent = originating;
        // 不经元素的脏标记（它不在子节点列表里，帧末的清理走不到它）：状态由下面的字段与原始元素的标记表达
        ClearDirty(ElementDirtyFlags.All);
    }

    /// <summary>原始元素。</summary>
    public Element Originating { get; }

    public PseudoElementKind Kind { get; }

    /// <summary><c>content</c> 求出的文字（字符串、<c>attr()</c>、引号）；空串表示没有文字的盒（装饰用）。</summary>
    public string Text = "";

    /// <summary>需要重新映射布局样式 / 重建段落。</summary>
    public bool LayoutDirty = true;

    /// <summary>只需要重建段落（颜色等绘制属性变化）。</summary>
    public bool PaintDirty;

    public override string ToString() => Originating + Tag;
}

public sealed partial class RenderPipeline
{
    /// <summary>当前存在的伪元素盒（原始元素被移出树后清理）。</summary>
    readonly HashSet<GeneratedContentElement> _generated = new();
    readonly List<GeneratedContentElement> _generatedScratch = new();
    bool _pruneGenerated;

    /// <summary>当前存在的 <c>::before / ::after</c> 盒的数目（测试与 DevTools 用）。</summary>
    internal int GeneratedBoxCount => _generated.Count;

    /// <summary>元素的 <c>::before</c> / <c>::after</c> 盒（没有时为 null）。</summary>
    internal (GeneratedContentElement? Before, GeneratedContentElement? After) GetGenerated(Element e)
    {
        if (_generated.Count == 0 || e is GeneratedContentElement || e.IsTextNode || e.Render is not { } box) return (null, null);
        var view = box.GetView(this);
        return (view.Before, view.After);
    }

    /// <summary>经 <see cref="UIRoot.Pipeline"/> 找到元素的伪元素盒（<see cref="LayoutMapper.PaintOrder"/> 用）。</summary>
    internal static (GeneratedContentElement? Before, GeneratedContentElement? After) GeneratedOf(Element e) =>
        e.Root?.Pipeline is RenderPipeline p ? p.GetGenerated(e) : (null, null);

    // ------------------------------------------------------------ 样式

    /// <summary>
    /// 原始元素的样式重算之后：解析它的伪元素样式（只在样式表中可能有匹配规则时，见 <see cref="StyleInvalidation.MayGenerateContent"/>），
    /// 创建、更新或移除伪元素盒；输入框解析 <c>::placeholder</c> / <c>::selection</c>；登记查询容器。
    /// </summary>
    void UpdatePseudoElements(Element e, RenderBox box)
    {
        var style = box.Style;
        if (style is null) return;
        if (style.ContainerType != CssContainerType.Normal) _queryContainers.Add(e);
        // 经典滚动条的槽取决于 overflow / scrollbar-gutter / scrollbar-width（后两者不在布局分组里，单独检查）
        if ((_gutterBoxes.Count > 0 || EffectiveScrollbarStyle == ScrollbarStyle.Classic) && GutterStale(e, box))
        {
            box.LayoutStyleDirty = true;
            e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        }
        var inv = _invalidation;
        if (e is InputElement)
        {
            if (inv.UsesPlaceholder || inv.UsesSelection) UpdateInputPseudo(e, box, style);
            return;
        }
        bool may = style.Display != CssDisplay.None && e is not ReplacedElement && inv.MayGenerateContent(e);
        if (!may && _generated.Count == 0) return;
        var view = box.GetView(this);
        if (!may && view.Before is null && view.After is null) return;
        UpdateGenerated(e, box, view, ref view.Before, PseudoElementKind.Before, may);
        UpdateGenerated(e, box, view, ref view.After, PseudoElementKind.After, may);
    }

    void UpdateGenerated(Element e, RenderBox box, ElementStyleView view, ref GeneratedContentElement? slot, PseudoElementKind kind, bool may)
    {
        ComputedStyle? ps = null;
        string? text = null;
        if (may)
        {
            try
            {
                ps = _engine.Resolve(view, box.Style, kind, null);
            }
            catch (InsufficientExecutionStackException)
            {
                ps = null;
            }
            if (ps is not null) text = GeneratedText(ps);
        }
        if (text is null || ps is null)
        {
            if (slot is not null)
            {
                RemoveGenerated(e, slot);
                slot = null;
            }
            return;
        }
        var g = slot;
        if (g is null)
        {
            g = slot = new GeneratedContentElement(e, kind);
            _generated.Add(g);
            LayeringChanged();
        }
        g._root = e._root;
        var gb = RenderBox.Get(g);
        var old = gb.Style;
        if (!string.Equals(g.Text, text, StringComparison.Ordinal))
        {
            g.Text = text;
            g.LayoutDirty = true;
        }
        if (!ReferenceEquals(old, ps))
        {
            gb.Style = ps;
            var diff = ComputedStyle.Diff(old, ps);
            if ((diff & (StyleDifference.Structure | StyleDifference.Layout)) != 0)
            {
                gb.LayoutStyleDirty = true;
                g.LayoutDirty = true;
            }
            else if ((diff & (StyleDifference.Paint | StyleDifference.Composite)) != 0 || (old is not null && DecorationChanged(old, ps)))
                g.PaintDirty = true;
            if (AffectsLayering(old, ps)) LayeringChanged();
        }
        if (g.LayoutDirty || g.PaintDirty)
        {
            // 原始元素经布局阶段同步伪元素（容器：布局子节点；段落：重建段落）
            e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
            if (gb.HasBox)
            {
                DamageBox(gb);
                InvalidateCache(gb);
            }
        }
    }

    void RemoveGenerated(Element e, GeneratedContentElement g)
    {
        _generated.Remove(g);
        if (g.Render is { } gb)
        {
            if (gb.HasBox)
            {
                DamageBox(gb);
                InvalidateCache(gb);
            }
            gb.HasBox = false;
            gb.Geometry = default;
            gb.Layout?.Parent?.RemoveChild(gb.Layout);
        }
        g._root = null;
        LayeringChanged();
        e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
    }

    /// <summary>
    /// <c>content</c> 的文字；不生成盒（<c>normal</c>、<c>none</c>、<c>display: none</c>）时为 null。
    /// 字符串与 <c>attr()</c>（计算值阶段已替换）原样连接；<c>open-quote / close-quote</c> 取 <c>quotes</c> 的第一对（auto 为弯引号）；
    /// 计数器（未实现计数器作用域）与图像（不支持图像内容）不产生文字。
    /// </summary>
    internal static string? GeneratedText(ComputedStyle ps)
    {
        if (ps.Display == CssDisplay.None) return null;
        var content = ps.Content;
        if (content.Kind != CssContentKind.List) return null;
        var items = content.Items;
        if (items.Count == 1 && items[0].Kind == CssContentItemKind.String) return items[0].Text ?? "";
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case CssContentItemKind.String:
                    sb.Append(item.Text);
                    break;
                case CssContentItemKind.OpenQuote:
                case CssContentItemKind.CloseQuote:
                {
                    bool open = item.Kind == CssContentItemKind.OpenQuote;
                    var q = ps.Quotes;
                    if (q.IsAuto) sb.Append(open ? '“' : '”');
                    else if (q.Strings.Count >= 2) sb.Append(q.Strings[open ? 0 : 1]);
                    break;
                }
            }
            // 生成内容的长度有上限（不可信的样式表不能让一个伪元素产生无界的段落）
            if (sb.Length > MaxGeneratedText) break;
        }
        return sb.Length > MaxGeneratedText ? sb.ToString(0, MaxGeneratedText) : sb.ToString();
    }

    /// <summary>一个伪元素生成文字的长度上限（UTF-16 单元）。</summary>
    internal const int MaxGeneratedText = 4096;

    /// <summary>输入框的 <c>::placeholder</c> / <c>::selection</c>：变化时重建段落（占位文字的样式）或重绘（选区颜色）。</summary>
    void UpdateInputPseudo(Element e, RenderBox box, ComputedStyle style)
    {
        var view = box.GetView(this);
        var inv = _invalidation;
        var ph = inv.UsesPlaceholder ? ResolvePseudo(view, style, PseudoElementKind.Placeholder) : null;
        var sel = inv.UsesSelection ? ResolvePseudo(view, style, PseudoElementKind.Selection) : null;
        if (!ReferenceEquals(ph, view.PlaceholderStyle))
        {
            bool same = ph is not null && view.PlaceholderStyle is not null && ComputedStyle.Diff(view.PlaceholderStyle, ph) == StyleDifference.None;
            view.PlaceholderStyle = ph;
            if (!same) e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        }
        if (!ReferenceEquals(sel, view.SelectionStyle))
        {
            view.SelectionStyle = sel;
            e.MarkDirty(ElementDirtyFlags.Paint);
        }
    }

    ComputedStyle? ResolvePseudo(ElementStyleView view, ComputedStyle parent, PseudoElementKind kind)
    {
        try
        {
            return _engine.Resolve(view, parent, kind, null);
        }
        catch (InsufficientExecutionStackException)
        {
            return null;
        }
    }

    /// <summary>输入框占位文字的文本样式：有 <c>::placeholder</c> 规则时按其计算样式，否则沿用半透明的文字颜色。</summary>
    internal TextStyle PlaceholderTextStyle(Element input, ComputedStyle style) =>
        input.Render?.GetView(this).PlaceholderStyle is { } ph ? _textStyles.Get(ph) : _textStyles.Get(style, 0.5f);

    /// <summary>输入框选区的高亮色：<c>::selection</c> 的背景色（未指定或透明时为 null，用默认色）。</summary>
    internal Color? SelectionHighlight(Element input)
    {
        if (input.Render?.GetView(this).SelectionStyle is not { } sel) return null;
        var c = sel.ResolveColor(sel.BackgroundColor);
        return c.IsTransparent ? null : c;
    }

    // ------------------------------------------------------------ 命中测试

    /// <summary>命中伪元素的盒时报告原始元素（伪元素不是事件目标，CSS Pseudo-Elements 4 §4）。</summary>
    static Element? MapGeneratedHit(Element? e)
    {
        if (e is GeneratedContentElement g) return g.Originating.IsAttached ? g.Originating : null;
        return e;
    }

    sealed class GeneratedAwareHitTester : HitTester
    {
        readonly RenderPipeline _p;

        public GeneratedAwareHitTester(RenderPipeline p) => _p = p;

        public override Element? HitTest(Point position) => MapGeneratedHit(_p.HitTestCore(position));
    }

    // ------------------------------------------------------------ 布局

    /// <summary>容器的伪元素盒：映射布局样式、按需重建段落（文字非空时是段落叶子，否则是没有子节点的容器）。</summary>
    int SyncGenerated(GeneratedContentElement? g, bool force)
    {
        if (g is null || g.Render is not { Style: not null } gb) return 0;
        bool created = false;
        if (gb.Layout is null)
        {
            gb.Layout = new LayoutNode { Owner = gb };
            created = true;
        }
        var node = gb.Layout;
        var kind = g.Text.Length > 0 ? BoxKind.Paragraph : BoxKind.Container;
        if (gb.Kind != kind)
        {
            gb.Kind = kind;
            gb.LayoutStyleDirty = true;
            created = true;
        }
        MapLayoutStyle(gb, node);
        int paragraphs = 0;
        if (kind == BoxKind.Paragraph)
        {
            bool layoutChange = created || force || g.LayoutDirty;
            if (gb.Paragraph is null || layoutChange || g.PaintDirty)
            {
                paragraphs++;
                var tl = BuildParagraph(gb, out bool empty, out float maxFont);
                if (gb.Paragraph is null) gb.Paragraph = new TextParagraph(tl, empty);
                else
                {
                    gb.Paragraph.Layout = tl;
                    gb.Paragraph.IsEmpty = empty;
                }
                gb.Paragraph.MaxFontSize = maxFont;
                if (!ReferenceEquals(node.Measure, gb.Paragraph)) node.Measure = gb.Paragraph;
                if (layoutChange) node.MarkDirty();
            }
        }
        else
        {
            if (node.Measure is not null) node.Measure = null;
            gb.Paragraph = null;
            if (created || g.LayoutDirty) node.MarkDirty();
        }
        g.LayoutDirty = g.PaintDirty = false;
        return paragraphs;
    }

    /// <summary>段落中的伪元素文字（原始元素是段落或段落内的行内元素）：以伪元素的样式加进段落。</summary>
    void AddGeneratedInline(GeneratedContentElement? g, ParagraphBuilder builder)
    {
        if (g is null || g.Text.Length == 0 || g.Render?.Style is not { } ps) return;
        // 伪元素的装饰叠加在原始元素（及其祖先）传播来的装饰上
        var outer = _inlineDecorations.Count > 0 ? _inlineDecorations.Peek() : _paragraphDecoration;
        float shift = _inlineShifts.Count > 0 ? _inlineShifts.Peek() : 0;
        builder.PushStyle(_textStyles.Get(ps, 1f, outer, shift));
        if (ps.FontSize > _maxFontSize) _maxFontSize = ps.FontSize;
        builder.AddText(g.Text);
        builder.Pop();
        g.LayoutDirty = g.PaintDirty = false;
    }

    /// <summary>清理原始元素已离开树的伪元素。移除子树时已经按整个子树的旧墨迹计入损伤。</summary>
    void PruneGeneratedContent()
    {
        if (!_pruneGenerated) return;
        _pruneGenerated = false;
        var stale = _generatedScratch;
        foreach (var g in _generated)
            if (!g.Originating.IsAttached || !ReferenceEquals(g.Originating.Root, _root)) stale.Add(g);
        foreach (var g in stale)
        {
            _generated.Remove(g);
            g._root = null;
            if (g.Render is { } gb) gb.Layout?.Parent?.RemoveChild(gb.Layout);
            if (g.Originating.Render is { } ob && ob.GetView(this) is { } v)
            {
                if (ReferenceEquals(v.Before, g)) v.Before = null;
                if (ReferenceEquals(v.After, g)) v.After = null;
            }
        }
        stale.Clear();
    }

    // ------------------------------------------------------------ 文本属性的失效

    /// <summary>文本装饰（不继承，但传播到后代的文字）是否变化。</summary>
    static bool DecorationChanged(ComputedStyle a, ComputedStyle b) =>
        !ReferenceEquals(a.EffectsGroup, b.EffectsGroup)
        && (a.TextDecorationLine != b.TextDecorationLine || a.TextDecorationStyle != b.TextDecorationStyle
            || !a.TextDecorationColor.Equals(b.TextDecorationColor) || !a.TextDecorationThickness.Equals(b.TextDecorationThickness));

    /// <summary>
    /// 元素的文本装饰变化：它传播到的后代段落（<c>text</c>、<c>span</c>、裸文本）要重建。不进入脱离常规流的后代（装饰不传播给它们）。
    /// </summary>
    static void MarkDecoratedText(Element e)
    {
        var stack = new Stack<Element>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            var ch = x.Children;
            for (int i = 0; i < ch.Count; i++)
            {
                var c = ch[i];
                if (c.IsTextNode)
                {
                    c.MarkDirty(ElementDirtyFlags.Paint);
                    continue;
                }
                if (c.Render?.Style is { } cs && cs.Position is CssPosition.Absolute or CssPosition.Fixed) continue;
                if (c.Render is { Kind: BoxKind.Paragraph or BoxKind.Inline }) c.MarkDirty(ElementDirtyFlags.Paint);
                stack.Push(c);
            }
        }
    }

    /// <summary>继承的文本属性中影响断行与度量的部分是否变化（文本节点与父元素共享样式，需要单独判断）。</summary>
    static bool TextLayoutChanged(TextStyleGroup a, TextStyleGroup b) =>
        a.LetterSpacing != b.LetterSpacing || a.WordSpacing != b.WordSpacing || !a.TextIndent.Equals(b.TextIndent) || !a.TabSize.Equals(b.TabSize)
        || a.TextWrapMode != b.TextWrapMode || a.TextWrapStyle != b.TextWrapStyle || a.TextTransform != b.TextTransform || a.Hyphens != b.Hyphens;

    /// <summary>继承的文本属性中只影响绘制的部分（阴影、下划线位置、填充色）是否变化。</summary>
    static bool TextPaintChanged(TextStyleGroup a, TextStyleGroup b) =>
        !a.TextShadow.Equals(b.TextShadow) || !a.TextUnderlineOffset.Equals(b.TextUnderlineOffset)
        || a.TextUnderlinePosition != b.TextUnderlinePosition || !a.WebkitTextFillColor.Equals(b.WebkitTextFillColor);
}
