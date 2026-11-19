using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 把折线描边展开为可填充的多边形（非零规则）。
/// 开放折线生成一个闭合轮廓：左侧正向 → 末端线帽 → 右侧反向 → 起点线帽；
/// 闭合折线生成两个方向相反的环。内侧连接经过顶点本身，自交部分由非零规则正确处理。
/// </summary>
internal static class Stroker
{
    /// <summary>
    /// 一次描边最多切出的虚线段数。超出时（图案极短而可见路径极长；或没有可用的可见范围，例如变换不可逆）忽略虚线、按实线描边，
    /// 返回 false 让调用方上报降级。可见范围内的正常内容远达不到这个数（1280×880 的屏幕铺满 1px 点线约 2×10⁵ 段）。
    /// </summary>
    public const int MaxDashPieces = 1 << 18;

    /// <summary>一次描边中圆角连接与圆线帽最多生成的圆弧点数；用完后其余圆弧退化为一段弦（近似斜角）。</summary>
    public const int MaxArcPoints = 1 << 20;

    /// <param name="cull">
    /// 可选的局部坐标可见范围（已按线宽、线帽与斜接外扩）。虚线只在范围内生成：范围外的虚线段不产生几何，
    /// 只按弧长推进虚线相位（O(1)）。否则巨大的虚线 / 点线边框（如 CSS 中 10⁶px 的盒子）会生成数百万段、占用数百 MB。
    /// </param>
    /// <returns>虚线是否按要求生效（超出 <see cref="MaxDashPieces"/> 时改为实线，返回 false）。</returns>
    public static bool Stroke(List<Polyline> lines, Stroke stroke, float tolerance, PolyList output, CullRect? cull = null)
    {
        bool dashed = true;
        var pieces = lines;
        if (stroke.Dashes is { } dashes)
        {
            if (Dash(lines, dashes, stroke.DashOffset, cull) is { } d) pieces = d;
            else dashed = false;
        }
        float hw = stroke.Width * 0.5f;
        int arcBudget = MaxArcPoints;
        var pts = new List<Vector2>();
        foreach (var line in pieces)
        {
            pts.Clear();
            foreach (var p in line.Points)
                if (pts.Count == 0 || Vector2.DistanceSquared(pts[pts.Count - 1], p) > 1e-12f) pts.Add(p);
            bool closed = line.Closed;
            if (closed && pts.Count > 1 && Vector2.DistanceSquared(pts[0], pts[pts.Count - 1]) <= 1e-12f)
                pts.RemoveAt(pts.Count - 1);

            if (pts.Count == 1)
            {
                if (!closed) Dot(pts[0], hw, stroke.Cap, tolerance, output, ref arcBudget);
                continue;
            }
            if (closed && pts.Count == 2) closed = false; // 来回两点的闭合路径按开放处理
            if (closed) StrokeClosed(pts, hw, stroke, tolerance, output, ref arcBudget);
            else StrokeOpen(pts, hw, stroke, tolerance, output, ref arcBudget);
        }
        return dashed;
    }

    static Vector2 Normal(Vector2 d) => new(-d.Y, d.X);

