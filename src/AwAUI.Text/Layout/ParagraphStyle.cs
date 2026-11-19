using System;
using AwAUI.Unicode;

namespace AwAUI.Text;

/// <summary>CSS <c>text-align</c>。<see cref="Start"/>/<see cref="End"/> 按段落方向映射到左右。</summary>
public enum TextAlign : byte
{
    Start,
    End,
    Left,
    Right,
    Center,
    /// <summary>两端对齐：扩展词间距（以及 CJK 字符间距）；段落最后一行与强制换行前的行按 Start 对齐。</summary>
    Justify,
}

/// <summary>CSS <c>white-space</c>（空白折叠与换行）。</summary>
public enum WhiteSpace : byte
{
    /// <summary>折叠空白与换行，自动换行。</summary>
    Normal,
    /// <summary>折叠空白与换行，不自动换行。</summary>
    NoWrap,
    /// <summary>保留空白与换行，不自动换行。</summary>
    Pre,
    /// <summary>保留空白与换行，自动换行；行尾空白悬挂。</summary>
    PreWrap,
    /// <summary>折叠空白，保留换行，自动换行。</summary>
    PreLine,
    /// <summary>与 <see cref="PreWrap"/> 相同，但空白不悬挂、占据宽度，且每个空白之后都可换行。</summary>
    BreakSpaces,
}

/// <summary>CSS <c>word-break</c>。</summary>
public enum WordBreak : byte
{
    Normal,
    /// <summary>字母与数字之间也可断开（视同表意文字）。</summary>
    BreakAll,
    /// <summary>字母、数字与表意文字之间都不断开（只在空格、标点等处断开）。</summary>
    KeepAll,
}

/// <summary>CSS <c>overflow-wrap</c>。</summary>
public enum OverflowWrap : byte
{
    Normal,
    /// <summary>行内没有其他断点时可在任意字素簇之间断开；不影响最小固有宽度。</summary>
    BreakWord,
    /// <summary>与 <see cref="BreakWord"/> 相同，但最小固有宽度按最宽的字素簇计算。</summary>
    Anywhere,
}

/// <summary>
/// 段落样式：对齐、方向、最大行数、省略号、行高策略、断行严格度、空白处理。与 CSS 文本属性一一对应（design/16）。
/// 不可变；用对象初始化器设置。
/// </summary>
public sealed class ParagraphStyle
{
    public static ParagraphStyle Default { get; } = new();

    /// <summary>段落的默认文本样式：用于 <see cref="ParagraphBuilder.PushStyle"/> 之外的文本，也是每行的支柱（strut，CSS 根行内盒）。</summary>
    public TextStyle TextStyle { get; init; } = TextStyle.Default;

    public TextAlign TextAlign { get; init; }

    /// <summary>段落基础方向（CSS <c>direction</c>；<see cref="BidiDirection.Auto"/> 对应 <c>unicode-bidi: plaintext</c>）。</summary>
    public BidiDirection Direction { get; init; }

    /// <summary>最大行数（CSS <c>line-clamp</c>）；0 表示不限制。</summary>
    public int MaxLines { get; init; }

    /// <summary>
    /// 省略号文本（CSS <c>text-overflow: ellipsis</c> 时为 "…"）；null 表示不省略。
    /// 超出 <see cref="MaxLines"/> 时加在最后一行末尾；某一行宽于可用宽度（如 nowrap）时截断该行并加上省略号。
    /// </summary>
    public string? Ellipsis { get; init; }

    /// <summary>行高（CSS <c>line-height</c>）。文本样式未指定行高时也使用它。</summary>
    public LineHeight LineHeight { get; init; }

    /// <summary>断行严格度（CSS <c>line-break</c>）。</summary>
    public LineBreakStrictness LineBreak { get; init; } = LineBreakStrictness.Normal;

    public WhiteSpace WhiteSpace { get; init; }
    public WordBreak WordBreak { get; init; }
    public OverflowWrap OverflowWrap { get; init; }

    /// <summary>CSS <c>text-overflow: ellipsis</c> 使用的省略号。</summary>
    public const string HorizontalEllipsis = "…";

    internal bool Wraps => WhiteSpace is not (WhiteSpace.NoWrap or WhiteSpace.Pre);
    internal bool CollapsesSpaces => WhiteSpace is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;
    internal bool PreservesNewlines => WhiteSpace is not (WhiteSpace.Normal or WhiteSpace.NoWrap);
}

/// <summary>行内占位盒（图片、行内组件）的竖直对齐方式。</summary>
public enum PlaceholderAlignment : byte
{
    /// <summary>盒的基线（距顶部 <see cref="PlaceholderStyle.Baseline"/>）与行的基线对齐。</summary>
    Baseline,
    /// <summary>与行盒顶部对齐。</summary>
    Top,
    /// <summary>与行盒底部对齐。</summary>
    Bottom,
    /// <summary>盒的中心与基线上方半个 x 高度处对齐（CSS <c>vertical-align: middle</c>）。</summary>
    Middle,
}

