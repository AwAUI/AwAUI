using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics.Text;
using AwAUI.Layout;
using AwAUI.Text;
using TextLineHeight = AwAUI.Text.LineHeight;

namespace AwAUI.UI;

/// <summary>
/// 段落叶子的测量（design/07「文本测量」）：内容盒约束 → <see cref="TextLayout.Layout"/>；min-content / max-content 用
/// <see cref="TextLayout.MinIntrinsicWidth"/> / <see cref="TextLayout.MaxIntrinsicWidth"/>（不触发断行）；基线用
/// <see cref="TextLayout.FirstBaseline"/>。同一个 <see cref="TextLayout"/> 在内容与样式不变时跨帧复用，绘制直接使用它。
/// </summary>
internal sealed class TextParagraph : LayoutMeasure
{
    /// <summary>
    /// 断行宽度的余量：测量得到的 max-content 宽度回传为盒宽后再按该宽度断行时，浮点累加误差可能让最后一个词换到下一行。
    /// </summary>
    internal const float WidthSlack = 0.01f;

    public TextParagraph(TextLayout layout, bool isEmpty)
    {
        Layout = layout;
        IsEmpty = isEmpty;
    }

    /// <summary>
    /// 当前段落。只有绘制属性（颜色）变化时宿主原地换成新段落而不标脏布局节点：新旧段落的度量相同，引擎缓存的测量结果仍然有效。
    /// </summary>
    public TextLayout Layout { get; set; }

    /// <summary>没有任何文本（空的 <c>text</c> 元素）：与浏览器一样不产生行盒，尺寸为 0。</summary>
    public bool IsEmpty { get; set; }

    /// <summary>
    /// 段落中最大的字号（段落自身与行内后代）。字形可能伸出行盒（<c>line-height</c> 小于字体的内容区、斜体悬垂），
    /// 墨迹范围（损伤区域与剔除）按它外扩。
    /// </summary>
    public float MaxFontSize { get; set; }

    public override Size Measure(LayoutNode node, in MeasureConstraints constraints)
    {
        if (IsEmpty) return default;
        var tl = Layout;
        switch (constraints.Width.Kind)
        {
            case AvailableSpaceKind.Definite:
                tl.Layout(constraints.Width.Value + WidthSlack);
                return new Size(Math.Min(tl.Width, constraints.Width.Value), tl.Height);
            case AvailableSpaceKind.MinContent:
            {
                float w = tl.MinIntrinsicWidth;
                tl.Layout(w + WidthSlack);
                return new Size(w, tl.Height);
            }
            default:
            {
                tl.Layout(float.PositiveInfinity);
                return new Size(tl.MaxIntrinsicWidth, tl.Height);
            }
        }
    }

    public override float GetBaseline(LayoutNode node, Size contentSize)
    {
        if (IsEmpty) return float.NaN;
        Layout.Layout(contentSize.Width + WidthSlack);
        return Layout.FirstBaseline;
    }
}

/// <summary>计算样式 → 文本栈样式（<see cref="TextStyle"/> / <see cref="ParagraphStyle"/>）。</summary>
internal sealed class TextStyleMapper
{
    // 按（字体分组, 文本分组）缓存：同一组合的 TextStyle 复用同一实例，ParagraphBuilder 可以合并相邻片段
    readonly Dictionary<(FontStyleGroup, TextStyleGroup), TextStyle> _cache = new();
    const int MaxCacheEntries = 4096;

    public TextStyle Get(ComputedStyle s, float alphaFactor = 1f)
    {
        if (alphaFactor != 1f) return Create(s, alphaFactor);
        var key = (s.FontGroup, s.TextGroup);
        if (_cache.TryGetValue(key, out var ts)) return ts;
        ts = Create(s, 1f);
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[key] = ts;
        return ts;
    }

    public void Clear() => _cache.Clear();

