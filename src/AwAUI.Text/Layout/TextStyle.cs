using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

/// <summary>CSS <c>text-decoration-line</c>。</summary>
[Flags]
public enum TextDecorationLine : byte
{
    None = 0,
    Underline = 1,
    Overline = 2,
    LineThrough = 4,
}

/// <summary>CSS <c>text-decoration-style</c>。</summary>
public enum TextDecorationStyle : byte
{
    Solid,
    Double,
    Dotted,
    Dashed,
    Wavy,
}

/// <summary>CSS <c>text-underline-position</c>。</summary>
public enum UnderlinePosition : byte
{
    /// <summary>取字体的下划线位置（post 表），没有时按字号估计。</summary>
    Auto,
    /// <summary>放在字形下伸部之下（不穿过下伸部，CSS <c>under</c>）。</summary>
    Under,
}

/// <summary>一层文字阴影（CSS <c>text-shadow</c> 的一项）：偏移、模糊半径与颜色。</summary>
public readonly struct TextShadow : IEquatable<TextShadow>
{
    public TextShadow(float offsetX, float offsetY, float blur, Color color)
    {
        OffsetX = TextStyle.Sanitize(offsetX, -TextStyle.MaxFontSize, TextStyle.MaxFontSize);
        OffsetY = TextStyle.Sanitize(offsetY, -TextStyle.MaxFontSize, TextStyle.MaxFontSize);
        Blur = TextStyle.Sanitize(blur, 0, TextStyle.MaxFontSize);
        Color = color;
    }

    public float OffsetX { get; }
    public float OffsetY { get; }

    /// <summary>模糊半径（CSS 语义，σ = Blur / 2）。绘制按 <see cref="TextLayout.Paint"/> 的说明近似。</summary>
    public float Blur { get; }

    public Color Color { get; }

    public bool Equals(TextShadow o) => OffsetX.Equals(o.OffsetX) && OffsetY.Equals(o.OffsetY) && Blur.Equals(o.Blur) && Color.Equals(o.Color);
    public override bool Equals(object? obj) => obj is TextShadow s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(OffsetX.GetHashCode(), OffsetY.GetHashCode(), Blur.GetHashCode(), Color.GetHashCode());
}

/// <summary>CSS <c>text-transform</c>（第 1 级：大小写变换）。</summary>
public enum TextTransform : byte
{
    None,
    /// <summary>全部大写（Unicode 简单大小写映射，不做 ß → SS 之类改变长度的特殊映射）。</summary>
    Uppercase,
    /// <summary>全部小写（简单映射）。</summary>
    Lowercase,
    /// <summary>每个词（UAX #29 词边界）的第一个字母转为词首大写形式（Titlecase）。</summary>
    Capitalize,
}

/// <summary>
/// 行高（CSS <c>line-height</c>）：<see cref="Normal"/> 取字体度量（上升 + 下降 + 行间距），
/// 数值倍数相对字号，像素为绝对值。非有限或负的取值按 <see cref="Normal"/> 处理；求出的行高不超过 1,000,000 逻辑像素。
/// </summary>
public readonly struct LineHeight : IEquatable<LineHeight>
{
    const byte KindNormal = 0, KindMultiple = 1, KindPixels = 2;
    readonly byte _kind;

    LineHeight(byte kind, float value)
    {
        if (!(value >= 0) || float.IsInfinity(value))
        {
            kind = KindNormal;
            value = 0;
        }
        _kind = kind;
        Value = value;
    }

    /// <summary><c>line-height: normal</c>。</summary>
    public static LineHeight Normal => default;

    /// <summary>无单位数值：字号的倍数。</summary>
    public static LineHeight Multiple(float factor) => new(KindMultiple, factor);

    /// <summary>绝对长度（逻辑像素）。</summary>
    public static LineHeight Pixels(float pixels) => new(KindPixels, pixels);

    public bool IsNormal => _kind == KindNormal;
    public bool IsMultiple => _kind == KindMultiple;
    public bool IsPixels => _kind == KindPixels;

    /// <summary>倍数或像素值（<see cref="Normal"/> 时为 0）。</summary>
    public float Value { get; }

    /// <summary>求出的行高上限（逻辑像素）：更大的取值只会让行的坐标溢出为无穷大。</summary>
    internal const float MaxResolved = TextStyle.MaxFontSize * 10;

    /// <summary>求出行高：<paramref name="normal"/> 是字体度量给出的默认行高。结果限制在 [0, <see cref="MaxResolved"/>]。</summary>
    internal float Resolve(float fontSize, float normal)
    {
        float v = _kind switch
        {
            KindMultiple => Value * fontSize,
            KindPixels => Value,
            _ => normal,
        };
        return v > MaxResolved ? MaxResolved : v;
    }

    public bool Equals(LineHeight other) => _kind == other._kind && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is LineHeight l && Equals(l);
    public override int GetHashCode() => HashCodeCompat.Combine(_kind, Value.GetHashCode());
    public static bool operator ==(LineHeight a, LineHeight b) => a.Equals(b);
    public static bool operator !=(LineHeight a, LineHeight b) => !a.Equals(b);

    public override string ToString() => _kind switch
    {
        KindMultiple => Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        KindPixels => Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px",
        _ => "normal",
    };
}

