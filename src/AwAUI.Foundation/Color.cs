using System;
using System.Globalization;

namespace AwAUI;

/// <summary>非预乘 sRGB 颜色，分量为 0..1 的 float。与 DisplayList 规范一致（design/13）。</summary>
public readonly struct Color : IEquatable<Color>
{
    public Color(float r, float g, float b, float a = 1f) { R = r; G = g; B = b; A = a; }

    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }

    public static Color FromRgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);
    public static Color FromRgba(byte r, byte g, byte b, byte a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>解析 #rgb、#rgba、#rrggbb、#rrggbbaa。</summary>
    public static Color Parse(string hex)
    {
        if (!TryParse(hex, out var c)) throw new FormatException($"Invalid color '{hex}'.");
        return c;
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex) || hex![0] != '#') return false;
        var s = hex.Substring(1);
        if (s.Length is 3 or 4)
        {
            var e = new char[s.Length * 2];
            for (int i = 0; i < s.Length; i++) { e[2 * i] = s[i]; e[2 * i + 1] = s[i]; }
            s = new string(e);
        }
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        if (s.Length == 6) v = (v << 8) | 0xFF;
        color = FromRgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public Color WithAlpha(float a) => new(R, G, B, a);
    public bool IsOpaque => A >= 1f;
    public bool IsTransparent => A <= 0f;

    public static Color Transparent => default;
    public static Color Black => new(0, 0, 0);
    public static Color White => new(1, 1, 1);

    public static Color Lerp(Color a, Color b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    public static bool operator ==(Color a, Color b) => a.Equals(b);
    public static bool operator !=(Color a, Color b) => !a.Equals(b);

    public bool Equals(Color o) => R == o.R && G == o.G && B == o.B && A == o.A;
    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => HashCodeCompat.Combine(R.GetHashCode(), G.GetHashCode(), B.GetHashCode(), A.GetHashCode());

    public override string ToString()
    {
        static int B8(float v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}{3:x2}", B8(R), B8(G), B8(B), B8(A));
    }
}
