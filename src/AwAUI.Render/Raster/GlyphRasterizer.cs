using System;
using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Render.Raster;

/// <summary>
/// 一条字形串在当前变换下的缓存路径参数（design/03「渲染相关」）。软件后端与 GPU 后端共用：两者按同一规则决定哪些字形走缓存、
/// 怎样量化位置，所以同一字形得到逐字节相同的 A8 掩码。
/// </summary>
internal readonly struct GlyphRunPlan
{
    public GlyphRunPlan(GlyphRun run, float scale, float translateX, float translateY, int sizeBucket, GlyphStyle style, int boldBucket,
        float cullLeft, float cullTop, float cullRight, float cullBottom)
    {
        Run = run;
        Scale = scale;
        TranslateX = translateX;
        TranslateY = translateY;
        SizeBucket = sizeBucket;
        Style = style;
        BoldBucket = boldBucket;
        CullLeft = cullLeft;
        CullTop = cullTop;
        CullRight = cullRight;
        CullBottom = cullBottom;
    }

    public GlyphRun Run { get; }
    public float Scale { get; }
    public float TranslateX { get; }
    public float TranslateY { get; }
    public int SizeBucket { get; }
    public GlyphStyle Style { get; }
    public int BoldBucket { get; }

    /// <summary>未缓存字形的剔除范围（相对整像素原点，设备像素）。</summary>
    public float CullLeft { get; }
    public float CullTop { get; }
    public float CullRight { get; }
    public float CullBottom { get; }
}

/// <summary>
/// 字形掩码的光栅化与字形串的回退路径（软件后端与 GPU 后端共用的唯一实现）。持有可复用的临时缓冲；不是线程安全的。
/// </summary>
internal sealed class GlyphRasterizer
{
    /// <summary>入缓存的最大设备字号（px）；更大的字形直接填充路径（design/03：大字号不入图集）。</summary>
    public const float MaxCachedGlyphSize = 128f;

    /// <summary>单个字形掩码的最大像素数；超出时该字形改走路径填充。</summary>
    public const int MaxGlyphMaskPixels = 256 * 256;

    /// <summary>
    /// 合成加粗的四个平移副本（以非零规则求并）：(0, 0)、(w, 0)、(w/2, ±w/2)，即横、竖方向各加宽 w。
    /// 是轮廓外扩的近似：45° 方向的笔画只加宽约 0.7w；比 w/2 更细的笔画在副本之间可能出现缝隙。
    /// </summary>
    static readonly Vector2[] BoldOffsets = { new(1, 0), new(0.5f, -0.5f), new(0.5f, 0.5f) };

    readonly Rasterizer _rasterizer;
    readonly PolyList _poly = new();
    readonly PolyList _scratch = new();
    float[] _coverage = new float[256];

    public GlyphRasterizer(Rasterizer rasterizer) => _rasterizer = rasterizer;

    /// <summary>
    /// 变换为「均匀正缩放 + 平移」且设备字号不超过 <see cref="MaxCachedGlyphSize"/> 时给出缓存路径的参数，否则返回 false
    /// （调用方整串走 <see cref="AppendOutlines"/> 的路径填充）。设备字号小于 1/128 px 时 <paramref name="visible"/> 为 false。
    /// </summary>
    public static bool TryPlan(GlyphRun run, in Matrix3x2 ctm, bool antiAlias, out GlyphRunPlan plan, out bool visible)
    {
        plan = default;
        visible = true;
        var face = run.Face;
        float s = ctm.M11;
        float deviceSize = run.Size * s;
        if (!(ctm.M12 == 0 && ctm.M21 == 0 && ctm.M22 == s && s > 0 && deviceSize <= MaxCachedGlyphSize)) return false;
        int sizeBucket = (int)(deviceSize * GlyphKey.SizeScale + 0.5f);
        if (sizeBucket <= 0)
        {
            visible = false;   // 小于 1/128 px，不可见
            return true;
        }
        var style = GlyphStyle.None;
        int boldBucket = 0;
        if (run.IsSyntheticBold)
        {
            // 加宽量按逻辑字号计算再乘以缩放（与回退路径及 GlyphRun.Bounds 一致），不随设备像素比改变字重
            style |= GlyphStyle.SyntheticBold;
            boldBucket = (int)(GlyphRun.GetSyntheticBoldExtent(run.Size) * s * GlyphKey.BoldScale + 0.5f);
        }
        if (run.IsSyntheticOblique) style |= GlyphStyle.SyntheticOblique;
        if (!antiAlias) style |= GlyphStyle.Aliased;
        // 未缓存的字形若明显落在裁剪区之外就不光栅化：按 'head' 边界框外扩 1 em 估计墨迹范围；
        // 掩码可能延伸到整像素原点之外 1 px（子像素偏移与外扩取整），再留 1 px 余量
        GetCullExtents(face, run.IsSyntheticOblique, deviceSize, 2, out float l, out float t, out float r, out float b);
        plan = new GlyphRunPlan(run, s, ctm.M31, ctm.M32, sizeBucket, style, boldBucket, l, t, r, b);
        return true;
    }

