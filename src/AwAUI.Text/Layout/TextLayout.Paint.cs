using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Graphics.Text;

namespace AwAUI.Text;

public sealed partial class TextLayout
{
    readonly struct GlyphRunOp
    {
        public GlyphRunOp(GlyphRun run, float x, float y, Paint paint)
        {
            Run = run;
            X = x;
            Y = y;
            Paint = paint;
        }

        public GlyphRun Run { get; }
        public float X { get; }
        public float Y { get; }
        public Paint Paint { get; }
    }

    readonly struct DecorationOp
    {
        public DecorationOp(Rect rect, VectorPath? wave, float thickness, Paint paint)
        {
            Rect = rect;
            Wave = wave;
            Thickness = thickness;
            Paint = paint;
        }

        public Rect Rect { get; }
        /// <summary>波浪线（坐标相对布局原点）；为 null 时填充 <see cref="Rect"/>。</summary>
        public VectorPath? Wave { get; }
        public float Thickness { get; }
        public Paint Paint { get; }
    }

    sealed class PaintData
    {
        public GlyphRunOp[] Runs = Array.Empty<GlyphRunOp>();
        /// <summary>下划线与上划线：在文字之前绘制。</summary>
        public DecorationOp[] Under = Array.Empty<DecorationOp>();
        /// <summary>删除线：在文字之后绘制。</summary>
        public DecorationOp[] Through = Array.Empty<DecorationOp>();
    }

    /// <summary>
    /// 绘制：每行的每个 run（同一样式与字型的视觉连续片段）录制一次 <see cref="DrawingContext.DrawGlyphRun"/>；
    /// 下划线与上划线在文字之前、删除线在文字之后以矩形绘制（位置与粗细取自字体的 post / OS/2 度量）。
    /// 字形串在第一次绘制时创建并缓存到下一次 <see cref="Layout"/>。占位盒不绘制（由调用方在 <see cref="GetPlaceholderRect"/> 处绘制）。
    /// </summary>
    public void Paint(DrawingContext dc, Point origin)
    {
        if (dc is null) throw new ArgumentNullException(nameof(dc));
        EnsureLayout();
        var data = _paintData ??= BuildPaintData();
        foreach (var d in data.Under) DrawDecoration(dc, origin, d);
        foreach (var r in data.Runs) dc.DrawGlyphRun(r.Run, new Point(origin.X + r.X, origin.Y + r.Y), r.Paint);
        foreach (var d in data.Through) DrawDecoration(dc, origin, d);
    }

    static void DrawDecoration(DrawingContext dc, Point origin, in DecorationOp d)
    {
        if (d.Wave is { } wave)
        {
            dc.Save();
            dc.Translate(origin.X, origin.Y);
            dc.StrokePath(wave, d.Paint, new Stroke(d.Thickness));
            dc.Restore();
        }
        else dc.FillRect(d.Rect.Offset(origin.X, origin.Y), d.Paint);
    }

