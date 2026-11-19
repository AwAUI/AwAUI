using System;

namespace AwAUI.Svg.Values;

/// <summary>
/// SVG/CSS 数字的无分配扫描器。语法：符号? 数字* (. 数字+)? ([eE] 符号? 数字+)?。
/// 「1.5.5」读作 1.5 与 .5；「1em」中的 e 不是指数。超出 float 范围得到 ±∞，由调用方判定为错误。
/// </summary>
internal static class SvgNumber
{
    static readonly double[] Pow10 = BuildPow10();

    static double[] BuildPow10()
    {
        var t = new double[309];
        double v = 1;
        for (int i = 0; i < t.Length; i++) { t[i] = v; v *= 10; }
        return t;
    }

    /// <summary>最小的正规 float。</summary>
    public const float MinNormal = 1.17549435e-38f;

    public static bool IsNumberStart(char c) => c is >= '0' and <= '9' or '.' or '-' or '+';

    public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>从 <paramref name="pos"/> 读一个数字；成功时前移 pos。值可能是 ±∞（溢出）。</summary>
    public static bool TryScan(ReadOnlySpan<char> s, ref int pos, out float value)
    {
        value = 0;
        int i = pos;
        int n = s.Length;
        bool negative = false;
        if (i < n && (s[i] == '+' || s[i] == '-')) { negative = s[i] == '-'; i++; }

        ulong mantissa = 0;
        int digits = 0;        // 计入尾数的有效数字
        int exponent = 0;      // 十进制指数修正
        bool any = false;
        while (i < n && s[i] is >= '0' and <= '9')
        {
            any = true;
            if (digits < 19) { mantissa = mantissa * 10 + (ulong)(s[i] - '0'); if (mantissa != 0) digits++; }
            else exponent++;
            i++;
        }
        if (i < n && s[i] == '.' && i + 1 < n && s[i + 1] is >= '0' and <= '9')
        {
            i++;
            while (i < n && s[i] is >= '0' and <= '9')
            {
                any = true;
                if (digits < 19) { mantissa = mantissa * 10 + (ulong)(s[i] - '0'); exponent--; if (mantissa != 0) digits++; }
                i++;
            }
        }
        else if (any && i < n && s[i] == '.')
        {
            i++; // SVG 1.1 路径文法允许「数字序列 .」，如 "M1.,2."
        }
        if (!any) return false;

        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            bool expNeg = false;
            if (j < n && (s[j] == '+' || s[j] == '-')) { expNeg = s[j] == '-'; j++; }
            if (j < n && s[j] is >= '0' and <= '9')
            {
                int e = 0;
                while (j < n && s[j] is >= '0' and <= '9')
                {
                    if (e < 100_000) e = e * 10 + (s[j] - '0');
                    j++;
                }
                exponent += expNeg ? -e : e;
                i = j;
            }
        }

        double v = mantissa;
        if (mantissa != 0 && exponent != 0)
        {
            if (exponent > 0)
                v = exponent > 308 ? double.PositiveInfinity : v * Pow10[exponent];
            else
            {
                int e = -exponent;
                if (e > 308) { v /= Pow10[308]; e -= 308; }
                v = e > 308 ? 0 : v / Pow10[e];
            }
        }
        if (negative) v = -v;
        value = (float)v;
        // 次正规数一律视为 0：没有几何意义，且会触发下游浮点运算的边界问题
        if (value is > -MinNormal and < MinNormal) value = 0;
        pos = i;
        return true;
    }

    /// <summary>跳过 SVG 空白（空格、制表、回车、换行、换页）。</summary>
    public static void SkipWsp(ReadOnlySpan<char> s, ref int pos)
    {
        while (pos < s.Length && IsWsp(s[pos])) pos++;
    }

    public static bool IsWsp(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';

    /// <summary>跳过「空白* ,? 空白*」。返回是否遇到了逗号。</summary>
    public static bool SkipCommaWsp(ReadOnlySpan<char> s, ref int pos)
    {
        SkipWsp(s, ref pos);
        if (pos < s.Length && s[pos] == ',')
        {
            pos++;
            SkipWsp(s, ref pos);
            return true;
        }
        return false;
    }

    /// <summary>整个字符串是一个有限数字（允许首尾空白）。</summary>
    public static bool TryParse(string? text, out float value)
    {
        value = 0;
        if (text == null) return false;
        var s = text.AsSpan();
        int pos = 0;
        SkipWsp(s, ref pos);
        if (!TryScan(s, ref pos, out value) || !IsFinite(value)) return false;
        SkipWsp(s, ref pos);
        return pos == s.Length;
    }

    /// <summary>数字或百分比（百分比除以 100）。用于 opacity、offset。</summary>
    public static bool TryParseNumberOrPercent(string? text, out float value)
    {
        value = 0;
        if (text == null) return false;
        var s = text.AsSpan();
        int pos = 0;
        SkipWsp(s, ref pos);
        if (!TryScan(s, ref pos, out value) || !IsFinite(value)) return false;
        if (pos < s.Length && s[pos] == '%') { value /= 100f; pos++; }
        SkipWsp(s, ref pos);
        return pos == s.Length;
    }

    /// <summary>以逗号或空白分隔的数字列表（viewBox、points）。遇到错误时返回已读部分并置 <paramref name="error"/>。</summary>
    public static float[] ParseList(string text, out bool error, int maxCount = int.MaxValue)
    {
        var s = text.AsSpan();
        var list = new System.Collections.Generic.List<float>();
        int pos = 0;
        error = false;
        SkipWsp(s, ref pos);
        while (pos < s.Length)
        {
            if (list.Count >= maxCount) { error = true; break; }
            if (!TryScan(s, ref pos, out float v) || !IsFinite(v)) { error = true; break; }
            list.Add(v);
            bool comma = SkipCommaWsp(s, ref pos);
            if (comma && pos >= s.Length) { error = true; break; }
        }
        return list.ToArray();
    }
}