/// <summary>行内占位盒：在文本流中占据一个 U+FFFC 字符的位置与给定的尺寸，由调用方在 <see cref="TextLayout.GetPlaceholderRect"/> 处绘制内容。</summary>
public readonly struct PlaceholderStyle
{
    /// <param name="width">宽度（逻辑像素）。</param>
    /// <param name="height">高度（逻辑像素）。</param>
    /// <param name="alignment">竖直对齐。</param>
    /// <param name="baseline">盒顶部到盒基线的距离；NaN 表示盒底部即基线（替换元素的默认行为）。</param>
    public PlaceholderStyle(float width, float height, PlaceholderAlignment alignment = PlaceholderAlignment.Baseline, float baseline = float.NaN)
    {
        Width = TextStyle.Sanitize(width, 0, TextStyle.MaxFontSize * 10);
        Height = TextStyle.Sanitize(height, 0, TextStyle.MaxFontSize * 10);
        Alignment = alignment;
        Baseline = float.IsNaN(baseline) ? Height : TextStyle.Sanitize(baseline, -Height * 2 - 1, Height * 2 + 1);
    }

    public float Width { get; }
    public float Height { get; }
    public PlaceholderAlignment Alignment { get; }
    public float Baseline { get; }
}

/// <summary>插入点的亲和性：同一偏移在软换行处既是上一行的末尾也是下一行的开头。</summary>
public enum TextAffinity : byte
{
    /// <summary>与后面的字符关联（下一行的行首）。</summary>
    Downstream,
    /// <summary>与前面的字符关联（上一行的行尾）。</summary>
    Upstream,
}

/// <summary>文本中的插入点：UTF-16 偏移（落在字素簇边界上）与亲和性。</summary>
public readonly struct TextPosition : IEquatable<TextPosition>
{
    public TextPosition(int offset, TextAffinity affinity = TextAffinity.Downstream)
    {
        Offset = offset;
        Affinity = affinity;
    }

    public int Offset { get; }
    public TextAffinity Affinity { get; }

    public bool Equals(TextPosition other) => Offset == other.Offset && Affinity == other.Affinity;
    public override bool Equals(object? obj) => obj is TextPosition p && Equals(p);
    public override int GetHashCode() => HashCodeCompat.Combine(Offset, (int)Affinity);
    public static bool operator ==(TextPosition a, TextPosition b) => a.Equals(b);
    public static bool operator !=(TextPosition a, TextPosition b) => !a.Equals(b);
    public override string ToString() => Affinity == TextAffinity.Upstream ? $"{Offset}↑" : Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>插入点移动方式（<see cref="TextLayout.Move"/>）。</summary>
public enum CaretMove : byte
{
    /// <summary>向左一个字素簇（视觉顺序，双向文本中按屏幕方向）。</summary>
    Left,
    /// <summary>向右一个字素簇（视觉顺序）。</summary>
    Right,
    /// <summary>逻辑顺序上的前一个字素簇边界。</summary>
    Backward,
    /// <summary>逻辑顺序上的后一个字素簇边界。</summary>
    Forward,
    /// <summary>上一行，保持水平位置。</summary>
    Up,
    /// <summary>下一行，保持水平位置。</summary>
    Down,
    /// <summary>前一个词的开头。</summary>
    WordStart,
    /// <summary>后一个词的结尾。</summary>
    WordEnd,
    LineStart,
    LineEnd,
    TextStart,
    TextEnd,
}

/// <summary>一行的度量（逻辑像素，原点在布局盒左上角）。</summary>
public readonly struct LineMetrics
{
    internal LineMetrics(int index, int start, int end, int endExcludingBreak, bool hardBreak,
        float left, float width, float top, float height, float baseline, float ascent, float descent)
    {
        Index = index;
        StartOffset = start;
        EndOffset = end;
        EndExcludingBreak = endExcludingBreak;
        HardBreak = hardBreak;
        Left = left;
        Width = width;
        Top = top;
        Height = height;
        Baseline = baseline;
        Ascent = ascent;
        Descent = descent;
    }

    public int Index { get; }
    /// <summary>行的文本范围 [StartOffset, EndOffset)，含行尾空白与换行符。</summary>
    public int StartOffset { get; }
    public int EndOffset { get; }
    /// <summary>不含行尾强制换行符的结束偏移。</summary>
    public int EndExcludingBreak { get; }
    /// <summary>是否以强制换行符结束。</summary>
    public bool HardBreak { get; }
    /// <summary>内容（不含悬挂的行尾空白）左边缘。</summary>
    public float Left { get; }
    /// <summary>内容宽度（不含悬挂的行尾空白；含省略号；两端对齐后等于可用宽度）。</summary>
    public float Width { get; }
    public float Top { get; }
    public float Height { get; }
    /// <summary>基线的 y 坐标。</summary>
    public float Baseline { get; }
    /// <summary>基线以上的高度（含半行距）。</summary>
    public float Ascent { get; }
    /// <summary>基线以下的高度（含半行距）。</summary>
    public float Descent { get; }

    public TextRange Range => new(StartOffset, EndOffset);

    public override string ToString() =>
        FormattableString.Invariant($"line {Index} [{StartOffset}, {EndOffset}) x={Left} w={Width} y={Top} h={Height} baseline={Baseline}");
}
