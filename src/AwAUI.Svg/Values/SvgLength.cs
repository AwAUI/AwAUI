using System;

namespace AwAUI.Svg.Values;

internal enum SvgUnit : byte { None, Px, Pt, Pc, Mm, Cm, In, Q, Em, Ex, Rem, Percent }

/// <summary>百分比的参照方向：宽、高或对角线（√((w²+h²)/2)）。</summary>
internal enum LengthAxis : byte { X, Y, Other }

/// <summary>带单位的长度。用户单位与 px 相同（96 dpi）。</summary>
internal readonly struct SvgLength
{
    public SvgLength(float value, SvgUnit unit) { Value = value; Unit = unit; }

    public float Value { get; }
    public SvgUnit Unit { get; }
    public bool IsPercent => Unit == SvgUnit.Percent;

    public static bool TryParse(string? text, out SvgLength length)
    {
        length = default;
        if (text == null) return false;
        var s = text.AsSpan();
        int pos = 0;
        SvgNumber.SkipWsp(s, ref pos);
        if (!TryScan(s, ref pos, out length)) return false;
        SvgNumber.SkipWsp(s, ref pos);
        return pos == s.Length;
    }

    /// <summary>读一个长度（数字 + 可选单位）。</summary>
    public static bool TryScan(ReadOnlySpan<char> s, ref int pos, out SvgLength length)
    {
        length = default;
        int p = pos;
        if (!SvgNumber.TryScan(s, ref p, out float v) || !SvgNumber.IsFinite(v)) return false;
        var unit = SvgUnit.None;
        if (p < s.Length && s[p] == '%') { unit = SvgUnit.Percent; p++; }
        else
        {
            int u = p;
            while (u < s.Length && (s[u] is >= 'a' and <= 'z' or >= 'A' and <= 'Z')) u++;
            if (u > p)
            {
                var name = s.Slice(p, u - p);
                unit = ParseUnit(name);
                if (unit == SvgUnit.None) return false;
                p = u;
            }
        }
        length = new SvgLength(v, unit);
        pos = p;
        return true;
    }

    static SvgUnit ParseUnit(ReadOnlySpan<char> name)
    {
        if (SvgColor.EqualsIgnoreCase(name, "px")) return SvgUnit.Px;
        if (SvgColor.EqualsIgnoreCase(name, "pt")) return SvgUnit.Pt;
        if (SvgColor.EqualsIgnoreCase(name, "pc")) return SvgUnit.Pc;
        if (SvgColor.EqualsIgnoreCase(name, "mm")) return SvgUnit.Mm;
        if (SvgColor.EqualsIgnoreCase(name, "cm")) return SvgUnit.Cm;
        if (SvgColor.EqualsIgnoreCase(name, "in")) return SvgUnit.In;
        if (SvgColor.EqualsIgnoreCase(name, "q")) return SvgUnit.Q;
        if (SvgColor.EqualsIgnoreCase(name, "em")) return SvgUnit.Em;
        if (SvgColor.EqualsIgnoreCase(name, "ex")) return SvgUnit.Ex;
        if (SvgColor.EqualsIgnoreCase(name, "rem")) return SvgUnit.Rem;
        return SvgUnit.None;
    }

    /// <summary>转换为用户单位。<paramref name="viewport"/> 是最近视口的尺寸，用于百分比。</summary>
    public float Resolve(Size viewport, LengthAxis axis, float fontSize)
    {
        float r = Unit switch
        {
            SvgUnit.None or SvgUnit.Px => Value,
            SvgUnit.Pt => Value * 96f / 72f,
            SvgUnit.Pc => Value * 16f,
            SvgUnit.Mm => Value * 96f / 25.4f,
            SvgUnit.Cm => Value * 96f / 2.54f,
            SvgUnit.In => Value * 96f,
            SvgUnit.Q => Value * 96f / 101.6f,
            SvgUnit.Em => Value * fontSize,
            SvgUnit.Ex => Value * fontSize * 0.5f,
            SvgUnit.Rem => Value * 16f,
            SvgUnit.Percent => Value / 100f * axis switch
            {
                LengthAxis.X => viewport.Width,
                LengthAxis.Y => viewport.Height,
                _ => (float)Math.Sqrt((viewport.Width * (double)viewport.Width + viewport.Height * (double)viewport.Height) / 2),
            },
            _ => Value,
        };
        return SvgNumber.IsFinite(r) ? r : 0;
    }

    /// <summary>是否是绝对长度（可以不依赖视口确定固有尺寸）。</summary>
    public bool IsAbsolute => Unit is not (SvgUnit.Percent or SvgUnit.Em or SvgUnit.Ex or SvgUnit.Rem);
}
