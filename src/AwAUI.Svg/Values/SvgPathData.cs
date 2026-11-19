using System;
using AwAUI.Graphics;

namespace AwAUI.Svg.Values;

/// <summary>
/// SVG 路径数据（SVG 2 §9.3 的完整文法）：M L H V C S Q T A Z 的绝对与相对形式、隐式重复命令
/// （M 之后的坐标对视为 L）、无分隔符的数字与弧线标志（"a1 1 0 00.5.5"）。
/// 出错时按规范保留出错之前完整解析的段，并返回错误描述。
/// </summary>
internal static class SvgPathData
{
    /// <summary>解析到 <paramref name="builder"/>。<paramref name="budget"/> 为剩余可用的段数，耗尽时停止并报告。</summary>
    /// <returns>null 表示成功；否则为错误描述（已解析的部分仍保留在 builder 中）。</returns>
    public static string? Parse(ReadOnlySpan<char> d, PathBuilder builder, ref int budget)
    {
        int pos = 0;
        SvgNumber.SkipWsp(d, ref pos);
        if (pos >= d.Length) return null;

        double cx = 0, cy = 0;          // 当前点
        double sx = 0, sy = 0;          // 子路径起点
        double lcx = 0, lcy = 0;        // 上一个控制点（用于 S/T 的反射）
        char prev = '\0';               // 上一个命令（已规范化为大写）
        char cmd = '\0';
        Span<double> a = stackalloc double[7];

        while (true)
        {
            SvgNumber.SkipWsp(d, ref pos);
            if (pos >= d.Length) return null;
            char c = d[pos];
            if (IsCommand(c))
            {
                cmd = c;
                pos++;
            }
            else if (SvgNumber.IsNumberStart(c) && cmd != '\0' && cmd is not ('Z' or 'z'))
            {
                // 隐式重复：M 之后是 L，m 之后是 l
                if (cmd == 'M') cmd = 'L';
                else if (cmd == 'm') cmd = 'l';
            }
            else
            {
                return Describe("Unexpected character", c, pos);
            }

            if (prev == '\0' && cmd is not ('M' or 'm')) return "Path data must begin with a moveto command.";

            char upper = char.ToUpperInvariant(cmd);
            bool rel = cmd != upper;
            if (upper == 'Z')
            {
                if (--budget < 0) return "Path segment limit exceeded.";
                builder.Close();
                cx = sx; cy = sy;
                prev = 'Z';
                continue;
            }

            int argc = upper switch { 'M' or 'L' or 'T' => 2, 'H' or 'V' => 1, 'C' => 6, 'S' or 'Q' => 4, 'A' => 7, _ => 0 };
            // 读第一组参数；之后的同类参数组在循环里处理
            bool firstSet = true;
            while (true)
            {
                if (!firstSet)
                {
                    int save = pos;
                    bool comma = SvgNumber.SkipCommaWsp(d, ref pos);
                    if (pos >= d.Length || !SvgNumber.IsNumberStart(d[pos]))
                    {
                        if (comma) return "Unexpected comma.";
                        pos = save;
                        break;
                    }
                    if (cmd == 'M') cmd = 'L';
                    else if (cmd == 'm') cmd = 'l';
                    upper = char.ToUpperInvariant(cmd);
                }
                else
                {
                    SvgNumber.SkipWsp(d, ref pos);
                }

                for (int i = 0; i < argc; i++)
                {
                    if (i > 0) SvgNumber.SkipCommaWsp(d, ref pos);
                    if (upper == 'A' && i is 3 or 4)
                    {
                        if (pos >= d.Length || d[pos] is not ('0' or '1')) return "Invalid arc flag.";
                        a[i] = d[pos] - '0';
                        pos++;
                        continue;
                    }
                    if (!SvgNumber.TryScan(d, ref pos, out float v))
                        return pos < d.Length ? Describe("Expected a number", d[pos], pos) : "Unexpected end of path data.";
                    if (!SvgNumber.IsFinite(v)) return "Number out of range.";
                    a[i] = v;
                }

                if (--budget < 0) return "Path segment limit exceeded.";
                double ox = rel ? cx : 0, oy = rel ? cy : 0;
                switch (upper)
                {
                    case 'M':
                        cx = sx = a[0] + ox; cy = sy = a[1] + oy;
                        builder.MoveTo(P(cx, cy));
                        break;
                    case 'L':
                        cx = a[0] + ox; cy = a[1] + oy;
                        builder.LineTo(P(cx, cy));
                        break;
                    case 'H':
                        cx = a[0] + ox;
                        builder.LineTo(P(cx, cy));
                        break;
                    case 'V':
                        cy = a[0] + (rel ? cy : 0);
                        builder.LineTo(P(cx, cy));
                        break;
                    case 'C':
                        lcx = a[2] + ox; lcy = a[3] + oy;
                        builder.CubicTo(P(a[0] + ox, a[1] + oy), P(lcx, lcy), P(a[4] + ox, a[5] + oy));
                        cx = a[4] + ox; cy = a[5] + oy;
                        break;
                    case 'S':
                    {
                        double c1x = cx, c1y = cy;
                        if (prev is 'C' or 'S') { c1x = 2 * cx - lcx; c1y = 2 * cy - lcy; }
                        lcx = a[0] + ox; lcy = a[1] + oy;
                        builder.CubicTo(P(c1x, c1y), P(lcx, lcy), P(a[2] + ox, a[3] + oy));
                        cx = a[2] + ox; cy = a[3] + oy;
                        break;
                    }
                    case 'Q':
                        lcx = a[0] + ox; lcy = a[1] + oy;
                        builder.QuadTo(P(lcx, lcy), P(a[2] + ox, a[3] + oy));
                        cx = a[2] + ox; cy = a[3] + oy;
                        break;
                    case 'T':
                    {
                        double qx = cx, qy = cy;
                        if (prev is 'Q' or 'T') { qx = 2 * cx - lcx; qy = 2 * cy - lcy; }
                        lcx = qx; lcy = qy;
                        builder.QuadTo(P(qx, qy), P(a[0] + ox, a[1] + oy));
                        cx = a[0] + ox; cy = a[1] + oy;
                        break;
                    }
                    case 'A':
                    {
                        double ex = a[5] + ox, ey = a[6] + oy;
                        budget -= AddArc(builder, cx, cy, a[0], a[1], a[2], a[3] != 0, a[4] != 0, ex, ey);
                        cx = ex; cy = ey;
                        break;
                    }
                }
                prev = upper;
                firstSet = false;
            }
        }
    }