    /// <summary>
    /// 第 <paramref name="index"/> 个字形的缓存键与整像素原点（x 量化到 1/4 px、≥ 0.875 进位；y 对齐到整像素）。
    /// 位置不是有限值或超出 ±10⁷ 设备像素时返回 false（跳过该字形）。
    /// </summary>
    public static bool TryPlace(in GlyphRunPlan plan, Point origin, int index, out GlyphKey key, out int ix, out int iy)
    {
        var run = plan.Run;
        var pos = run.Positions[index];
        float dx = (origin.X + pos.X) * plan.Scale + plan.TranslateX;
        float dy = (origin.Y + pos.Y) * plan.Scale + plan.TranslateY;
        if (!(Math.Abs(dx) < 1e7f) || !(Math.Abs(dy) < 1e7f))   // 同时排除 NaN
        {
            key = default;
            ix = iy = 0;
            return false;
        }
        float fx = MathF.Floor(dx);
        ix = (int)fx;
        int q = (int)((dx - fx) * GlyphKey.SubpixelSteps + 0.5f);
        if (q >= GlyphKey.SubpixelSteps) { ix++; q = 0; }
        iy = (int)MathF.Floor(dy + 0.5f);
        key = new GlyphKey(run.Face, run.Variation, run.Glyphs[index], plan.SizeBucket, (byte)q, plan.Style, plan.BoldBucket);
        return true;
    }

    /// <summary>未缓存的字形是否明显在 <paramref name="bounds"/> 之外（不必光栅化）。</summary>
    public static bool IsCulled(in GlyphRunPlan plan, int ix, int iy, PixelRect bounds) =>
        ix + plan.CullRight < bounds.X || ix + plan.CullLeft > bounds.Right || iy + plan.CullBottom < bounds.Y || iy + plan.CullTop > bounds.Bottom;

    /// <summary>
    /// 剔除用的保守墨迹范围（相对字形原点，单位与 <paramref name="size"/> 相同）：'head' 边界框外扩 1 em 再加 <paramref name="margin"/>；
    /// 没有边界框时按 [−2, 3] em × [−2.5, 1.5] em。只用来跳过明显不可见的字形，边界框严重失实的字体可能被误剔除。
    /// </summary>
    static void GetCullExtents(FontFace face, bool oblique, float size, float margin, out float left, out float top, out float right, out float bottom)
    {
        var m = face.Metrics;
        float scale = size / m.UnitsPerEm;
        if (m.XMax > m.XMin && m.YMax > m.YMin)
        {
            left = m.XMin * scale - size;
            right = m.XMax * scale + size;
            top = -m.YMax * scale - size;
            bottom = -m.YMin * scale + size;
        }
        else
        {
            left = -2 * size; right = 3 * size;
            top = -2.5f * size; bottom = 1.5f * size;
        }
        if (oblique)
        {
            float k = GlyphRun.SyntheticObliqueSkew;
            left -= Math.Abs(bottom) * k;
            right += Math.Abs(top) * k;
        }
        left -= margin; top -= margin; right += margin; bottom += margin;
    }

    /// <summary>字形轮廓到「原点在字形原点、单位为设备像素（或局部坐标）」的变换：缩放 + 合成倾斜。</summary>
    static Matrix3x2 GlyphMatrix(float scale, bool oblique)
    {
        var m = Matrix3x2.CreateScale(scale, scale);
        if (oblique) m *= new Matrix3x2(1, 0, -GlyphRun.SyntheticObliqueSkew, 1, 0, 0);
        return m;
    }

