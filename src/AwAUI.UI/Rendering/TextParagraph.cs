using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics.Text;
using AwAUI.Layout;
using AwAUI.Text;
using TextLineHeight = AwAUI.Text.LineHeight;
using TextDecorationStyle = AwAUI.Text.TextDecorationStyle;
using TextWrapStyle = AwAUI.Text.TextWrapStyle;

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

    /// <summary>
    /// 内容不决定行内尺寸（输入框的 <c>field-sizing: fixed</c>，CSS 的默认值）：测量宽度为 0，盒宽只由 <c>width</c> / <c>min-width</c>
    /// 等决定，较长的文字在框内水平滚动。为 false 时（<c>field-sizing: content</c>）宽度随文字（或占位文字）增长。
    /// </summary>
    public bool FixedInlineSize { get; set; }

    public override Size Measure(LayoutNode node, in MeasureConstraints constraints)
    {
        if (IsEmpty) return default;
        var tl = Layout;
        if (FixedInlineSize)
        {
            tl.Layout(float.PositiveInfinity);
            return new Size(0, tl.Height);
        }
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

/// <summary>
/// 文本装饰（<c>text-decoration-*</c>）的使用值：装饰不继承，但传播到装饰盒内常规流后代的文字（CSS Text Decoration 3 §2），
/// 所以段落的文字样式由「自身的装饰 ∪ 祖先传播来的装饰」决定。颜色在装饰盒上解析（<c>currentColor</c> 取装饰盒的颜色）。
/// </summary>
internal readonly struct DecorationInfo : IEquatable<DecorationInfo>
{
    public DecorationInfo(TextDecorationLine lines, TextDecorationStyle style, Color color, float thickness)
    {
        Lines = lines;
        Style = style;
        Color = color;
        Thickness = thickness;
    }

    public TextDecorationLine Lines { get; }
    public TextDecorationStyle Style { get; }
    public Color Color { get; }

    /// <summary>粗细（px）；NaN 表示 auto / from-font（取字体度量）。</summary>
    public float Thickness { get; }

    public bool IsNone => Lines == TextDecorationLine.None;

    /// <summary>元素自身的装饰。</summary>
    public static DecorationInfo Of(ComputedStyle s)
    {
        var css = s.TextDecorationLine;
        var lines = TextDecorationLine.None;
        if ((css & CssTextDecorationLine.Underline) != 0) lines |= TextDecorationLine.Underline;
        if ((css & CssTextDecorationLine.Overline) != 0) lines |= TextDecorationLine.Overline;
        if ((css & CssTextDecorationLine.LineThrough) != 0) lines |= TextDecorationLine.LineThrough;
        if (lines == TextDecorationLine.None) return default;
        var style = s.TextDecorationStyle switch
        {
            CssTextDecorationStyle.Double => TextDecorationStyle.Double,
            CssTextDecorationStyle.Dotted => TextDecorationStyle.Dotted,
            CssTextDecorationStyle.Dashed => TextDecorationStyle.Dashed,
            CssTextDecorationStyle.Wavy => TextDecorationStyle.Wavy,
            _ => TextDecorationStyle.Solid,
        };
        var t = s.TextDecorationThickness;
        // 百分比相对 1em
        float thickness = t.Kind == CssDecorationThicknessKind.Length ? Math.Max(0, t.Length.Resolve(s.FontSize)) : float.NaN;
        return new DecorationInfo(lines, style, s.ResolveColor(s.TextDecorationColor), thickness);
    }

    /// <summary>内层（更近的装饰盒）与外层合并：线条取并集，样式、颜色与粗细取内层（内层没有装饰时取外层）。</summary>
    public static DecorationInfo Combine(in DecorationInfo inner, in DecorationInfo outer)
    {
        if (outer.IsNone) return inner;
        if (inner.IsNone) return outer;
        return new DecorationInfo(inner.Lines | outer.Lines, inner.Style, inner.Color, inner.Thickness);
    }

    /// <summary>
    /// 从 <paramref name="start"/>（其样式是段落的基础样式）的父元素起向上收集传播来的装饰。脱离常规流的元素与原子行内盒
    /// （inline-block / inline-flex / inline-grid）不接收外层的装饰；最多向上 256 层。
    /// </summary>
    public static DecorationInfo Propagated(Element? start)
    {
        var result = default(DecorationInfo);
        var c = start;
        for (int depth = 0; c is not null && depth < 256; depth++)
        {
            var cs = c.Render?.Style;
            if (cs is null || cs.Position is CssPosition.Absolute or CssPosition.Fixed
                || cs.Display is CssDisplay.InlineBlock or CssDisplay.InlineFlex or CssDisplay.InlineGrid or CssDisplay.InlineTable)
                break;
            var a = c.Parent;
            if (a?.Render?.Style is not { } ast) break;
            var own = Of(ast);
            if (!own.IsNone) result = Combine(result, own);
            c = a;
        }
        return result;
    }

    public bool Equals(DecorationInfo o) => Lines == o.Lines && (Lines == TextDecorationLine.None
        || (Style == o.Style && Color.Equals(o.Color) && Thickness.Equals(o.Thickness)));
    public override bool Equals(object? obj) => obj is DecorationInfo d && Equals(d);
    public override int GetHashCode() => Lines == TextDecorationLine.None ? 0 : HashCodeCompat.Combine((int)Lines, (int)Style, Color.GetHashCode(), Thickness.GetHashCode());
}

/// <summary>计算样式 → 文本栈样式（<see cref="TextStyle"/> / <see cref="ParagraphStyle"/>）。</summary>
internal sealed class TextStyleMapper
{
    // 按（字体分组, 文本分组, 装饰, 基线偏移, 语言）缓存：同一组合的 TextStyle 复用同一实例，ParagraphBuilder 可以合并相邻片段
    readonly Dictionary<(FontStyleGroup, TextStyleGroup, DecorationInfo, float, string?), TextStyle> _cache = new();
    const int MaxCacheEntries = 4096;

    /// <summary>
    /// 进程的语言环境（BCP 47）：解析 <c>locl</c> 特性、汉字变体与字体回退（design/19「字体回退」）。
    /// 与 Avalonia 相同，内建控件没有独立的 <c>Language</c> 属性，默认取 <see cref="CultureInfo.CurrentUICulture"/>；
    /// 元素上的 <c>lang</c> 目前只参与 <c>:lang()</c> 选择器。
    /// </summary>
    static readonly string? s_locale = CultureInfo.CurrentUICulture.Name is { Length: > 0 } name ? name : null;

    /// <summary>每个样式最多映射的 OpenType 特性数（font-feature-settings 与 font-variant-* 合计）。</summary>
    const int MaxFeatures = 64;

    /// <param name="inherited">祖先传播来的文本装饰（与自身的合并，见 <see cref="DecorationInfo.Propagated"/>）。</param>
    /// <param name="baselineShift">行内元素的 <c>vertical-align</c> 换算出的基线偏移（px，向上为正，见 <see cref="BaselineShiftOf"/>）。</param>
    public TextStyle Get(ComputedStyle s, float alphaFactor = 1f, DecorationInfo inherited = default, float baselineShift = 0)
    {
        var deco = DecorationInfo.Combine(DecorationInfo.Of(s), inherited);
        if (alphaFactor != 1f) return Create(s, alphaFactor, deco, baselineShift);
        var key = (s.FontGroup, s.TextGroup, deco, baselineShift, s_locale);
        if (_cache.TryGetValue(key, out var ts)) return ts;
        ts = Create(s, 1f, deco, baselineShift);
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[key] = ts;
        return ts;
    }

    public void Clear() => _cache.Clear();

    /// <summary>
    /// 段落中行内元素的 <c>vertical-align</c> 换算为基线偏移（px，向上为正）：<c>sub</c> 降低父元素字号的 0.2 倍、<c>super</c>
    /// 抬高 0.34 倍（与常见浏览器的默认下标 / 上标位置相近），长度与百分比（相对自身的行高）原样；其余关键字（middle、top ...）
    /// 需要行内盒的对齐，段落中按 baseline 处理。
    /// </summary>
    public static float BaselineShiftOf(ComputedStyle s, ComputedStyle? parent)
    {
        var va = s.VerticalAlign;
        float parentSize = parent?.FontSize ?? s.FontSize;
        float v = va.Kind switch
        {
            CssVerticalAlignKind.Sub => -0.2f * parentSize,
            CssVerticalAlignKind.Super => 0.34f * parentSize,
            CssVerticalAlignKind.Length => va.Length.Resolve(LineHeightPx(s)),
            _ => 0,
        };
        return float.IsNaN(v) || float.IsInfinity(v) ? 0 : Math.Max(-TextStyle.MaxFontSize, Math.Min(TextStyle.MaxFontSize, v));
    }

    static float LineHeightPx(ComputedStyle s) => s.LineHeight.Kind switch
    {
        LineHeightKind.Number => s.LineHeight.Value * s.FontSize,
        LineHeightKind.Length => s.LineHeight.Value,
        _ => 1.2f * s.FontSize,
    };

    static TextStyle Create(ComputedStyle s, float alphaFactor, in DecorationInfo deco, float baselineShift)
    {
        // -webkit-text-fill-color 的初始值是 currentColor（即 color）
        var color = s.ResolveColor(s.WebkitTextFillColor);
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
            LetterSpacing = Finite(s.LetterSpacing),
            WordSpacing = Finite(s.WordSpacing),
            TextTransform = s.TextTransform switch
            {
                CssTextTransform.Uppercase => TextTransform.Uppercase,
                CssTextTransform.Lowercase => TextTransform.Lowercase,
                CssTextTransform.Capitalize => TextTransform.Capitalize,
                _ => TextTransform.None,
            },
            Decoration = deco.Lines,
            DecorationStyle = deco.Style,
            DecorationColor = deco.IsNone ? null : alphaFactor != 1f ? deco.Color.WithAlpha(deco.Color.A * alphaFactor) : deco.Color,
            DecorationThickness = deco.IsNone ? float.NaN : deco.Thickness,
            UnderlineOffset = s.TextUnderlineOffset.Kind == SizeKind.LengthPercentage ? Finite(s.TextUnderlineOffset.Value.Resolve(s.FontSize)) : float.NaN,
            UnderlinePosition = (s.TextUnderlinePosition & CssTextUnderlinePosition.Under) != 0 ? UnderlinePosition.Under : UnderlinePosition.Auto,
            Shadows = MapShadows(s, alphaFactor),
            Features = MapFeatures(s),
            Variation = MapVariation(s.FontVariationSettings),
            BaselineShift = baselineShift,
            Locale = s_locale,
        };
    }

    static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;

    static IReadOnlyList<TextShadow>? MapShadows(ComputedStyle s, float alphaFactor)
    {
        var list = s.TextShadow;
        if (list.IsNone) return null;
        int n = Math.Min(list.Shadows.Count, TextStyle.MaxShadows);
        var result = new TextShadow[n];
        for (int i = 0; i < n; i++)
        {
            var sh = list.Shadows[i];
            var c = s.ResolveColor(sh.Color);
            if (alphaFactor != 1f) c = c.WithAlpha(c.A * alphaFactor);
            result[i] = new TextShadow(sh.OffsetX, sh.OffsetY, sh.Blur, c);
        }
        return result;
    }

    /// <summary>
    /// OpenType 特性：<c>font-variant-numeric</c>、<c>font-variant-caps</c>、<c>font-kerning</c> 先映射为对应特性
    /// （CSS Fonts 4 §6），<c>font-feature-settings</c> 的显式设置排在后面（同一特性以它为准）。
    /// </summary>
    static IReadOnlyList<FontFeature>? MapFeatures(ComputedStyle s)
    {
        List<FontFeature>? list = null;
        void Add(string tag, uint value = 1)
        {
            if ((list ??= new List<FontFeature>()).Count < MaxFeatures) list.Add(new FontFeature(tag, value));
        }
        var n = s.FontVariantNumeric;
        if (n != CssFontVariantNumeric.Normal)
        {
            if ((n & CssFontVariantNumeric.LiningNums) != 0) Add("lnum");
            if ((n & CssFontVariantNumeric.OldstyleNums) != 0) Add("onum");
            if ((n & CssFontVariantNumeric.ProportionalNums) != 0) Add("pnum");
            if ((n & CssFontVariantNumeric.TabularNums) != 0) Add("tnum");
            if ((n & CssFontVariantNumeric.DiagonalFractions) != 0) Add("frac");
            if ((n & CssFontVariantNumeric.StackedFractions) != 0) Add("afrc");
            if ((n & CssFontVariantNumeric.Ordinal) != 0) Add("ordn");
            if ((n & CssFontVariantNumeric.SlashedZero) != 0) Add("zero");
        }
        switch (s.FontVariantCaps)
        {
            case CssFontVariantCaps.SmallCaps: Add("smcp"); break;
            case CssFontVariantCaps.AllSmallCaps: Add("c2sc"); Add("smcp"); break;
            case CssFontVariantCaps.PetiteCaps: Add("pcap"); break;
            case CssFontVariantCaps.AllPetiteCaps: Add("c2pc"); Add("pcap"); break;
            case CssFontVariantCaps.Unicase: Add("unic"); break;
            case CssFontVariantCaps.TitlingCaps: Add("titl"); break;
        }
        if (s.FontKerning == CssFontKerning.None) Add("kern", 0);
        foreach (var f in s.FontFeatureSettings)
        {
            if (f.Tag is not { Length: 4 } tag) continue;
            Add(tag, (uint)Math.Max(0, f.Value));
        }
        return list;
    }

    static FontVariation MapVariation(CssList<CssFontVariation> settings)
    {
        if (settings.Count == 0) return default;
        var list = new List<VariationSetting>(Math.Min(settings.Count, MaxFeatures));
        foreach (var v in settings)
        {
            if (list.Count >= MaxFeatures) break;
            if (v.Tag is not { Length: 4 } tag || float.IsNaN(v.Value) || float.IsInfinity(v.Value)) continue;
            uint t = ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
            list.Add(new VariationSetting(t, v.Value));
        }
        return list.Count == 0 ? default : new FontVariation(list.ToArray());
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
        // text-wrap-mode: nowrap（white-space 的换行部分）：保留空白的模式变为 pre，其余为 nowrap
        if (s.TextWrapMode == CssTextWrapMode.Nowrap)
            ws = ws is WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.BreakSpaces or WhiteSpace.PreLine ? WhiteSpace.Pre : WhiteSpace.NoWrap;
        var lh = s.LineHeight;
        var lineHeight = lh.Kind switch
        {
            LineHeightKind.Number => TextLineHeight.Multiple(lh.Value),
            LineHeightKind.Length => TextLineHeight.Pixels(lh.Value),
            _ => TextLineHeight.Normal,
        };
        // text-overflow: ellipsis 只在溢出被裁剪时生效
        bool ellipsis = s.TextOverflow == CssTextOverflow.Ellipsis && s.OverflowX != CssOverflow.Visible;
        // -webkit-line-clamp：只在 display: -webkit-box / -webkit-inline-box 且 -webkit-box-orient: vertical 时生效，最后一行加省略号
        int maxLines = 0;
        if (s.WebkitLineClamp > 0 && s.Display is CssDisplay.WebkitBox or CssDisplay.WebkitInlineBox && s.WebkitBoxOrient is CssBoxOrient.Vertical or CssBoxOrient.BlockAxis)
        {
            maxLines = s.WebkitLineClamp;
            ellipsis = true;
        }
        var indent = s.TextIndent;
        var tab = s.TabSize;
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
            // 段落方向参与双向算法，并决定 start / end 对齐落在哪一侧
            Direction = s.Direction == CssDirection.Rtl ? ParagraphDirection.RightToLeft : ParagraphDirection.LeftToRight,
            LineHeight = lineHeight,
            WhiteSpace = ws,
            WordBreak = wordBreak,
            OverflowWrap = overflowWrap,
            Ellipsis = ellipsis ? ParagraphStyle.HorizontalEllipsis : null,
            MaxLines = maxLines,
            TextIndent = indent.Length.Calc is null ? Finite(indent.Length.Length) : 0,
            TextIndentPercent = indent.Length.Calc is null && indent.Length.HasPercentage ? Finite(indent.Length.Percentage) / 100f : 0,
            HangingIndent = indent.Hanging,
            IndentEachLine = indent.EachLine,
            TabSize = Finite(tab.Value),
            TabSizeIsLength = tab.IsLength,
            TextWrapStyle = s.TextWrapStyle switch
            {
                CssTextWrapStyle.Balance => TextWrapStyle.Balance,
                CssTextWrapStyle.Stable or CssTextWrapStyle.Pretty => TextWrapStyle.Stable,
                _ => TextWrapStyle.Auto,
            },
        };
    }
}
