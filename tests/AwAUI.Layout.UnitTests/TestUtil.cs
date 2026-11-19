using System.Globalization;

namespace AwAUI.Layout.UnitTests;

/// <summary>
/// 假文本：单词宽度 = 字符数 × 10，空格 10，行高 20，基线 16。按可用宽度贪心断行。
/// min-content = 最长单词；max-content = 单行全部内容。统计调用次数以验证缓存。
/// </summary>
sealed class FakeText : LayoutMeasure
{
    public const float CharWidth = 10, Space = 10, LineHeight = 20, Ascent = 16;

    readonly float[] _words;
    public int Calls;

    public FakeText(string text)
    {
        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        _words = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) _words[i] = parts[i].Length * CharWidth;
    }

    public override Size Measure(LayoutNode node, in MeasureConstraints c)
    {
        Calls++;
        if (_words.Length == 0) return new Size(0, 0);
        float limit = c.Width.Kind switch
        {
            AvailableSpaceKind.MinContent => 0,
            AvailableSpaceKind.MaxContent => float.PositiveInfinity,
            _ => c.Width.Value,
        };
        float line = 0, max = 0;
        int lines = 1;
        bool lineEmpty = true;
        foreach (var w in _words)
        {
            if (lineEmpty) { line = w; lineEmpty = false; }
            else if (line + Space + w <= limit + 0.001f) line += Space + w;
            else
            {
                max = Math.Max(max, line);
                lines++;
                line = w;
            }
        }
        max = Math.Max(max, line);
        return new Size(max, lines * LineHeight);
    }

    public override float GetBaseline(LayoutNode node, Size contentSize) => Ascent;
}

/// <summary>固定尺寸的叶子（类似图片的自然尺寸）。</summary>
sealed class FixedMeasure : LayoutMeasure
{
    readonly Size _size;
    public int Calls;
    public FixedMeasure(float w, float h) => _size = new Size(w, h);
    public override Size Measure(LayoutNode node, in MeasureConstraints constraints)
    {
        Calls++;
        return _size;
    }
}

static class T
{
    public static LayoutStyle Block => LayoutStyle.Default;
    public static LayoutStyle Row => LayoutStyle.Default with { Display = Display.Flex };
    public static LayoutStyle Column => LayoutStyle.Default with { Display = Display.Flex, FlexDirection = FlexDirection.Column };

    public static LayoutNode N(LayoutStyle style, params LayoutNode[] children)
    {
        var n = new LayoutNode(style);
        foreach (var c in children) n.AddChild(c);
        return n;
    }

    public static LayoutNode Text(string text, LayoutStyle? style = null) =>
        new(style ?? LayoutStyle.Default) { Measure = new FakeText(text) };

    public static LayoutNode Text(string text, out FakeText measure, LayoutStyle? style = null)
    {
        measure = new FakeText(text);
        return new LayoutNode(style ?? LayoutStyle.Default) { Measure = measure };
    }

    /// <summary>以给定视口布局。</summary>
    public static LayoutEngine Run(LayoutNode root, float width = 1000, float height = 1000)
    {
        var e = new LayoutEngine();
        e.Compute(root, new Size(width, height));
        return e;
    }

    public static void Box(LayoutNode n, float x, float y, float w, float h)
    {
        string actual = string.Format(CultureInfo.InvariantCulture, "[{0}, {1}, {2}x{3}]", n.X, n.Y, n.Width, n.Height);
        string expected = string.Format(CultureInfo.InvariantCulture, "[{0}, {1}, {2}x{3}]", x, y, w, h);
        Assert.True(Near(n.X, x) && Near(n.Y, y) && Near(n.Width, w) && Near(n.Height, h),
            $"expected {expected}, actual {actual}");
    }

    public static void Size(LayoutNode n, float w, float h)
    {
        Assert.True(Near(n.Width, w) && Near(n.Height, h),
            string.Format(CultureInfo.InvariantCulture, "expected {0}x{1}, actual {2}x{3}", w, h, n.Width, n.Height));
    }

    public static void Eq(float expected, float actual) =>
        Assert.True(Near(expected, actual), string.Format(CultureInfo.InvariantCulture, "expected {0}, actual {1}", expected, actual));

    public static bool Near(float a, float b) => Math.Abs(a - b) < 0.02f;

    public static Edges<LengthPercentageAuto> Margin(LengthPercentageAuto all) => new(all);
    public static Edges<LengthPercentageAuto> Margin(LengthPercentageAuto top, LengthPercentageAuto right, LengthPercentageAuto bottom, LengthPercentageAuto left) =>
        new(top, right, bottom, left);
    public static readonly LengthPercentageAuto Auto = LengthPercentageAuto.Auto;

    public static Edges<LengthPercentageAuto> Inset(float? top = null, float? right = null, float? bottom = null, float? left = null) =>
        new(V(top), V(right), V(bottom), V(left));

    static LengthPercentageAuto V(float? v) => v is float f ? LengthPercentageAuto.Px(f) : LengthPercentageAuto.Auto;
}
