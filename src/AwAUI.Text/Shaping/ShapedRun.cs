using System;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

/// <summary>每个字形的整形标志。</summary>
[Flags]
public enum GlyphFlags : byte
{
    None = 0,
    /// <summary>在此字形之前断开文本需要重新整形（例如它与前一个字形之间应用了字距调整）。</summary>
    UnsafeToBreak = 1,
    /// <summary>不可见字形（默认可忽略字符，如 ZWJ、软连字符、变体选择符）：前进量为 0，绘制时跳过。</summary>
    Hidden = 2,
}

/// <summary>
/// 一段整形结果。不可变，可跨线程读取。
/// 字形按视觉顺序（从左到右）排列；坐标为逻辑像素，y 向下。
/// <see cref="Clusters"/> 是每个字形对应的 UTF-16 偏移（相对于整形请求中的完整文本，而不是 Range 的起点）。
/// </summary>
public sealed class ShapedRun
{
    readonly ushort[] _glyphs;
    readonly int[] _clusters;
    readonly Vector2[] _advances;
    readonly Vector2[] _offsets;
    readonly GlyphFlags[] _flags;

    /// <summary>创建整形结果。各数组会被复制，之后修改传入的数组不影响本对象。</summary>
    public ShapedRun(FontFace face, float size, TextRange range, bool rightToLeft,
        ReadOnlySpan<ushort> glyphs, ReadOnlySpan<int> clusters, ReadOnlySpan<Vector2> advances,
        ReadOnlySpan<Vector2> offsets, ReadOnlySpan<GlyphFlags> flags)
        : this(face, size, range, rightToLeft, Validate(glyphs, clusters, advances, offsets, flags),
            clusters.ToArray(), advances.ToArray(), offsets.ToArray(), flags.ToArray(), owned: true)
    {
    }

    static ushort[] Validate(ReadOnlySpan<ushort> glyphs, ReadOnlySpan<int> clusters, ReadOnlySpan<Vector2> advances,
        ReadOnlySpan<Vector2> offsets, ReadOnlySpan<GlyphFlags> flags)
    {
        int n = glyphs.Length;
        if (clusters.Length != n) throw new ArgumentException("Clusters must match glyphs.", nameof(clusters));
        if (advances.Length != n) throw new ArgumentException("Advances must match glyphs.", nameof(advances));
        if (offsets.Length != n) throw new ArgumentException("Offsets must match glyphs.", nameof(offsets));
        if (flags.Length != n) throw new ArgumentException("Flags must match glyphs.", nameof(flags));
        return glyphs.ToArray();
    }

    /// <summary>接管数组所有权、不复制的构造（供本程序集内的整形器使用，调用方之后不得再修改这些数组）。</summary>
    internal ShapedRun(FontFace face, float size, TextRange range, bool rightToLeft,
        ushort[] glyphs, int[] clusters, Vector2[] advances, Vector2[] offsets, GlyphFlags[] flags, bool owned)
    {
        _ = owned;
        Face = face ?? throw new ArgumentNullException(nameof(face));
        int n = glyphs.Length;
        if (clusters.Length != n || advances.Length != n || offsets.Length != n || flags.Length != n)
            throw new ArgumentException("Glyph arrays must have equal length.");
        Size = size;
        Range = range;
        RightToLeft = rightToLeft;
        _glyphs = glyphs;
        _clusters = clusters;
        _advances = advances;
        _offsets = offsets;
        _flags = flags;
        var total = Vector2.Zero;
        foreach (var a in advances) total += a;
        Advance = total;
    }

    public FontFace Face { get; }
    public float Size { get; }
    public TextRange Range { get; }
    public bool RightToLeft { get; }

    public int Count => _glyphs.Length;
    public ReadOnlySpan<ushort> Glyphs => _glyphs;
    public ReadOnlySpan<int> Clusters => _clusters;
    public ReadOnlySpan<Vector2> Advances => _advances;
    public ReadOnlySpan<Vector2> Offsets => _offsets;
    public ReadOnlySpan<GlyphFlags> Flags => _flags;

    /// <summary>全部字形前进量之和（水平文本的宽度在 X 中）。</summary>
    public Vector2 Advance { get; }

    public float Width => Advance.X;

    /// <summary>
    /// 转换为可由 <see cref="DrawingContext.DrawGlyphRun(GlyphRun, Point, Paint)"/> 绘制的字形串。
    /// 字形位置相对第一个字形在基线上的原点（按前进量累加再加上偏移）；带 <see cref="GlyphFlags.Hidden"/> 的字形被略去。
    /// <see cref="Size"/> 不是非负有限值时（整形请求不校验字号）返回字号为 0 的空字形串，不会被绘制。
    /// </summary>
    /// <param name="flags">合成加粗 / 倾斜（字体缺少对应字重或风格时由调用方决定）。</param>
    /// <param name="variation">可变字体的轴取值（应与整形时相同）。</param>
    public GlyphRun ToGlyphRun(GlyphRunFlags flags = GlyphRunFlags.None, FontVariation variation = default)
    {
        int visible = 0;
        foreach (var f in _flags)
            if ((f & GlyphFlags.Hidden) == 0) visible++;
        var glyphs = new ushort[visible];
        var positions = new Point[visible];
        float x = 0, y = 0;
        int n = 0;
        for (int i = 0; i < _glyphs.Length; i++)
        {
            if ((_flags[i] & GlyphFlags.Hidden) == 0)
            {
                glyphs[n] = _glyphs[i];
                positions[n] = new Point(x + _offsets[i].X, y + _offsets[i].Y);
                n++;
            }
            x += _advances[i].X;
            y += _advances[i].Y;
        }
        // GlyphRun 拒绝非有限或负的字号；这里不抛异常，而是给出不可见的空字形串（与 DrawingContext 跳过退化输入的做法一致）
        float size = Size >= 0 && !float.IsInfinity(Size) ? Size : 0;
        return new GlyphRun(Face, size, glyphs, positions, flags, variation);
    }

    /// <summary>
    /// 把整段字形的轮廓追加到 <paramref name="sink"/>（逻辑像素坐标）。<paramref name="origin"/> 是第一个字形在基线上的原点。
    /// 带 <see cref="GlyphFlags.Hidden"/> 的字形不绘制。
    /// 返回无法取得轮廓的字形数量（例如字形数据损坏，或位图 / 彩色字体没有轮廓）。
    /// </summary>
    public int AppendOutlines(PathBuilder sink, Point origin)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        float scale = Face.Metrics.GetScale(Size);
        var scaleMatrix = Matrix3x2.CreateScale(scale);
        float x = origin.X, y = origin.Y;
        int failed = 0;
        for (int i = 0; i < _glyphs.Length; i++)
        {
            if ((_flags[i] & GlyphFlags.Hidden) == 0)
            {
                var m = scaleMatrix * Matrix3x2.CreateTranslation(x + _offsets[i].X, y + _offsets[i].Y);
                if (!Face.GetOutline(_glyphs[i], default, sink, m)) failed++;
            }
            x += _advances[i].X;
            y += _advances[i].Y;
        }
        return failed;
    }
}