    static void StrokeOpen(List<Vector2> p, float hw, Stroke s, float tol, PolyList o, ref int budget)
    {
        int n = p.Count;
        var d = new Vector2[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = Vector2.Normalize(p[i + 1] - p[i]);

        o.BeginContour();
        o.Add(p[0] + Normal(d[0]) * hw);
        for (int k = 1; k < n - 1; k++) Join(o, p[k], d[k - 1], d[k], hw, s, tol, ref budget);
        Cap(o, p[n - 1], d[n - 2], hw, s.Cap, tol, ref budget);
        for (int k = n - 2; k >= 1; k--) Join(o, p[k], -d[k], -d[k - 1], hw, s, tol, ref budget);
        Cap(o, p[0], -d[0], hw, s.Cap, tol, ref budget);
    }

    static void StrokeClosed(List<Vector2> p, float hw, Stroke s, float tol, PolyList o, ref int budget)
    {
        int n = p.Count;
        var d = new Vector2[n];
        for (int i = 0; i < n; i++) d[i] = Vector2.Normalize(p[(i + 1) % n] - p[i]);

        o.BeginContour();
        for (int k = 0; k < n; k++) Join(o, p[k], d[(k + n - 1) % n], d[k], hw, s, tol, ref budget);
        o.BeginContour();
        for (int k = n - 1; k >= 0; k--) Join(o, p[k], -d[k], -d[(k + n - 1) % n], hw, s, tol, ref budget);
    }

    /// <summary>在顶点 p 处、沿 +法线一侧，从入射边连接到出射边。</summary>
    static void Join(PolyList o, Vector2 p, Vector2 dIn, Vector2 dOut, float hw, Stroke s, float tol, ref int budget)
    {
        var nIn = Normal(dIn) * hw;
        var nOut = Normal(dOut) * hw;
        float cross = dIn.X * dOut.Y - dIn.Y * dOut.X;
        float dot = Vector2.Dot(dIn, dOut);

        if (MathF.Abs(cross) < 1e-6f && dot > 0)
        {
            o.Add(p + nIn); // 共线
            return;
        }
        if (cross > 0)
        {
            // +法线一侧是内侧：经过顶点，自交部分由非零规则吸收
            o.Add(p + nIn);
            o.Add(p);
            o.Add(p + nOut);
            return;
        }

        o.Add(p + nIn);
        switch (s.Join)
        {
            case StrokeJoin.Miter:
            {
                float sinHalf = MathF.Sqrt(MathF.Max(0, (1 - dot) * 0.5f)); // 夹角一半的正弦
                if (sinHalf > 1e-6f)
                {
                    float ratio = 1 / sinHalf;
                    if (ratio <= s.MiterLimit)
                    {
                        var bis = nIn + nOut;
                        if (bis.LengthSquared() > 1e-12f) o.Add(p + Vector2.Normalize(bis) * hw * ratio);
                    }
                }
                break;
            }
            case StrokeJoin.Round:
            {
                float a0 = MathF.Atan2(nIn.Y, nIn.X);
                float sweep;
                if (MathF.Abs(cross) < 1e-6f) sweep = -MathF.PI; // 180° 掉头：绕过前进方向
                else
                {
                    sweep = MathF.Atan2(nOut.Y, nOut.X) - a0;
                    while (sweep > MathF.PI) sweep -= 2 * MathF.PI;
                    while (sweep <= -MathF.PI) sweep += 2 * MathF.PI;
                }
                Arc(o, p, hw, a0, sweep, tol, includeEnds: false, ref budget);
                break;
            }
        }
        o.Add(p + nOut);
    }

    /// <summary>线帽：从 p + n 绕到 p - n，d 为指向线外的方向。</summary>
    static void Cap(PolyList o, Vector2 p, Vector2 d, float hw, StrokeCap cap, float tol, ref int budget)
    {
        var n = Normal(d) * hw;
        o.Add(p + n);
        switch (cap)
        {
            case StrokeCap.Square:
                o.Add(p + n + d * hw);
                o.Add(p - n + d * hw);
                break;
            case StrokeCap.Round:
                Arc(o, p, hw, MathF.Atan2(n.Y, n.X), -MathF.PI, tol, includeEnds: false, ref budget);
                break;
        }
        o.Add(p - n);
    }

    static void Dot(Vector2 p, float hw, StrokeCap cap, float tol, PolyList o, ref int budget)
    {
        switch (cap)
        {
            case StrokeCap.Round:
                o.BeginContour();
                Arc(o, p, hw, 0, 2 * MathF.PI, tol, includeEnds: true, ref budget);
                break;
            case StrokeCap.Square:
                o.BeginContour();
                o.Add(p + new Vector2(-hw, -hw));
                o.Add(p + new Vector2(hw, -hw));
                o.Add(p + new Vector2(hw, hw));
                o.Add(p + new Vector2(-hw, hw));
                break;
        }
    }

    static void Arc(PolyList o, Vector2 c, float r, float start, float sweep, float tol, bool includeEnds, ref int budget)
    {
        // 每段弦的最大误差 r(1 - cos(θ/2)) ≤ tol
        float step = 2 * MathF.Acos(MathF.Max(-1f, MathF.Min(1f, 1 - tol / MathF.Max(r, 1e-6f))));
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / MathF.Max(step, 1e-3f)));
        n = Math.Min(n, 512);
        // 整圆（点）至少 3 段才是一个面；其余用完预算后为 1 段
        int min = includeEnds ? 3 : 1;
        if (n > min && n - min > budget) n = Math.Max(min, budget + min);
        budget = Math.Max(0, budget - (n - min));
        int from = includeEnds ? 0 : 1, to = includeEnds ? n : n - 1;
        for (int i = from; i <= to; i++)
        {
            float a = start + sweep * i / n;
            o.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
        }
    }

    /// <summary>
    /// 按虚线模式把折线切成若干开放折线。闭合折线首尾相接的两段会被合并。
    /// 给出 <paramref name="cull"/> 时只为落在范围内的部分生成虚线段：每条线段先求出与范围相交的弧长区间，区间之外按弧长
    /// 直接推进虚线状态（取模，O(模式长度)），所以耗时与内存只与可见的虚线段数成正比。
    /// 段数超过 <see cref="MaxDashPieces"/> 时返回 null（调用方按实线描边）。
    /// </summary>
    internal static List<Polyline>? Dash(List<Polyline> lines, IReadOnlyList<float> pattern, float offset, CullRect? cull = null)
    {
        float total = 0;
        foreach (var d in pattern) total += d;
        var result = new List<Polyline>();
        // 构造 Stroke 时模式已规范为偶数项、总长为正，所以一个周期后开 / 关状态复原
        int count = pattern.Count;

        foreach (var line in lines)
        {
            var pts = new List<Vector2>(line.Points);
            if (line.Closed && pts.Count > 1) pts.Add(pts[0]);
            if (pts.Count == 0) continue;

            // 定位起始的虚线段
            float phase = offset % total;
            if (phase < 0) phase += total;
            int idx = 0;
            while (phase >= pattern[idx]) { phase -= pattern[idx]; idx = (idx + 1) % count; }
            float remaining = pattern[idx] - phase;
            bool on = idx % 2 == 0;

            int firstIndex = result.Count;
            bool startedOn = on;
            // 第一段虚线从起点开始（起点可见或没有裁剪），闭合路径的首尾合并才有意义
            bool firstAtStart = false;
            Polyline? cur = null;
            if (on && (cull is null || Inside(pts[0], cull.Value)))
            {
                if (result.Count >= MaxDashPieces) return null;
                cur = new Polyline();
                cur.Points.Add(pts[0]);
                result.Add(cur);
                firstAtStart = true;
            }

            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                // 弧长用 double：10⁹ 量级的坐标上 float 的 pos += remaining 可能不再前进（死循环），插值也会失去精度
                double segLen = Math.Sqrt((double)(b.X - a.X) * (b.X - a.X) + (double)(b.Y - a.Y) * (b.Y - a.Y));
                double s0 = 0, s1 = segLen;
                if (cull is { } c && !ClipSegment(a, b, c, out s0, out s1))
                {
                    cur = null;
                    Advance(pattern, total, ref idx, ref remaining, ref on, segLen);
                    continue;
                }
                if (s0 > 0)
                {
                    // 线段开头不可见：跳过，在可见起点处重新开始
                    cur = null;
                    Advance(pattern, total, ref idx, ref remaining, ref on, s0);
                }
                if (on && cur is null)
                {
                    if (result.Count >= MaxDashPieces) return null;
                    cur = new Polyline();
                    cur.Points.Add(segLen > 0 ? Lerp(a, b, s0 / segLen) : a);
                    result.Add(cur);
                }
                double pos = s0;
                while (s1 - pos > remaining)
                {
                    if (result.Count >= MaxDashPieces) return null;
                    pos += remaining;
                    var q = Lerp(a, b, pos / segLen);
                    if (on) cur!.Points.Add(q);
                    else { cur = new Polyline(); cur.Points.Add(q); result.Add(cur); }
                    on = !on;
                    idx = (idx + 1) % count;
                    remaining = pattern[idx];
                }
                remaining -= (float)(s1 - pos);
                if (s1 < segLen)
                {
                    if (on) cur!.Points.Add(Lerp(a, b, s1 / segLen));
                    cur = null;
                    Advance(pattern, total, ref idx, ref remaining, ref on, segLen - s1);
                }
                else if (on) cur!.Points.Add(b);
            }

            // 闭合路径：结尾的「开」段与开头的「开」段在起点处相接，合并成一段
            if (line.Closed && on && startedOn && firstAtStart && cur is not null && result.Count - firstIndex >= 2
                && !ReferenceEquals(cur, result[firstIndex]))
            {
                var first = result[firstIndex];
                cur.Points.AddRange(first.Points.GetRange(1, first.Points.Count - 1));
                result.RemoveAt(firstIndex);
            }
        }
        return result;
    }

    /// <summary>不生成几何、沿弧长推进虚线状态 <paramref name="distance"/>。整周期取模，循环次数不超过模式长度的两倍。</summary>
    static void Advance(IReadOnlyList<float> pattern, float total, ref int idx, ref float remaining, ref bool on, double distance)
    {
        if (!(distance > 0)) return;
        if (distance < remaining)
        {
            remaining = (float)(remaining - distance);
            return;
        }
        distance -= remaining;
        Step(pattern, ref idx, ref remaining, ref on);
        if (distance >= total) distance %= total;
        if (double.IsNaN(distance)) distance = 0;
        for (int guard = 0; distance >= remaining && guard < 2 * pattern.Count + 2; guard++)
        {
            distance -= remaining;
            Step(pattern, ref idx, ref remaining, ref on);
        }
        remaining = (float)Math.Max(0, remaining - distance);
    }

    static void Step(IReadOnlyList<float> pattern, ref int idx, ref float remaining, ref bool on)
    {
        on = !on;
        idx = (idx + 1) % pattern.Count;
        remaining = pattern[idx];
    }

    /// <summary>双精度插值（端点相距 10⁹ 时 float 的 a + (b − a)·t 会因相消而偏差数十个单位）。</summary>
    static Vector2 Lerp(Vector2 a, Vector2 b, double t) =>
        new((float)(a.X + (b.X - (double)a.X) * t), (float)(a.Y + (b.Y - (double)a.Y) * t));

    static bool Inside(Vector2 p, in CullRect c) => c.Contains(p);

    /// <summary>线段 a→b 与矩形相交部分的弧长区间 [s0, s1]（Liang–Barsky）；不相交返回 false。</summary>
    static bool ClipSegment(Vector2 a, Vector2 b, in CullRect c, out double s0, out double s1)
    {
        double len = Math.Sqrt((double)(b.X - a.X) * (b.X - a.X) + (double)(b.Y - a.Y) * (b.Y - a.Y));
        float t0 = 0, t1 = 1;
        var d = b - a;
        if (!Clip(-d.X, a.X - c.Left, ref t0, ref t1) || !Clip(d.X, c.Right - a.X, ref t0, ref t1)
            || !Clip(-d.Y, a.Y - c.Top, ref t0, ref t1) || !Clip(d.Y, c.Bottom - a.Y, ref t0, ref t1))
        {
            s0 = s1 = 0;
            return false;
        }
        s0 = t0 * len;
        s1 = t1 * len;
        return true;
    }

    static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        float r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }
}