/// <summary>
/// 一段文本的样式（字体、字号、颜色、装饰、字距、特性 ...），对应 CSS 的可继承文本属性的计算值。
/// 不可变；用对象初始化器设置，未设置的属性取 CSS 初始值。由 <see cref="ParagraphBuilder.PushStyle"/> 使用。
/// </summary>
public sealed class TextStyle
{
    /// <summary>全部取默认值的样式（14px，黑色，字体族取 <see cref="TextEngine.DefaultFontFamilies"/>）。</summary>
    public static TextStyle Default { get; } = new();

    /// <summary>按优先级排列的字体族名；为空时使用 <see cref="TextEngine.DefaultFontFamilies"/>。</summary>
    public IReadOnlyList<string> FontFamilies { get; init; } = Array.Empty<string>();

    /// <summary>字号（逻辑像素）。非有限或负值按 0 处理，过大的值被限制在 <see cref="MaxFontSize"/>。</summary>
    public float FontSize { get; init; } = 14;

    public FontWeight FontWeight { get; init; }
    public FontStyle FontStyle { get; init; }
    public FontStretch FontStretch { get; init; } = FontStretch.Normal;

    /// <summary>文字颜色（CSS <c>color</c>）。</summary>
    public Color Color { get; init; } = Color.Black;

    /// <summary>可选的画刷（如渐变）；设置后代替 <see cref="Color"/> 填充字形。</summary>
    public Brush? Foreground { get; init; }

    public TextDecorationLine Decoration { get; init; }

    /// <summary>装饰线颜色；为 null 时与文字相同（currentColor）。</summary>
    public Color? DecorationColor { get; init; }

    public TextDecorationStyle DecorationStyle { get; init; }

    /// <summary>装饰线粗细（逻辑像素）；NaN 表示 auto（取字体的下划线 / 删除线粗细）。</summary>
    public float DecorationThickness { get; init; } = float.NaN;

    /// <summary>下划线相对其原位置的偏移（CSS <c>text-underline-offset</c>，逻辑像素，向下为正）；NaN 表示 auto（取字体度量）。
    /// 给出时 <see cref="UnderlinePosition.Auto"/> 的下划线顶边在基线之下该距离处，<see cref="UnderlinePosition.Under"/> 的在下伸部之下该距离处。</summary>
    public float UnderlineOffset { get; init; } = float.NaN;

    /// <summary>CSS <c>text-underline-position</c>。</summary>
    public UnderlinePosition UnderlinePosition { get; init; }

    /// <summary>
    /// 文字阴影（CSS <c>text-shadow</c>），按 CSS 的顺序：第一项在最上面。阴影画在文字与装饰线之下，偏移文字与装饰线的形状。
    /// 最多使用前 <see cref="MaxShadows"/> 项。
    /// </summary>
    public IReadOnlyList<TextShadow>? Shadows { get; init; }

    /// <summary>每个样式最多绘制的阴影层数（不可信的样式表不能让每个字形串产生无界的绘制命令）。</summary>
    public const int MaxShadows = 16;

    /// <summary>
    /// 基线偏移（逻辑像素，向上为正）：CSS <c>vertical-align: sub / super / &lt;length&gt;</c> 作用于行内文字时，本样式的文字整体抬高或降低，
    /// 行盒随之增高以容纳它。
    /// </summary>
    public float BaselineShift { get; init; }

    /// <summary>字母间距（CSS <c>letter-spacing</c>，逻辑像素）：加在每个字素簇之后。</summary>
    public float LetterSpacing { get; init; }

    /// <summary>词间距（CSS <c>word-spacing</c>，逻辑像素）：加在每个词分隔符（空格等）之后。</summary>
    public float WordSpacing { get; init; }

    /// <summary>行高；为 null 时使用 <see cref="ParagraphStyle.LineHeight"/>。</summary>
    public LineHeight? LineHeight { get; init; }

    public TextTransform TextTransform { get; init; }

    /// <summary>语言标签（BCP 47，如 "ja"、"zh-Hans"），用于字体回退与整形。</summary>
    public string? Locale { get; init; }

    /// <summary>OpenType 特性（CSS <c>font-feature-settings</c>）。作用于整段样式，<see cref="FontFeature.Start"/>/<see cref="FontFeature.End"/> 被忽略。</summary>
    public IReadOnlyList<FontFeature>? Features { get; init; }

    /// <summary>可变字体的轴取值（CSS <c>font-variation-settings</c>）。</summary>
    public FontVariation Variation { get; init; }

    /// <summary>字号上限：更大的值会让坐标失去精度且毫无用处，按此截断。</summary>
    public const float MaxFontSize = 100_000;

    internal float EffectiveFontSize => Sanitize(FontSize, 0, MaxFontSize);

    internal static float Sanitize(float value, float min, float max)
    {
        if (float.IsNaN(value)) return 0 < min ? min : (0 > max ? max : 0);
        return value < min ? min : value > max ? max : value;
    }
}
