using System;

namespace AwAUI.Graphics.Text;

/// <summary>字重，对应 CSS <c>font-weight</c> 的数值（1–1000）。</summary>
public readonly struct FontWeight : IEquatable<FontWeight>, IComparable<FontWeight>
{
    public FontWeight(int value)
    {
        if (value < 1 || value > 1000) throw new ArgumentOutOfRangeException(nameof(value), "Font weight must be between 1 and 1000.");
        _value = value;
    }

    // default(FontWeight) 视为 Normal(400)
    readonly int _value;
    public int Value => _value == 0 ? 400 : _value;

    public static FontWeight Thin => new(100);
    public static FontWeight ExtraLight => new(200);
    public static FontWeight Light => new(300);
    public static FontWeight Normal => new(400);
    public static FontWeight Medium => new(500);
    public static FontWeight SemiBold => new(600);
    public static FontWeight Bold => new(700);
    public static FontWeight ExtraBold => new(800);
    public static FontWeight Black => new(900);

    public static implicit operator FontWeight(int value) => new(value);

    public bool Equals(FontWeight other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is FontWeight w && Equals(w);
    public override int GetHashCode() => Value;
    public int CompareTo(FontWeight other) => Value.CompareTo(other.Value);
    public static bool operator ==(FontWeight a, FontWeight b) => a.Value == b.Value;
    public static bool operator !=(FontWeight a, FontWeight b) => a.Value != b.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>字形风格，对应 CSS <c>font-style</c>。</summary>
public enum FontStyle : byte
{
    Normal,
    Italic,
    Oblique,
}

/// <summary>字宽，对应 CSS <c>font-stretch</c> 的关键字与 OpenType 'OS/2' usWidthClass（1–9）。</summary>
public enum FontStretch : byte
{
    UltraCondensed = 1,
    ExtraCondensed = 2,
    Condensed = 3,
    SemiCondensed = 4,
    Normal = 5,
    SemiExpanded = 6,
    Expanded = 7,
    ExtraExpanded = 8,
    UltraExpanded = 9,
}

/// <summary>字形轮廓的存储格式。</summary>
public enum OutlineFormat : byte
{
    /// <summary>没有矢量轮廓（例如只有位图的字体）。</summary>
    None,
    /// <summary>TrueType 'glyf'（二次贝塞尔）。</summary>
    TrueType,
    /// <summary>'CFF '（三次贝塞尔，P2 实现）。</summary>
    Cff,
    /// <summary>'CFF2'（P2 实现）。</summary>
    Cff2,
}