    static bool IsCommand(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's'
        or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    static Point P(double x, double y) => new(Flush((float)x), Flush((float)y));

    static float Flush(float v) => v is > -SvgNumber.MinNormal and < SvgNumber.MinNormal ? 0 : v;

    static string Describe(string what, char c, int pos) =>
        what + " '" + c + "' at offset " + pos.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";

    /// <summary>
    /// 端点参数化的椭圆弧 → 三次贝塞尔（SVG 2 附录 B.2.4/B.2.5：半径修正 + 中心参数化，每段不超过 90°）。
    /// 返回额外消耗的段数。
    /// </summary>
    internal static int AddArc(PathBuilder b, double x1, double y1, double rx, double ry, double angleDeg,
        bool largeArc, bool sweep, double x2, double y2)
    {
        if (x1 == x2 && y1 == y2) return 0; // 端点重合：省略该段
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            b.LineTo(P(x2, y2));
            return 0;
        }
        double phi = angleDeg % 360 * Math.PI / 180;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);
        double dx2 = (x1 - x2) / 2, dy2 = (y1 - y2) / 2;
        double x1p = cosPhi * dx2 + sinPhi * dy2;
        double y1p = -sinPhi * dx2 + cosPhi * dy2;

        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            double sl = Math.Sqrt(lambda);
            rx *= sl;
            ry *= sl;
        }
        double rx2 = rx * rx, ry2 = ry * ry;
        double num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        double den = rx2 * y1p * y1p + ry2 * x1p * x1p;
        double coef = den > 0 ? Math.Sqrt(Math.Max(0, num / den)) : 0;
        if (largeArc == sweep) coef = -coef;
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * -(ry * x1p / rx);
        double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2;
        double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2;

        double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
        double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
        double theta1 = Math.Atan2(uy, ux);
        double dtheta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        if (!sweep && dtheta > 0) dtheta -= 2 * Math.PI;
        else if (sweep && dtheta < 0) dtheta += 2 * Math.PI;

        if (double.IsNaN(cx) || double.IsNaN(cy) || double.IsNaN(dtheta) || double.IsInfinity(cx) || double.IsInfinity(cy))
        {
            b.LineTo(P(x2, y2));
            return 0;
        }

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(dtheta) / (Math.PI / 2) - 1e-7));
        double step = dtheta / segments;
        double t = 4.0 / 3.0 * Math.Tan(step / 4);
        double a0 = theta1;
        double cos0 = Math.Cos(a0), sin0 = Math.Sin(a0);
        for (int i = 0; i < segments; i++)
        {
            double a1 = a0 + step;
            double cos1 = Math.Cos(a1), sin1 = Math.Sin(a1);
            // 单位圆上的控制点，再按半径缩放、旋转 phi、平移到中心
            double e1x = cos0 - t * sin0, e1y = sin0 + t * cos0;
            double e2x = cos1 + t * sin1, e2y = sin1 - t * cos1;
            double ex = cos1, ey = sin1;
            var c1 = Map(e1x, e1y);
            var c2 = Map(e2x, e2y);
            var end = i == segments - 1 ? P(x2, y2) : Map(ex, ey);
            b.CubicTo(c1, c2, end);
            a0 = a1;
            cos0 = cos1;
            sin0 = sin1;
        }
        return segments - 1;

        Point Map(double ux0, double uy0)
        {
            double px = ux0 * rx, py = uy0 * ry;
            return P(cosPhi * px - sinPhi * py + cx, sinPhi * px + cosPhi * py + cy);
        }
    }
}