    static TextStyle Create(ComputedStyle s, float alphaFactor)
    {
        var color = s.ResolveColor(s.Color);
        if (alphaFactor != 1f) color = color.WithAlpha(color.A * alphaFactor);
        return new TextStyle
        {
            FontFamilies = MapFamilies(s.FontFamily),
            FontSize = s.FontSize,
            FontWeight = new FontWeight((int)Math.Round(Math.Max(1, Math.Min(1000, s.FontWeight)))),
            FontStyle = s.FontStyle switch
            {
                CssFontStyle.Italic => Graphics.Text.FontStyle.Italic,
                CssFontStyle.Oblique => Graphics.Text.FontStyle.Oblique,
                _ => Graphics.Text.FontStyle.Normal,
            },
            FontStretch = MapStretch(s.FontStretch),
            Color = color,
        };
    }

    static IReadOnlyList<string> MapFamilies(FontFamilyList list)
    {
        var f = list.Families;
        if (f.Count == 0) return Array.Empty<string>();
        var names = new string[f.Count];
        for (int i = 0; i < names.Length; i++) names[i] = f[i].Name;
        return names;
    }

    static FontStretch MapStretch(float percent)
    {
        // CSS 关键字对应的百分比：50 62.5 75 87.5 100 112.5 125 150 200，取最近的
        ReadOnlySpan<float> table = stackalloc float[] { 50f, 62.5f, 75f, 87.5f, 100f, 112.5f, 125f, 150f, 200f };
        int best = 4;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < table.Length; i++)
        {
            float d = Math.Abs(table[i] - percent);
            if (d < bestDiff)
            {
                bestDiff = d;
                best = i;
            }
        }
        return (FontStretch)(best + 1);
    }

    public ParagraphStyle CreateParagraphStyle(ComputedStyle s, TextStyle textStyle, bool singleLine)
    {
        var ws = s.WhiteSpace switch
        {
            CssWhiteSpace.Pre => WhiteSpace.Pre,
            CssWhiteSpace.Nowrap => WhiteSpace.NoWrap,
            CssWhiteSpace.PreWrap => WhiteSpace.PreWrap,
            CssWhiteSpace.BreakSpaces => WhiteSpace.BreakSpaces,
            CssWhiteSpace.PreLine => WhiteSpace.PreLine,
            _ => WhiteSpace.Normal,
        };
        if (singleLine && ws is WhiteSpace.Normal or WhiteSpace.PreLine or WhiteSpace.PreWrap or WhiteSpace.BreakSpaces)
            ws = ws == WhiteSpace.Normal || ws == WhiteSpace.PreLine ? WhiteSpace.NoWrap : WhiteSpace.Pre;
        var overflowWrap = s.OverflowWrap switch
        {
            CssOverflowWrap.BreakWord => OverflowWrap.BreakWord,
            CssOverflowWrap.Anywhere => OverflowWrap.Anywhere,
            _ => OverflowWrap.Normal,
        };
        var wordBreak = WordBreak.Normal;
        switch (s.WordBreak)
        {
            case CssWordBreak.BreakAll: wordBreak = WordBreak.BreakAll; break;
            case CssWordBreak.KeepAll: wordBreak = WordBreak.KeepAll; break;
            // word-break: break-word（已废弃）= normal + overflow-wrap: anywhere
            case CssWordBreak.BreakWord: overflowWrap = OverflowWrap.Anywhere; break;
        }
        var lh = s.LineHeight;
        var lineHeight = lh.Kind switch
        {
            LineHeightKind.Number => TextLineHeight.Multiple(lh.Value),
            LineHeightKind.Length => TextLineHeight.Pixels(lh.Value),
            _ => TextLineHeight.Normal,
        };
        // text-overflow: ellipsis 只在溢出被裁剪时生效
        bool ellipsis = s.TextOverflow == CssTextOverflow.Ellipsis && s.OverflowX != CssOverflow.Visible;
        return new ParagraphStyle
        {
            TextStyle = textStyle,
            TextAlign = s.TextAlign switch
            {
                CssTextAlign.End => TextAlign.End,
                CssTextAlign.Left => TextAlign.Left,
                CssTextAlign.Right => TextAlign.Right,
                CssTextAlign.Center => TextAlign.Center,
                CssTextAlign.Justify => TextAlign.Justify,
                _ => TextAlign.Start,
            },
            LineHeight = lineHeight,
            WhiteSpace = ws,
            WordBreak = wordBreak,
            OverflowWrap = overflowWrap,
            Ellipsis = ellipsis ? ParagraphStyle.HorizontalEllipsis : null,
        };
    }
}