    /// <summary>按键光栅化一个字形的 A8 掩码（空字形、过大的掩码分别给出空掩码与 <see cref="GlyphMask.TooLarge"/>）。</summary>
    public GlyphMask Rasterize(in GlyphKey key)
    {
        var face = key.Face;
        float deviceSize = key.DeviceSize;
        var m = GlyphMatrix(deviceSize / face.Metrics.UnitsPerEm, (key.Style & GlyphStyle.SyntheticOblique) != 0)
                * Matrix3x2.CreateTranslation(key.SubpixelX / (float)GlyphKey.SubpixelSteps, 0);
        var pb = new PathBuilder();
        if (!face.GetOutline(key.Glyph, key.Variation, pb, m)) return new GlyphMask(key, null, 0, 0, 0, 0);
        var path = pb.ToPath();
        if (path.IsEmpty) return new GlyphMask(key, null, 0, 0, 0, 0);

        _poly.Clear();
        Flattener.FlattenToPolygons(path, Matrix3x2.Identity, Flattener.DefaultTolerance, _poly);
        if (key.BoldBucket > 0)
        {
            float w = key.BoldExtent;
            _scratch.Clear();
            _scratch.AppendFrom(_poly);
            foreach (var o in BoldOffsets) _poly.AppendFrom(_scratch, o * w);
        }
        if (!_poly.TryGetBounds(out float l, out float t, out float r, out float b))
            return new GlyphMask(key, null, 0, 0, 0, 0);
        // 损坏的字体可能给出巨大或非有限的坐标：先在浮点上判断尺寸，避免取整溢出与大量分配
        float fw = r - l + 2, fh = b - t + 2;
        if (!(fw * fh <= MaxGlyphMaskPixels) || !(Math.Abs(l) < 1e5f) || !(Math.Abs(t) < 1e5f))
            return new GlyphMask(key, null, 0, 0, 0, 0, tooLarge: true);

        var rect = PixelRect.RoundOut(Rect.FromLTRB(l, t, r, b));
        if (rect.IsEmpty) return new GlyphMask(key, null, 0, 0, 0, 0);
        int n = rect.Width * rect.Height;
        if (_coverage.Length < n) _coverage = new float[Math.Max(n, _coverage.Length * 2)];
        Array.Clear(_coverage, 0, n);
        _rasterizer.Fill(_poly, FillRule.NonZero, (key.Style & GlyphStyle.Aliased) == 0, rect, new MaskSink(_coverage, rect, null));

        var bytes = new byte[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            byte v = ScalarKernels.ToByte(_coverage[i]);
            bytes[i] = v;
            any |= v != 0;
        }
        return any ? new GlyphMask(key, bytes, rect.X, rect.Y, rect.Width, rect.Height) : new GlyphMask(key, null, 0, 0, 0, 0);
    }

    /// <summary>
    /// 回退路径：把 [start, start + count) 的字形轮廓变换到局部坐标、追加到 <paramref name="pb"/>（调用方在当前变换下填充）。
    /// 逐字形按「'head' 包围盒 + 1 em」对设备裁剪范围 <paramref name="clip"/> 剔除：超大字号下只有少数字形可见，避免为不可见的字形展平大量曲线。
    /// </summary>
    public static void AppendOutlines(GlyphRun run, Point origin, int start, int count, in Matrix3x2 ctm, PixelRect clip, PathBuilder pb)
    {
        var face = run.Face;
        float scale = run.Size / face.Metrics.UnitsPerEm;
        var glyphMatrix = GlyphMatrix(scale, run.IsSyntheticOblique);
        float bold = run.IsSyntheticBold ? GlyphRun.GetSyntheticBoldExtent(run.Size) : 0;
        var glyphs = run.Glyphs;
        var positions = run.Positions;
        GetCullExtents(face, run.IsSyntheticOblique, run.Size, 0, out float cl, out float ct, out float cr, out float cb);
        for (int i = start; i < start + count; i++)
        {
            float x = origin.X + positions[i].X, y = origin.Y + positions[i].Y;
            if (!(Math.Abs(x) < 1e7f) || !(Math.Abs(y) < 1e7f)) continue;
            var dev = Rect.FromLTRB(x + cl, y + ct, x + cr, y + cb).TransformBounds(ctm);
            if (!(dev.Right >= clip.X - 1 && dev.Left <= clip.Right + 1 && dev.Bottom >= clip.Y - 1 && dev.Top <= clip.Bottom + 1)) continue;
            if (!face.GetOutline(glyphs[i], run.Variation, pb, glyphMatrix * Matrix3x2.CreateTranslation(x, y))) continue;
            if (bold > 0)
                foreach (var o in BoldOffsets)
                    face.GetOutline(glyphs[i], run.Variation, pb, glyphMatrix * Matrix3x2.CreateTranslation(x + o.X * bold, y + o.Y * bold));
        }
    }
}