    PaintData BuildPaintData()
    {
        var runs = new List<GlyphRunOp>();
        var under = new List<DecorationOp>();
        var through = new List<DecorationOp>();
        var glyphs = new List<ushort>();
        var positions = new List<Point>();
        int decorationBudget = MaxDecorationPiecesPerLayout;
        for (int li = 0; li < _lines.Count; li++)
        {
            ref readonly var line = ref _lines[li];
            int end = line.FragStart + line.FragCount;
            for (int f = line.FragStart; f < end;)
            {
                int item = _frags[f].Item;
                int g = f + 1;
                while (g < end && _frags[g].Item == item) g++;
                int styleItem = item >= 0 ? item : line.EllipsisItem;
                if (item >= 0 && _items[item].Placeholder >= 0)
                {
                    f = g;
                    continue;
                }

                glyphs.Clear();
                positions.Clear();
                float groupX = _frags[f].X;
                float decoStart = float.NaN, decoEnd = float.NaN;
                FontFace face;
                float size;
                GlyphRunFlags synthetic;
                StyleInfo info;
                if (item >= 0)
                {
                    face = _items[item].Face!;
                    info = _styles[_items[item].Style];
                    size = info.Size;
                    synthetic = _items[item].Synthetic;
                }
                else
                {
                    var run = line.Ellipsis!;
                    face = run.Face;
                    info = styleItem >= 0 ? _styles[_items[styleItem].Style] : _strutStyle;
                    size = run.Size;
                    synthetic = styleItem >= 0 ? _items[styleItem].Synthetic : GlyphRunFlags.None;
                }

                for (int k = f; k < g; k++)
                {
                    ref readonly var fr = ref _frags[k];
                    float x = fr.X - groupX;
                    if (!fr.Hang)
                    {
                        if (float.IsNaN(decoStart)) decoStart = fr.X;
                        decoEnd = fr.X + fr.Width;
                    }
                    if (fr.Item < 0)
                    {
                        AppendGlyphs(line.Ellipsis!, 0, line.Ellipsis!.Count, x, glyphs, positions);
                        continue;
                    }
                    bool rtl = (fr.Level & 1) != 0;
                    for (int i = 0; i < fr.UnitEnd - fr.UnitStart; i++)
                    {
                        int u = rtl ? fr.UnitEnd - 1 - i : fr.UnitStart + i;
                        ref readonly var unit = ref _units[u];
                        if (_pieces[unit.Piece].Run is { } run && unit.GlyphCount > 0)
                            AppendGlyphs(run, unit.GlyphStart, unit.GlyphCount, x, glyphs, positions);
                        x += UnitWidth(line, u);
                    }
                }

                float baseline = line.Baseline;
                float originX = line.X + groupX;
                if (glyphs.Count > 0 && size > 0)
                    runs.Add(new GlyphRunOp(new GlyphRun(face, size, glyphs.ToArray(), positions.ToArray(), synthetic, info.Style.Variation),
                        originX, baseline, info.Paint));

                var deco = info.Style.Decoration;
                if (deco != TextDecorationLine.None && !float.IsNaN(decoStart) && decoEnd > decoStart)
                    AddDecorations(info, face, size, line.X + decoStart, line.X + decoEnd, baseline, under, through, ref decorationBudget);
                f = g;
            }
        }
        return new PaintData { Runs = runs.ToArray(), Under = under.ToArray(), Through = through.ToArray() };
    }

    float UnitWidth(in LineInfo line, int u)
    {
        float w = _units[u].Advance;
        if (line.JustifyExtra != 0 && u < line.HangStart && IsJustificationOpportunity(u, line.LastContentUnit)) w += line.JustifyExtra;
        return w;
    }

    static void AppendGlyphs(ShapedRun run, int start, int count, float x, List<ushort> glyphs, List<Point> positions)
    {
        var ids = run.Glyphs;
        var adv = run.Advances;
        var off = run.Offsets;
        var flags = run.Flags;
        for (int j = start; j < start + count; j++)
        {
            if ((flags[j] & GlyphFlags.Hidden) == 0)
            {
                glyphs.Add(ids[j]);
                positions.Add(new Point(x + off[j].X, off[j].Y));
            }
            x += adv[j].X;
        }
    }

    /// <summary>单个装饰线最多拆成的小段数（点线、虚线、波浪线）；超出时退化为实线，防止极细的线产生海量绘制命令。</summary>
    const int MaxDecorationPieces = 4096;

    /// <summary>
    /// 整个段落的点线、虚线、波浪线小段总数上限：单条线的上限之外再按段落限界，
    /// 否则数万行的长文本每行数千个小段会产生与文本长度成正比且系数很大的绘制命令与内存。用完后其余的线退化为实线。
    /// </summary>
    const int MaxDecorationPiecesPerLayout = 1 << 16;

