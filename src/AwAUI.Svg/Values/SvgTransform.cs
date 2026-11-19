using System;
using System.Numerics;

namespace AwAUI.Svg.Values;

/// <summary>
/// transform 属性：matrix / translate / scale / rotate（可带中心）/ skewX / skewY 的列表。
/// 列表 "A B" 表示先作用 B 再作用 A；按 System.Numerics 的行向量约定合成为 M = M_B · M_A。
/// 任何语法错误或非有限值都使整个属性无效（按 SVG 2 视为没有变换）。
/// </summary>
internal static class SvgTransform
{
    public static bool TryParse(string text, out Matrix3x2 result)
    {
        result = Matrix3x2.Identity;
        var s = text.AsSpan();
        int pos = 0;
        Span<float> args = stackalloc float[6];
        var m = Matrix3x2.Identity;
        SvgNumber.SkipWsp(s, ref pos);
        while (pos < s.Length)
        {
            int nameStart = pos;
            while (pos < s.Length && (s[pos] is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) pos++;
            var name = s.Slice(nameStart, pos - nameStart);
            SvgNumber.SkipWsp(s, ref pos);
            if (pos >= s.Length || s[pos] != '(') return false;
            pos++;
            SvgNumber.SkipWsp(s, ref pos);
            int n = 0;
            while (pos < s.Length && s[pos] != ')')
            {
                if (n == 6) return false;
                if (n > 0) SvgNumber.SkipCommaWsp(s, ref pos);
                if (!SvgNumber.TryScan(s, ref pos, out float v) || !SvgNumber.IsFinite(v)) return false;
                args[n++] = v;
                SvgNumber.SkipWsp(s, ref pos);
            }
            if (pos >= s.Length) return false;
            pos++; // ')'

            Matrix3x2 f;
            if (Is(name, "matrix") && n == 6)
                f = new Matrix3x2(args[0], args[1], args[2], args[3], args[4], args[5]);
            else if (Is(name, "translate") && n is 1 or 2)
                f = Matrix3x2.CreateTranslation(args[0], n == 2 ? args[1] : 0);
            else if (Is(name, "scale") && n is 1 or 2)
                f = Matrix3x2.CreateScale(args[0], n == 2 ? args[1] : args[0]);
            else if (Is(name, "rotate") && n is 1 or 3)
            {
                float a = Radians(args[0]);
                f = n == 3 ? Rotate(a, args[1], args[2]) : Rotate(a, 0, 0);
            }
            else if (Is(name, "skewX") && n == 1)
                f = new Matrix3x2(1, 0, (float)Math.Tan(Radians(args[0])), 1, 0, 0);
            else if (Is(name, "skewY") && n == 1)
                f = new Matrix3x2(1, (float)Math.Tan(Radians(args[0])), 0, 1, 0, 0);
            else
                return false;

            m = f * m;
            SvgNumber.SkipCommaWsp(s, ref pos);
        }
        if (!IsFinite(m)) return false;
        result = m;
        return true;
    }

    static float Radians(float degrees) => (float)(degrees * Math.PI / 180.0);

    /// <summary>精确的旋转：90° 的整数倍不引入舍入误差。</summary>
    static Matrix3x2 Rotate(float radians, float cx, float cy)
    {
        double a = radians;
        double c = Math.Cos(a), s = Math.Sin(a);
        if (Math.Abs(c) < 1e-7) c = 0;
        if (Math.Abs(s) < 1e-7) s = 0;
        var r = new Matrix3x2((float)c, (float)s, (float)-s, (float)c, 0, 0);
        if (cx == 0 && cy == 0) return r;
        return Matrix3x2.CreateTranslation(-cx, -cy) * r * Matrix3x2.CreateTranslation(cx, cy);
    }

    static bool Is(ReadOnlySpan<char> name, string token) => name.SequenceEqual(token.AsSpan());

    public static bool IsFinite(in Matrix3x2 m) =>
        SvgNumber.IsFinite(m.M11) && SvgNumber.IsFinite(m.M12) && SvgNumber.IsFinite(m.M21) &&
        SvgNumber.IsFinite(m.M22) && SvgNumber.IsFinite(m.M31) && SvgNumber.IsFinite(m.M32);
}