    void AddDecorations(StyleInfo info, FontFace face, float size, float x0, float x1, float baseline,
        List<DecorationOp> under, List<DecorationOp> through, ref int budget)
    {
        var s = info.Style;
        var m = face.Metrics;
        float scale = m.GetScale(size);
        var paint = s.DecorationColor is { } c ? new Paint(c) : info.Paint;
        bool custom = s.DecorationThickness >= 0 && !float.IsInfinity(s.DecorationThickness);
        float Thickness(int metric)
        {
            if (custom) return Math.Min(s.DecorationThickness, size);
            float t = metric * scale;
            return t > 0 ? t : size / 16;
        }
        if ((s.Decoration & TextDecorationLine.Underline) != 0)
        {
            float t = Thickness(m.UnderlineThickness);
            AddLine(under, x0, x1, baseline - m.UnderlinePosition * scale, t, 1, s.DecorationStyle, paint, ref budget);
        }
        if ((s.Decoration & TextDecorationLine.Overline) != 0)
        {
            float t = Thickness(m.UnderlineThickness);
            AddLine(under, x0, x1, baseline - m.Ascender * scale, t, -1, s.DecorationStyle, paint, ref budget);
        }
        if ((s.Decoration & TextDecorationLine.LineThrough) != 0)
        {
            float t = Thickness(m.StrikeoutThickness);
            AddLine(through, x0, x1, baseline - m.StrikeoutPosition * scale, t, 1, s.DecorationStyle, paint, ref budget);
        }
    }

    /// <param name="top">线条顶边的 y。</param>
    /// <param name="away">双线的第二条线相对第一条的方向（+1 向下，−1 向上）。</param>
    static void AddDashes(List<DecorationOp> list, float x0, float x1, float top, float t, float dash, float period, Paint paint)
    {
        int count = (int)Math.Ceiling((x1 - x0) / period);
        for (int i = 0; i < count; i++)
        {
            float x = x0 + i * period;
            list.Add(new DecorationOp(new Rect(x, top, Math.Min(dash, x1 - x), t), null, t, paint));
        }
    }

    static void AddLine(List<DecorationOp> list, float x0, float x1, float top, float t, int away, TextDecorationStyle style, Paint paint, ref int budget)
    {
        if (!(t > 0) || float.IsInfinity(t) || float.IsNaN(top) || float.IsInfinity(top)) return;
        float length = x1 - x0;
        if (style is TextDecorationStyle.Dotted or TextDecorationStyle.Dashed or TextDecorationStyle.Wavy)
        {
            float pieces = length / (2 * t);
            if (!(pieces <= MaxDecorationPieces) || pieces + 1 > budget) style = TextDecorationStyle.Solid;
            else budget -= (int)pieces + 1;
        }
        switch (style)
        {
            case TextDecorationStyle.Double:
                list.Add(new DecorationOp(new Rect(x0, top, length, t), null, t, paint));
                list.Add(new DecorationOp(new Rect(x0, top + away * 2 * t, length, t), null, t, paint));
                break;
            case TextDecorationStyle.Dotted:
                AddDashes(list, x0, x1, top, t, t, 2 * t, paint);
                break;
            case TextDecorationStyle.Dashed:
                AddDashes(list, x0, x1, top, t, 3 * t, 5 * t, paint);
                break;
            case TextDecorationStyle.Wavy:
            {
                // 振幅 t、波长 4t 的二次曲线波浪，中心线在 top + t/2
                var b = new PathBuilder();
                float mid = top + t / 2, step = 2 * t;
                b.MoveTo(x0, mid);
                int count = (int)Math.Ceiling(length / step);
                for (int i = 0; i < count; i++)
                {
                    float x = x0 + i * step;
                    float nx = Math.Min(x + step, x1);
                    b.QuadTo(new Point((x + nx) / 2, mid + ((i & 1) == 0 ? -2 * t : 2 * t)), new Point(nx, mid));
                }
                list.Add(new DecorationOp(new Rect(x0, top - t, length, 3 * t), b.ToPath(), t, paint));
                break;
            }
            default:
                list.Add(new DecorationOp(new Rect(x0, top, length, t), null, t, paint));
                break;
        }
    }
}
